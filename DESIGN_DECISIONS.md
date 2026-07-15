# Design Decisions

Local notes on deliberate architectural/implementation choices and why. Not part of the public repo (see `.gitignore`) until we decide to publish it.

## Floating-point precision: float vs double

**Decision:** DSP/decode hot path uses `float`/`MathF` throughout (PR #67). `double` is kept only in a handful of non-hot-path spots where precision genuinely matters or the BCL forces it. Do not blanket-convert either direction without checking which category a given site falls into.

**Why:** Two failure modes were hit and fixed independently, and they pull in opposite directions:
- Per-sample DSP math (MDCT twiddle factors, Floor0 bark/wdel maps, Codebook lookup) in `double` was a measurable perf cost (implicit float↔double conversions in the hot path) — fixed by switching to `MathF` (#67). Precision loss from this is ≤6e-8/sample, inaudible.
- Large-value math (bisection seek interpolation over `long` granule positions in `StreamPageReader.cs`, bitrate calc over accumulated `long` bit counts in `StreamStats.cs`) needs `double`: `float`'s 24-bit mantissa (~16M) is not enough to represent granule positions on long files without silently corrupting seek-target math. This is the same class of bug the earlier MDCT double-precision fix (#63, later partially superseded by #67) was addressing before the hot-path/non-hot-path distinction was drawn clearly.

**Current double usages (audited 2026-07-10, all judged correct, left as-is):**
| File:line | Use | Why double is correct here |
|---|---|---|
| `StreamDecoder.cs:784` | `TimeSpan.FromSeconds((double)TotalSamples / _sampleRate)` | `TimeSpan.FromSeconds` has no float overload — BCL constraint, not a choice |
| `StreamDecoder.cs:796` | `TimeSpan.FromSeconds((double)_currentPosition / _sampleRate)` | same |
| `StreamStats.cs:33` | bitrate calc, `(double)bits / samples * sampleRate` | called once per stats query (not per-sample); avoids precision loss on large accumulated `long bits` |
| `StreamStats.cs:45` | same pattern, second stat | same |
| `Ogg/StreamPageReader.cs:251` | bisection seek interpolation: `(granulePos - lowGranulePos) / (double)(highGranulePos - lowGranulePos)` | dividing two `long` granule positions; `float` would lose precision on files with granule positions beyond ~16M, silently corrupting seek targets |
| `Ogg/StreamPageReader.cs:243-279` (`FindPageBisection`) | same interpolation, was originally `float` (commit `6c7647d`) | proportional-probe estimate assuming roughly uniform page sizes; the `float` version occasionally landed on the wrong page on large files — narrowly scoped fix, only the ratio, not the surrounding bisection logic |

**How to apply:** When touching floating-point code in this repo:
- If it's per-sample/per-packet decode math → `float`/`MathF`, no exceptions, that's the established hot-path convention.
- If it involves dividing/interpolating over `long` values that can exceed ~16M (sample counts, granule positions, byte offsets on large files) → `double`, and say why in a comment if it's not obvious.
- If it's a one-off BCL API call that only accepts `double` (e.g. `TimeSpan.FromSeconds`) → `double`, no choice, don't fight the API.

## Test coverage: Floor0 deprioritized (then closed via synthetic smoke test)

**Update 2026-07-10:** Added `Floor0Tests` synthetic Init/Unpack/Apply smoke test (branch `test/floor0-smoke`, commit 9c19255) — hand-built minimal floor0 header/data packets against a fake `ICodebook`, no real floor0-encoded file needed (none found, per research below). Coverage: `Floor0` 9.5% → 94.1%, `Floor0.Data` 0% → 100%. Full suite 272/272 passing. Not merged/pushed yet.

**Original decision (kept for context):** `Floor0`/`Floor0.Data` sit at 9.5%/0% line coverage (vs. `Floor1` at 96.7%) as of the 2026-07-10 coverage audit (dotnet-coverage + ReportGenerator, see below). Deliberately deprioritized rather than chased to parity with Floor1.

**Why:** Per the Vorbis I spec's own introduction, no known encoder past Xiph.Org's reference encoder's beta 4 has ever used floor type 0 — Floor1 decodes cheaper and behaves more stably in coupled-stereo/high-bitrate modes, and became the universal encoder choice almost immediately after Vorbis's early beta days (~2000). Every real-world `.ogg` file from any encoder, any era after beta 4, uses Floor1. `Floor0` is decode-completeness code for a spec path with ~zero practical exposure, not a path any real file will exercise. Chasing full coverage there is effort spent on the least-likely-to-matter code in the decoder. This is corroborated directly in-source: `Floor0.cs:8` — *"Packed LSP values on dB amplittude and Bark frequency scale. Virtually unused (libvorbis did not use past beta 4). Probably untested."*

**How to apply:** Don't block a release on `Floor0` coverage. If/when addressed, prefer a **synthetic smoke test** (hand-built minimal Vorbis stream using floor type 0, asserting `Apply`/decode doesn't crash or produce NaN/Inf on valid-but-unusual input) over chasing branch-by-branch parity with `Floor1` — the goal is "doesn't blow up if someone crafts a floor0 file," not deep correctness validation of a code path nothing in the wild emits.

Sources checked 2026-07-10: [xiph/vorbis doc/01-introduction.tex](https://github.com/xiph/vorbis/blob/master/doc/01-introduction.tex), [Vorbis I specification](https://xiph.org/vorbis/doc/Vorbis_I_spec.html).

## Test coverage: StreamDecoder malformed-input paths closed

**2026-07-10:** Added `StreamDecoderTests` (branch `test/floor0-smoke`, commit dd9f52e) — hand-built minimal stream/comments header packets against a fake `IPacketProvider` to drive `StreamDecoder`'s constructor down its non-Vorbis-codec detection (`GetInvalidStreamException`'s OPUS/FLAC/Speex/Skeleton/Theora/generic sniffing) and header-validation-failure branches, none of which any real `.ogg` fixture reaches (every fixture is a valid stream). Also covers the public single-arg constructor (previously only the internal 2-arg one was exercised via `VorbisReader`), disposed-instance access to `TotalSamples`/`TotalTime`, and `SeekTo` with an invalid `SeekOrigin`. `StreamDecoder` coverage: 79.6% → 91.4%. Full suite 288/288 passing.

**2026-07-12 update:** `StreamHeaderBlockSizeTests` (added on branch `fix/mdct-small-block-and-perf`, hardening the header-stage rejection that backs the MDCT small-block fix below) was merged into this same file rather than kept standalone — both hand-built minimal Vorbis header packets via identical `BitWriter`/`ByteArrayPacket`/`FakeHeaderPacketProvider`/`ConstructAndCapture` helpers. `ValidStreamHeader` is now derived from the shared `StreamHeaderWithBlockSizes(block0Exp, block1Exp)` helper instead of duplicating the same header-byte layout a second time. **How to apply:** new hand-built-header test files for `StreamDecoder` should extend this file rather than starting a new one with its own copy of the `BitWriter`/`ByteArrayPacket`/`FakeHeaderPacketProvider` scaffolding — that scaffolding has already drifted into duplication once.

**Remaining gap (deliberately not chased):** resync/corrupt-packet-header/failed-mode-decode branches in the decode loop, and the seek pre-roll failure paths, all require a fully valid *decodable* synthetic Vorbis stream (real codebooks/floor/residue/mapping/mode configs) to reach — synthesizing that is equivalent to writing a minimal encoder. Same wall `Floor0` hit before its smoke test, except there the fix was tractable because `Floor0.Apply` only needed fake LSP coefficients, not a full audio pipeline. Also skipped: the 3-line bitrate-averaging fallback in `LoadStreamHeader` (trivial arithmetic, low risk) and the deprecated `Read(Span,int,int)` overload (obsolete shim). If ever pursued, look for a purpose-built minimal-encoder test helper rather than hand-rolling one inline.

## Fixed defect: `Ogg.ContainerReader.GetStreams()` threw on a collected weak reference

**Found 2026-07-11** while closing the `ContainerReader` coverage gap (branch `test/floor0-smoke`). Initially left unfixed on purpose (test-only, to keep coverage-hardening commits free of behavior changes). **Fixed 2026-07-11**, same branch: user asked to debug/fix and update the pinning test.

```csharp
for (var i = 0; i < _packetProviders.Count; i++)
{
    if (_packetProviders[i].TryGetTarget(out var pp))
    {
        list.Add(pp);
    }
    else
    {
        list.RemoveAt(i);   // BUG: removes from the *output* list by the *source* list's index
        --i;
    }
}
```

`list` only contains entries that were successfully added (`TryGetTarget` true), so its `Count` is always `<= i` at the point a dead entry is hit. `list.RemoveAt(i)` therefore either throws `ArgumentOutOfRangeException` (typical case) or, worse, silently removes the wrong live entry from the output if `list.Count > i` can't happen here but could in a reordered variant — as written it's always an out-of-range throw. It looks like copy/paste from a pattern meant to prune the *backing* `_packetProviders` list in place, but it prunes `list` instead.

**Impact (before fix):** `GetStreams()` threw instead of returning the still-live streams whenever any previously-returned `IPacketProvider` had been garbage collected. Low real-world exposure — callers generally hold onto providers they care about — but it was a genuine correctness bug, not a hypothetical.

**Fix applied:** changed the cleanup branch to prune the dead entry from `_packetProviders` (the source list), not `list` (the output list) — `_packetProviders.RemoveAt(i); --i;`.

**Test:** `ContainerReaderTests.GetStreams_CollectedWeakReference_IsSilentlyDropped` (`NVorbis.Tests/ContainerReaderTests.cs`, formerly `..._ThrowsDueToIndexMismatchBug`) now asserts the correct behavior: a dead weakref is dropped silently, a live one alongside it is still returned, and `_packetProviders` itself shrinks by the dead entry.

## Test coverage: StreamPageReader closed to its practical ceiling (95.9%)

**2026-07-11:** Added `StreamPageReaderCoverageTests` (branch `test/floor0-smoke`) — a `FakePageData` implementing `IPageData` that supports both direct state injection (for `AddPage` validation tests) and a queued `ReadNextPage`-with-callback wiring that calls back into the owning `StreamPageReader.AddPage`, mirroring the real production object graph described in `StreamPageReader`'s own constructor comment. 11 new tests cover: `AddPage`'s granule-regression and granule=-1-without-single-continued-packet throws, resync (negative stored offset) handling in both `GetPagePackets` and `GetPageRaw`/`GetPage`, `FindPage`'s exact-match/forward-search-exhaustion/bisection-direct-hit/bisection-read-failure branches, and `GetPage`'s known-index-read-failure fallthrough. `StreamPageReader` coverage: 81.4% → 95.9%.

**Remaining 4.1%, left uncovered:**
- Lines 111-113 (`GetPagePackets`'s own re-cache after a direct disk read) and line 233 (`GetNextPageGranulePos`'s try-block closing brace after `return true`) are unreachable-closing-brace instrumentation artifacts, same class as the `Codebook.FastRange` brace noted earlier — the code before them always returns before reaching them.
- Lines 203-209 (`FindPageForward`'s "page index already known, re-read it" branch) is unreachable in a single-stream scenario: its guard is `++pageIndex == _pageOffsets.Count`, and `pageIndex` always starts as `_pageOffsets.Count - 1` (the last known page) when `FindPageForward` is called from `FindPage`, so the increment always lands exactly on `Count` on the first loop iteration. It would only be reachable if this stream's `_pageOffsets` could grow by more than one entry between checks (multi-stream page interleaving through a shared container reader) — not exercised here and not worth a bespoke multi-stream harness for one defensive branch.

---

## Codebook lookup: two-tier Huffman decode (prefix table + overflow list)

**Decision:** `Huffman.cs`/`Codebook.DecodeScalar` (`Codebook.cs:287-313`) use a hybrid data structure, not a spec-literal bit-by-bit tree walk. Codewords up to `MAX_TABLE_BITS` (10) bits get a direct-indexed lookup table (`PrefixTree`, sized `1 << tableBits`), built by replicating each short code into every slot consistent with its bit pattern. Codewords longer than 10 bits fall through to a linear-scan `OverflowList`. `UNUSED_LENGTH = 99999` sentinel-sorts unused entries to the end.

**Why:** A straight linked-list/linear scan over all codewords per symbol decode is O(n) in a per-sample hot path. The bounded prefix table trades a bounded amount of extra memory (at most 2^10 entries) for O(1) decode of the common case, paying linear-scan cost only for the rare long codes.

**How to apply:** Don't "simplify" this back to a plain tree walk or a single flat table sized to the longest codeword — both regress either memory or the common-case decode cost this was built to avoid.

## Codebook: `FastRange` thread-static synthetic identity list

**Decision:** When a codebook is "sparse" but under the sparse threshold, `_lengths` acts as an implicit identity value list (0..N-1). Rather than allocate `int[Entries]` and fill it with `0,1,2,...`, `FastRange` (`Codebook.cs:12-50`) is a `[ThreadStatic]`-cached, reused "fake list" — an `IReadOnlyList<int>` whose indexer computes `_start + index` on the fly. `GetEnumerator()` deliberately throws `NotSupportedException`; it's indexer-only by design.

**Why:** Avoids an allocation-per-decode for a value that's mathematically derivable from the index. Explicit in-code attribution: *"FastRange is 'borrowed' from GitHub: TechnologicalPizza/MonoGame.NVorbis"* (`Codebook.cs:11`). A prior off-by-one (`index > _count` should have been `index >= _count`) let the indexer read one past the end — fixed in commit `53194f7`.

**How to apply:** This is a shared mutable thread-static object handed out as an `IReadOnlyList<int>` — only safe under the single-threaded-per-decode-call usage contract already established elsewhere in this codebase (see the float/double and MDCT entries). Don't retain a reference to a `FastRange` instance across decode calls or hand it to another thread.

## Codebook: canonical Huffman codeword assignment via `available[32]`

**Decision:** `ComputeCodewords` (`Codebook.cs:165-199`) assigns codewords iteratively using a 32-slot `available[]` array tracking the next free codeword at each bit length, directly ported from libvorbis's codeword-assignment algorithm (Vorbis I spec §3.2.1) — not a textbook recursive tree build. `Utils.BitReverse` converts the accumulated code into the bitstream's bit-reversed order. Marked in-source as adapted from libvorbis (BSD-licensed).

**Why:** This is the spec-mandated canonical Huffman assignment; a from-scratch reimplementation risks subtly non-conformant codeword ordering that would silently desync the bitstream on read. Porting the reference algorithm avoids reinventing spec-critical bit-exact behavior.

**How to apply:** Treat as spec-derived, not stylistic. If it ever needs modification, cross-check against libvorbis's `_book_maptype1_quantvals`-adjacent codeword-assignment source and the spec section directly, not first principles.

## Codebook: `long idxDiv` in `InitLookupTable` (int-overflow fix)

**Decision:** `InitLookupTable` (`Codebook.cs:244`) accumulates `idxDiv *= lookupValueCount` across `Dimensions` iterations using `long`, not `int`.

**Why:** Fixed in commit `f3b8ed3` ("fix: three correctness issues — dead null check, Dispose idempotency, idxDiv overflow"): when `Dimensions > ~9` and `lookupValueCount > 1`, the product silently wraps as `int`, producing a wrong `moff` index and corrupting the lookup table for that codebook entry — no crash, no exception, just quietly wrong decoded values. This is the dangerous kind of bug (silent corruption, not a throw).

**How to apply:** Don't narrow this back to `int` during a future "cleanup" pass without checking this history — it will look like unnecessary widening to a reviewer unfamiliar with the overflow case.

## Codebook: `Dimensions < 1` rejected at load time (protects an invariant in a different file)

**Decision:** `Codebook.Init` throws `InvalidDataException` if `Dimensions < 1` (`Codebook.cs:67`, added in commit `912e6fa`). `Residue1.cs:20` carries a comment documenting why: *"codebook.Dimensions >= 1 is guaranteed by Codebook.Init; i always advances."*

**Why:** A codebook with `Dimensions == 0` passes `Init` silently but causes an infinite loop in `Residue1.WriteVectors` — the inner loop (`j < Dimensions`) never runs, so the outer counter `i` never advances past 0. The validation lives in `Codebook.cs`, but the invariant it protects is consumed in a completely different file (`Residue1.cs`), which is not visible from either file in isolation.

**How to apply:** If `Codebook.Init`'s validation is ever refactored, grep for "Dimensions >= 1" comments elsewhere before relaxing it — the guarantee is load-bearing outside the file that establishes it.

---

## MDCT: small-block (64/128) IMDCT bug inherited from stb_vorbis, fixed 2026-07-12

**Decision/history:** `Mdct.cs:174-194` gates the FFT's iteration-0 (`step3_iter0_loop`) calls on `_n > 64` and iteration-1 (`step3_inner_r_loop`) calls on `_n > 128`, rather than running them unconditionally for every block size.

**Why:** `step3_inner_s_loop_ld654` (called unconditionally at `Mdct.cs:229`) always runs the FFT's final three stages; step 2 (`Mdct.cs:~136`) is always the first stage. The hardcoded iteration-0/1 calls exist to cover the stages *between* those two — but for `n < 256`, those "between" stages overlap the final three, corrupting decoded output for legal 64- and 128-sample blocks. This bug is present verbatim in upstream stb_vorbis's `inverse_mdct` (verified by compiling stb master against its own `inverse_mdct_slow` O(n²) reference implementation) — apparently unfixed/unreported there. Output for `n >= 256` is bit-identical before and after this fix.

**Tests:** New O(n²) spec-formula reference tests plus per-block-size bit-exact golden hashes in `MdctTests.cs`, added specifically to catch this class of regression — the bug is invisible unless small blocks are exercised directly.

**How to apply:** Any future change to the step2/step3/`ld654` staging must re-run the golden-hash tests across *all* legal block sizes, including 64 and 128 — a change that only gets validated against n≥256 would not catch a regression of this exact bug reappearing.

## MDCT: twiddle tables shared process-wide, lock-free cache

**Decision:** `s_implCache` (`Mdct.cs:14-44`) is a single process-wide `static readonly ConcurrentDictionary<int, MdctImpl>`, populated via `GetOrAdd`, not a per-`Mdct`-instance lock-guarded `Dictionary`. Each `Mdct` instance additionally keeps the (at most 2) `MdctImpl`s it has personally seen in `_impl0`/`_impl1`, published via `Interlocked.CompareExchange` — no `lock` statement anywhere in this class.

**Why:** Twiddle tables are immutable once built and depend only on block size. Multiple decoder instances decoding streams with the same block sizes (common — most encoders emit the same block0/block1 pair) previously rebuilt identical trig-heavy twiddle tables per instance; sharing cuts that redundant setup work across the whole process. The per-instance 2-slot cache keeps the hot path (`Reverse`) free of any dictionary lookup or lock after the first call for each of the (at most 2) block sizes a given stream uses.

**How to apply:** Don't add a `lock` back into `Reverse()` — the design specifically eliminates one. If a third cache tier is ever needed, keep the same shape: process-wide immutable-data cache + small fixed per-instance slots published via `Interlocked`, not a per-call dictionary lookup.

## MDCT: `Reverse()` validates its own contract, independent of `StreamDecoder`'s header-level check

**Decision:** `Mdct.Reverse` (`Mdct.cs:22-28`) throws `ArgumentOutOfRangeException` unless `sampleCount` is a power of two in [64, 8192]; `samples` is null-checked; the buffer length is checked.

**Why:** `StreamDecoder.cs:215` (`_block0Size < 64 || _block1Size < _block0Size || _block1Size > 8192`) rejects illegal block sizes at header-parse time, before `Mdct` is ever invoked — this is the corrupted-setup-table failure mode commit `8062285` describes ("the 4-bit exponent field admits 1..32768 and out-of-range values corrupted Mdct setup"). `Mdct.Reverse`'s own check is defense-in-depth / a documented contract for any other caller, not the primary guard — the primary guard is the header validation.

**How to apply:** Keep both checks. Removing `StreamDecoder`'s header-level rejection and relying solely on `Mdct.Reverse`'s guard would surface the failure much later (mid-decode, deep in the call stack) instead of at header-parse time where the actual malformed input is.

## MDCT: hand-unrolled FFT butterfly via `Unsafe.Add` ref cursors — treat as a verified black box

**Decision:** `step3_iter0_loop`, `step3_inner_r_loop`, `step3_inner_s_loop`, `step3_inner_s_loop_ld654`, `iter_54` (`Mdct.cs:361-581`) are a hand-unrolled, split-radix FFT-style butterfly, rewritten (2026-07-12, commit `8165808`) from computed-index array access to `Unsafe.Add` ref-cursor arithmetic.

**Why:** Decreasing computed array indices defeat the JIT's bounds-check elimination, and these loops dominate the transform's cost — this is the single hottest code in the library. In-code comment (`Mdct.cs:357-360`): *"Offsets are provably in-range for any legal block size (see the golden-output tests)."* Measured ~30-35% faster `Reverse` (n=2048: 6.9→4.7us; n=8192: 30.4→21.2us). Adds a `System.Runtime.CompilerServices.Unsafe` package reference (needed for netstandard2.1, which has no transitive source for it).

**How to apply:** This is now raw pointer-like ref arithmetic with zero compiler bounds-checking to catch a mistake — correctness depends entirely on `MdctTests.cs`'s golden hashes and spec-reference tests, not on the type system. Don't modify without deep FFT literature review and re-running the full golden-hash suite across every legal block size. Variable names (`k00_20`, `v41_21`, etc.) only make sense against the original stb_vorbis/libvorbis C reference — don't rename them to "clean up" without also re-deriving the index math from the reference.

## Floor0: precomputed Bark-scale/window-delta maps keyed by block size

**Decision:** `_barkMaps`/`_wMap` (`Floor0.cs:25-26,54-65`) are `Dictionary<int, int[]>`/`Dictionary<int, float[]>` keyed by block size (only ever 2 keys), precomputed once in `Init` rather than recomputed per packet.

**Why:** Avoids repeating the transcendental-function-heavy Bark-curve synthesis (`SynthesizeBarkCurve`, `SynthesizeWDelMap`) on every `Apply` call. A dictionary with only 2 possible keys can look like overkill until you realize it's replacing per-packet trig-heavy work with a one-time setup cost.

## Floor1: `inverse_dB_table` static lookup

**Decision:** A 256-entry `static readonly float[] inverse_dB_table` (`Floor1.cs:330-395`) converts the floor's dB-domain values to a linear multiplier via direct index (`v[x] *= inverse_dB_table[y]`), instead of calling `MathF.Pow`/`Exp` per residue sample.

**Why:** This multiply happens per frequency bin, per channel, per block — one of the hottest per-sample operations in floor application. The table trades ~1KB static memory for eliminating a transcendental call from that inner loop.

## Floor1: fixed 64-element arrays are a spec-derived hard cap, not arbitrary

**Decision:** `Posts` (`Floor1.cs:12-13`), and the scratch `stepFlags`/`finalY` arrays allocated per `Apply` call in `UnwrapPosts` (`Floor1.cs:224-226`), are all fixed-size 64-element arrays rather than `List<int>` or dynamically-sized arrays.

**Why:** The Vorbis spec caps floor1 partition points at 64 total — this is a hard, spec-derived limit (see the `rangebits`/partition math in `Init`), not an arbitrary choice. Exploiting the known cap avoids dynamic sizing and (unlike an earlier pre-refactor version of this codebase, which used an `ACache`-pooled post list) sidesteps pooling entirely — allocation-free by construction, aside from `Data` itself and the two 64-element scratch arrays per `Apply` call.

**How to apply:** Don't replace these with dynamically-sized collections "for flexibility" — the 64-element cap is a spec fact, not a self-imposed limitation.

## Floor1: `RenderLineMulti` Bresenham line rendering is a correctness requirement

**Decision:** The floor curve between two (x,y) breakpoints is rasterized using an integer Bresenham line algorithm (`err`, `sy`, `ady -= Math.Abs(b) * adx` — `Floor1.cs:301-326`), not floating-point interpolation per x.

**Why:** This must bit-for-bit match what the Vorbis spec mandates (`render_line`) for decoder interoperability. Floating-point interpolation would produce a different — and therefore spec-noncompliant, possibly audibly different — curve than integer Bresenham, even though it looks like an equivalent (and simpler) approach.

**How to apply:** Do not "simplify" this to float interpolation. This is a correctness requirement, not a style or perf choice.

---

## Residue0: `ArrayPool<T>` for the two hottest per-packet scratch buffers

**Decision:** `Residue0.cs:132-184` (`partWordCache`, rented per `Decode` call) and `Residue0.cs:192-214` (`entryCache` in `WriteVectors`, rented per channel×partition×stage call) use `ArrayPool<T>.Shared.Rent`/`Return` instead of allocating fresh arrays. Replaces a 2D array (`new int[_channels, partitionWords][]`, reallocated every packet, commit `b95249b`) with a flattened `ch * partitionWords + entryIdx` 1D index — `ArrayPool` doesn't support 2D/jagged rentals cleanly, so the flattening is a direct consequence of choosing pooling.

**Why:** `WriteVectors` is called per channel × partition × stage; on a typical stereo file at 44kHz this eliminates hundreds of small heap allocations per decoded audio packet.

**How to apply:** If this indexing scheme looks like arbitrary arithmetic, it isn't — it's a "flatten 2D→1D for pooling compatibility" transformation. Don't revert to a 2D/jagged array without also reverting to per-call allocation.

## Residue0/1/2: shared logic via inheritance, not composition

**Decision:** `Residue1`/`Residue2` inherit from `Residue0` and override only the parts that differ: `Residue1` overrides `WriteVectors` only; `Residue2` overrides `Init`, `Decode`, and `WriteVectors` (its `Init` calls `base.Init(packet, 1, codebooks)`; its `Decode` calls `base.Decode(packet, doNotDecodeChannel, blockSize * _channels, buffer)`, remapping the per-channel block into one flattened "single super-channel" pass).

**Why:** The three Vorbis residue types — 0: per-channel/per-dimension pass; 1: per-channel/interleaved-dimension pass; 2: all-channels-interleaved-into-one-pass — share ~95% of their partition/classification/cascade logic (`Init`, most of `Decode`); only the final vector-scatter differs. Inheritance-with-override-only-what-differs avoids both duplicating the shared ~95% and over-abstracting into a strategy-object pattern that would lose `Residue2`'s elegant trick of just multiplying `blockSize` by `_channels` and delegating entirely to the base.

**How to apply:** Keep new residue-type-specific logic scoped to overriding `WriteVectors` (and `Init`/`Decode` only if the channel-remapping trick applies) — don't duplicate the shared partition/classification/cascade logic into a new sibling class.

## Residue2: flattened multi-channel interleave arithmetic

**Decision:** `Residue2.WriteVectors` (`Residue2.cs:23-47`) treats the multi-channel output as one flattened stream: `offset /= _channels`, then a manual wrapping counter `chPtr` (incremented, wrapped via `if (++chPtr == _channels)`) scatters decoded values across `residue[chPtr][offset]`, advancing `offset` only every `_channels`th write.

**Why:** This implements Vorbis residue-type-2's channel-interleaving directly, without allocating an intermediate flat buffer. The arithmetic is compact but not self-explanatory without knowing the spec's residue-type-2 interleaving definition.

**How to apply:** Don't touch this without the Vorbis I spec's residue type 2 section open next to it — it reads like arbitrary modular arithmetic unless you know what it's implementing.

## Mapping: `ForceEnergy`/`ForceNoEnergy` three-state channel execution

**Decision:** `IFloorData` exposes `ExecuteChannel` (computed) plus two independent settable flags, `ForceEnergy` and `ForceNoEnergy`, rather than a single boolean. `Mapping.DecodePacket` (`Mapping.cs:100-134`) sets `ForceEnergy` when a coupled angle/magnitude channel pair has energy in *either* channel, and `ForceNoEnergy` when a channel's assigned submap doesn't match the current pass's submap.

**Why:** Vorbis channel coupling means a channel with `Amp == 0` (no data-encoded energy) must still be processed if its coupled partner has energy, because the coupling transform (magnitude/angle) redistributes energy between the pair. The three-state (execute / forced-execute / forced-skip) encoding lets "do we run the floor/MDCT for this channel" be computed once from decode-time information, rather than re-derived from the coupling table by every consumer.

**How to apply:** Don't collapse this back to a single boolean — the three states encode genuinely different reasons a channel might or might not need processing, and a consumer needs to distinguish "no reason to force" from "forced off."

## Mapping: inverse coupling transform iterates channels in reverse order

**Decision:** `Mapping.cs:137` — `for (var i = _couplingAngle.Length - 1; i >= 0; i--)` — walks the inverse polar-coupling transform backward.

**Why:** This mirrors the Vorbis spec's requirement that coupling be undone in the reverse of the order magnitude/angle relationships were declared, since later coupling steps can reference channels already produced by earlier ones — coupling is applied as a chain. Nothing else in the loop body hints that iteration order is significant, which makes this easy to "simplify" to forward iteration by someone who doesn't realize order matters for correctness.

**How to apply:** Do not change this to forward iteration. If the loop is ever refactored, preserve reverse order explicitly (e.g. with a comment) rather than relying on the current code shape to convey it.

## Mode: precomputed 4-way window/overlap table

**Decision:** `Mode.cs:8-13,41-66` precomputes 4 window arrays and 4 `OverlapInfo` structs at `Init` time, covering all 4 combinations of `(prevFlag, nextFlag)` (short/short, long/short, short/long, long/long block transitions), selected at decode time via a 2-bit index (`windowIndex = (prevFlag ? 1 : 0) + (nextFlag ? 2 : 0)`, `Mode.cs:135`) derived from 2 stream bits read per packet.

**Why:** Vorbis allows adjacent blocks of different sizes, and the overlap-add windowing shape depends on both neighbors' sizes. Precomputing all 4 combinations once avoids repeating the trig-heavy `CalcWindow` call per packet. `OverlapInfo` is a `struct`, not a `class`, specifically because it's looked up on every packet decode — a value type avoids heap indirection on that hot lookup.

---

## Ogg: forward-only vs. seekable are separate class hierarchies

**Decision:** `PageReaderBase` supplies shared byte-level sync/resync/CRC/header-parsing machinery as an abstract base. Below that, `PageReader`/`StreamPageReader`/`PacketProvider` (seekable path) buffer all page offsets and support random-access re-reads for bisection seeking; `ForwardOnlyPageReader`/`ForwardOnlyPacketProvider` keep only a bounded queue of pages and throw `NotSupportedException` for seek-related operations. `ContainerReader`'s constructor picks the pair once, based on `stream.CanSeek` (`ContainerReader.cs:66-84`, via the `SelectPageReaderFactory` helper).

**Why:** A single flexible implementation would force non-seekable (e.g. network/pipe) consumers to pay for offset-list bookkeeping and locking they can never use, and would force the seekable path's `PacketProvider` to support an unbounded look-ahead queue it doesn't need. `ForwardOnlyPacketProvider` itself extends `DataPacket` directly and returns `this` from `GetNextPacket` to avoid allocating a distinct packet object per call — impossible on the seekable path, where packets must be independently addressable for out-of-order/backward reads during seeking.

**How to apply:** Don't try to unify these two hierarchies into one flexible class "for simplicity" — the split exists because the two access patterns have genuinely incompatible resource-usage profiles.

## Ogg: CRC-32 validation is mandatory, hand-implements Ogg's exact bit order

**Decision:** Every candidate page — including ones found via resync — has its CRC computed and checked (`PageReaderBase.cs:33-70`, `VerifyPage`) before being handed to `AddPage`; there is no flag anywhere in the public API to disable this. `Crc.cs` uses polynomial `0x04c11db7` with MSB-first, left-shifting table generation and update — the "reflected-out, non-reflected-in" variant specific to Ogg, not the common right-shifting IEEE CRC-32 (`0xEDB88320`) used by zip/PNG/etc.

**Why:** Page boundaries are found by scanning for the 4-byte `"OggS"` capture pattern in arbitrary byte streams; that pattern can occur by chance in random data. CRC is the only correctness gate that a "found" page is real. Using a generic/standard CRC-32 implementation (e.g. `System.IO.Hashing.Crc32`, which implements the reflected IEEE variant) would produce different checksums than real Ogg files use, silently failing every check.

**How to apply:** Never add a bypass for CRC validation, even for a "trusted" input path. Don't replace `Crc.cs`'s hand-rolled implementation with a standard library CRC-32 — it computes a different value.

## Ogg: resync recovery via byte-level scan with carry-forward + overflow queue

**Decision:** `ReadNextPage` (`PageReaderBase.cs:87-292`) scans a 27-byte header buffer for the `"OggS"` capture pattern. If found but the page fails CRC, the read-ahead bytes are pushed into an `_overflowBuf` queue (`EnqueueData`) rather than discarded, so a subsequent scan can consume them without re-reading from the stream. If no marker is found at all, the last 3 bytes are carried to the front of the buffer before refilling, since a capture pattern can straddle the boundary between two reads. `WasteBits` tracks how many bytes were skipped as garbage.

**Why:** A naive discard-and-rescan resync strategy would either lose data that's actually part of a subsequent valid page, or require seeking backward — impossible on non-seekable streams. The carry-forward + overflow-queue approach guarantees no byte is read from the underlying `Stream` twice and no partially-scanned sync candidate is dropped. Two bugs were fixed here, both of which only manifested on the resync path (`index > 0`) and not on clean files starting at offset 0: `VerifyPage`'s bounds check double-counted `index` (`cnt - index < index + 27 + segCnt`), and `VerifyHeader` used `==` instead of `>=` when checking whether the overflow buffer supplied a complete segment table.

**How to apply:** If touching resync logic, test specifically against corrupted/truncated streams that force a resync — bugs here are invisible on any clean fixture file.

## Ogg: `EnsureRead` tolerates repeated zero-byte reads

**Decision:** `EnsureRead` (`PageReaderBase.cs:169-188`) loops calling `Stream.Read` until either the requested count is satisfied or 10 consecutive zero-byte reads occur, rather than assuming one `Read` call fills the buffer or treating any short read as EOF.

**Why:** In-code comment: *"Network streams don't always return the requested size immediately... it will loop until getting a certain count of zero reads... in most cases, the network stream probably died by the time we return a short read."* This is a deliberate deviation from the single-call `Stream.Read` pattern many simpler parsers use, made necessary by network-stream semantics.

## Ogg: `GetIsVorbisBugDiff` bit-pattern heuristic (keeps container/codec abstraction clean)

**Decision:** `GetIsVorbisBugDiff` (`PacketProvider.cs:242-274`) detects a known libvorbis long→short-block-crossing-a-page-boundary granule-miscounting bug purely from the numeric *shape* of the granule discrepancy (a run of set bits followed by a run of cleared bits: `diff == (1 << longBlockBits) - (1 << shortBlockBits)`), rather than having the Ogg container layer know about codec-level block-size concepts directly.

**Why:** In-code comment: *"This requires either breaking abstractions OR doing some fancy bit math... We're gonna use the latter to keep the abstractions clean."* The actual root-cause compensation lives in `Mode.GetPacketInfo`, gated by an `isLastInPage` parameter added specifically for this fix (commit `c81dc8a`).

**How to apply:** If a similar codec-specific quirk needs detecting from the container layer in the future, prefer a numeric/structural heuristic over threading codec knowledge into the container classes — this is the established precedent for keeping that boundary clean.

## Ogg: `FindPacket`'s 3-way granule-diff dispatch

**Decision:** `FindPacket` (`PacketProvider.cs:169-218`) distinguishes three cases when a page's calculated end-granule doesn't match the following page's stored granule position: (1) `diff > 0` matching the libvorbis bug's bit pattern → apply the `GetIsVorbisBugDiff` compensation; (2) `diff > 0` otherwise → a genuine timeline "hole" from spliced/edited source audio (issue #39) — `gps` is left as-is, a request landing in the hole snaps forward to the page's first real packet; (3) `diff < 0` → EOS-clipping over-count — `gps[]` is shifted to realign with the known previous-page boundary.

**Why:** Commit `3edd3c2` and its inline comments explicitly separate "genuine granule-timeline hole (spliced/edited source)" from "backward calculation over-counted samples (e.g. EOS-clipped last page)" — conflating them previously threw `"GranulePos mismatch"` on legitimately spliced files that had done nothing wrong.

**How to apply:** If a fourth granule-mismatch scenario is ever found in the wild, add a fourth explicit case rather than folding it into one of the existing three — the existing three are deliberately narrow, evidence-based classifications, not a catch-all.

## Ogg: `GetStreamStartGranule` — streams don't necessarily start at granule 0

**Decision:** `PacketProvider.cs:276-305`. `SeekTo`'s "seek to the beginning" shortcut doesn't assume granule 0 is the stream start. It resolves `_firstAudioPacketIndex` by checking if the first data page is itself a continuation (skipping a spilled setup-header tail — issue #37), walks that page's packets to compute the true start granule (issue #35, e.g. a broadcast capture beginning mid-timeline), and clamps the result to `Math.Max(0, start)`.

**Why:** Two different upstream-encoder deviations from the naive "audio always starts on a fresh page at granule 0" assumption a spec-literal parser would make. The clamp exists because a small negative result "isn't reliable... no way to distinguish that from a genuine begin-trim... Vorbis has no formal lead-in trim concept the way Opus does" (in-code rationale).

## Ogg: `NormalizePacketIndex` walkback bounded at `FirstDataPageIndex`

**Decision:** `PacketProvider.cs:309-351` (commit `003eec0`). Before decrementing the page index while resolving a continuation packet's true starting page, the walkback checks whether it has reached `FirstDataPageIndex` and, if so, snaps directly to `(FirstDataPageIndex, 0)` instead of continuing to decrement.

**Why:** The walkback previously had no lower bound; a pathological stream where every page holds exactly one continued packet (one giant packet spanning the whole file) caused an O(N) walk that only terminated when `GetPage` failed on a negative index and threw. The bound turns an adversarial-input O(N) walk + crash into an O(1) snap.

## Ogg: packet continuation stored as bit-packed page:packet index, not `List<int>`

**Decision:** `Packet.cs` stores continuation data as `_firstPart` (a packed `pageIndex:packetIndex` as `24:8` bits in a single `int`) plus `_extraParts` (`int[]`, `null` for the common single-page case), replacing an earlier `IReadOnlyList<int>` (a boxed `List<int>` per packet, commit `78214c3`).

**Why:** This eliminates a `List<int>`-plus-backing-array allocation on every decoded packet — a hot-path allocation-elimination change. In-code comment documents the bit-packing's capacity tradeoff: good for up to 1016 GiB of Ogg file (in practice closer to 300 days at 160kbps) — an explicit, deliberate limit, not an oversight.

**How to apply:** If a file larger than that theoretical ceiling is ever a real requirement, this packing scheme needs revisiting — it is not future-proofed past that point by design (the tradeoff was made deliberately in exchange for the allocation win).

## Ogg: weak-reference ownership chain for packet providers

**Decision:** `StreamPageReader.cs:30-42`'s constructor carries an explicit ownership-graph comment: *"The packet provider has a reference to us, and we have a reference to it. The page reader has a reference to us. The container reader only holds a weak reference to it. So long as the user doesn't drop their reference and the page reader doesn't drop us, the packet provider will stay alive."*

**Why:** This documents the invariant the weak-reference design (and the `GetStreams()` weakref-pruning fix documented earlier in this file) depends on: the container is deliberately never the sole thing keeping a stream's packet provider alive, so a caller who has stopped using a chained-in logical stream can let the whole page-reader subgraph for that stream be garbage-collected without the container needing an explicit "close stream" API.

**How to apply:** Don't add a strong reference from `ContainerReader` to a packet provider "for safety" — that would break the intended GC behavior for abandoned chained streams.

## Ogg: `StreamPageReader` page-packet caching avoids a double disk read on seek

**Decision:** `StreamPageReader.cs:92-120,383-393`. `ReadPageData` fetches the page's packets (`_reader.GetPackets()`) immediately, while the page is already loaded and the lock is still held, and stashes the result in `_cachedPagePackets` — rather than fetching lazily on first `GetPagePackets` access, which previously issued a second disk read for a page already loaded moments earlier (commit `3411f35`).

**Why:** Pure perf fix specific to the seek hot path. The non-obvious part is the ordering dependency: packets must be fetched *before* the lock is released, inside `ReadPageData`, not lazily on first access — a naive "read metadata first, fetch packets on demand" design would silently regress back to the double-read.

## Ogg: `AddPage` granule sanity checks reject "impossible" states outright

**Decision:** `StreamPageReader.cs:44-90`. Beyond resync detection, `AddPage` throws `InvalidDataException` for two specific "shouldn't happen" states: a later page's granule position less than the running max (*"Granule Position regressed?!"*), and a page with granule position -1 that isn't a pure single-continued-packet continuation page.

**Why:** CRC only proves bytes weren't corrupted in transit — it doesn't prove the encoder produced a spec-valid stream. These are defensive invariant checks against malformed-but-CRC-valid streams that a spec-literal parser might otherwise trust blindly.

---

## StreamDecoder: pre-roll + roll-forward seek algorithm for MDCT overlap-add correctness

**Decision:** Seeking (`StreamDecoder.cs`, `SeekTo`) always decodes one packet of "pre-roll" before the target (`_packetProvider.SeekTo(samplePosition + _granuleOffset, 1, GetPacketGranules)`), then rolls forward sample-by-sample inside `_prevPacketBuf` to the exact target. If the provider lands in a granule-timeline hole (spliced stream, issue #39), the roll-forward count goes negative and the code snaps forward to the first sample that actually exists rather than reporting the originally-requested position.

**Why:** Vorbis packets overlap 50% via MDCT windowing; decoding a single packet without its predecessor produces audible artifacts at the seek point. This is a codec-level correctness requirement, with defensive hole-handling layered on top for malformed/discontinuous streams.

**How to apply:** Any change to the seek path must preserve the pre-roll — removing it "to simplify" reintroduces audible seek-point artifacts, not just a subtle numerical difference.

## StreamDecoder: `ResetDecoder()`'s position-reset invariant

**Decision:** `ResetDecoder()` (`StreamDecoder.cs:320`) unconditionally zeroes `_currentPosition`. This is documented safe only because every caller sets `_currentPosition` right before or right after calling it.

**Why:** Root cause of issue #40 (a dead loop in `Read()`): `ReadNextPacket`'s EOS valid-length backoff uses `_currentPosition`; a stale value from a prior decode caused a bogus negative valid length on a near-end re-seek, which — before the `Read()` guard below was added — spun forever. Fixed in commit `d13affb`.

**How to apply:** This is a named invariant, not an implementation detail: *"every `SeekTo`/reset caller must set `_currentPosition` around `ResetDecoder()`."* A future refactor that adds a new caller of `ResetDecoder()` must uphold it explicitly, not assume it's automatic.

## StreamDecoder: `Read()`'s `>=` guard against the issue #40 dead loop

**Decision:** The refill condition in `Read()` is `if (_prevPacketStart >= _prevPacketEnd)`, not the more "natural"-looking `==`.

**Why:** A seek near the end of stream can leave `_prevPacketEnd < _prevPacketStart` (a negative valid length, see the `ResetDecoder` entry above). With `==`, that degenerate state would never re-enter the refill branch, and neither branch would make progress — `Read()` spins forever. Fixed alongside commit `d13affb`/`1c6dfb3`.

**How to apply:** Do not "simplify" this back to `==` — it looks like an off-by-one but is deliberate, and reverting it reintroduces the exact dead-loop bug issue #40 was filed for.

## StreamDecoder: EOS drain clamp only queries `TotalSamples` where it's provably cheap

**Decision:** `StreamDecoder.cs:375-392`. When `ReadNextPacket` fails (no more packets) but EOS wasn't flagged mid-decode, the drain of the current packet's remaining samples is clamped to `TotalSamples - (_currentPosition + count/_channels)` — but only when `_packetProvider.CanSeek`, and only in this specific branch.

**Why:** Without the clamp, the drain could emit samples past the stream's stated end when `HasAllPages` was still false at decode time, making total emitted length depend on caller behavior (whether `TotalSamples` was queried first) — a subtle non-determinism bug class. `GetGranuleCount()` (which backs `TotalSamples`) is safe/cheap to call *here specifically* because reaching this branch already implies EOF was hit, so no extra I/O is triggered — that safety does not hold at other call sites in this method.

**How to apply:** Don't hoist this `TotalSamples` query to earlier in the method "for clarity" — at other points in `Read()`, EOF has not necessarily been reached yet, and the call would no longer be free.

## StreamDecoder: `_granuleOffset` timeline-normalization layer

**Decision:** On construction, for seekable streams, the decoder seeks to granule 0 once purely to learn where the stream's *actual* granule timeline begins (which may not be 0 for a spliced/cut capture), stores the delta as `_granuleOffset` (`StreamDecoder.cs:29`, readonly per commit `bc9a73f`), and every public position/seek/total-samples computation subtracts it, so callers always see position 0 as "first decodable sample." The initial probe seek is wrapped in try/catch — an `ArgumentOutOfRangeException` there means "no audio pages; nothing to normalize."

**Why:** This is a systemic architectural pattern threaded through nearly every position-related code path (`Read`, `SeekTo`, `TotalSamples`, etc.), not a one-off fix — distinct from the specific issue #39/#35/#37 bug fixes documented earlier in this file, which are the concrete symptoms this pattern generalizes a fix for.

**How to apply:** Any new position-reporting or seek-related member must subtract/add `_granuleOffset` consistently with the existing call sites — treat it as part of the public-position API's contract, not an implementation detail local to one method.

## DataPacket: 64-bit sliding bit-bucket with a separate overflow byte

**Decision:** `DataPacket.cs:53-56,169-285`. Bits are read byte-at-a-time into a `ulong _bitBucket`, LSB-first (per Vorbis bit order), with counts that can exceed 64 handled via a separate `_overflowBits` byte rather than a wider integer type or a byte-array window.

**Why:** A hand-rolled variable-width bit reader, optimized to avoid array indexing/allocation in the hottest path in the library (called per-bit, effectively, during header/codebook/residue decode). `_overflowBits` exists specifically to let the bucket temporarily hold more than 64 bits without losing data mid-shift.

## DataPacket: `SkipBits`'s `count > 64` guard (C# shift-operator gotcha)

**Decision:** `SkipBits` (`DataPacket.cs:212-214`) throws `ArgumentOutOfRangeException` for `count > 64`, matching `TryPeekBits`'s existing guard. The two callers that previously exceeded 64 (`Extensions.SkipBytes`, and a `StreamDecoder` loop skipping unused channel-time-feature bits 16 at a time) were changed to loop in ≤64-bit chunks (commit `716040f`).

**Why:** When `_bitCount > 64` (overflow bits present) and `SkipBits` is called with `count > 64` but `count < _bitCount`, the shift expression `(64 - count)` goes negative; C# wraps a `ulong` left-shift via `& 63` rather than saturating or throwing, silently corrupting `_bitBucket`. This is a genuine C#-shift-operator gotcha (shift amounts are masked mod 64/32, not saturated), not defensive fluff.

**How to apply:** Don't remove this guard, and don't call `SkipBits` with a count that could exceed 64 without first chunking it — the failure mode is silent data corruption, not a clean exception, if the guard weren't there.

## DataPacket: `GetFlag` avoids `Enum.HasFlag` boxing

**Decision:** `GetFlag` (`DataPacket.cs:111-112`) implements the check as `(_packetFlags & flag) == flag` rather than `_packetFlags.HasFlag(flag)`. In-code comment: *"bitwise test instead of Enum.HasFlag (Enum.HasFlag), which boxes on .NET Framework; this runs per-packet."*

**Why:** `IsResync`/`IsEndOfStream`/`IsShort` are read per packet in the hottest part of the decode loop; `Enum.HasFlag` boxes the enum value on every call under netstandard2.0/.NET Framework — a real, if individually small, per-packet allocation this avoids (commit `d9ae22b`).

## DataPacket: `PacketFlags` sized as `byte` with reserved `User0..User4` bits

**Decision:** `[Flags] enum PacketFlags : byte` (`DataPacket.cs:14-51`), with five explicitly reserved `User0`-`User4` bits, commented *"for now, let's use a byte... if we find we need more space, we can always expand it... for use by inheritors."*

**Why:** A deliberate extensibility contract for the abstract `DataPacket` base class (subclassed by `Ogg.Packet`), letting container-specific implementations stash their own per-packet flags without needing a second field or breaking the ABI.

## Test seams: internal constructor parameters, not mutable statics

**Decision (2026-07-12):** `StreamDecoder`'s `internal StreamDecoder(IPacketProvider, IFactory)` ctor-parameter pattern was the right idea from the start. `VorbisReader` and `ContainerReader` originally used a worse pattern instead — global mutable static `Func<>` properties (`VorbisReader.CreateContainerReader`/`CreateStreamDecoder`, `ContainerReader.CreatePageReader`/`CreateForwardOnlyPageReader`) swapped by tests — which required serializing the entire test assembly (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) because concurrent tests mutating the same static could race. Both were refactored to internal ctor overloads taking the factory delegates as parameters instead, eliminating the race and the need for `DisableTestParallelization`. 6 further static seams elsewhere in `Ogg/*.cs` (`StreamPageReader`/`ForwardOnlyPageReader`/`PageReader`/`PageReaderBase`'s `CreatePacketProvider`/`CreateStreamPageReader`/`CreateCrc`, plus `StreamDecoder.CreateFactory`) were found dead — never overridden by any test — during the same audit and deleted outright rather than migrated.

**Why:** A mutable static is inherently a shared, unscoped piece of global state; any two tests that both mutate it can race under parallel execution, and the only fix at that layer is to serialize the whole assembly — a blunt instrument that slows every test run, not just the ones that need it. Constructor-parameter injection scopes the substitution to the single object under construction, with no shared mutable state at all.

**How to apply:** New internal test-substitution points should be constructor parameters, never a bare mutable `static { get; set; }` — that pattern already caused one documented cross-test race in this codebase. Before adding a new static seam "for testability," check whether a constructor parameter can do the same job — it almost always can.

## `IFactory`: internal DI for testability, not runtime configurability

**Decision:** `IFactory` (`Factory.cs`) is an `internal`, not `public`, interface, with methods like `CreateFloor(IPacket)`/`CreateResidue(IPacket)` that read the type discriminator from the packet themselves and dispatch to concrete `Floor0`/`Floor1`/`Residue0/1/2` etc. `StreamDecoder`'s internal 2-arg constructor accepts an `IFactory`; the public single-arg constructor hardcodes `new Factory()`.

**Why:** Two purposes: keeps type-dispatch-on-wire-format logic colocated with construction instead of scattered through `StreamDecoder`, and gives tests an internal seam to substitute a fake factory (see the previous entry) — without exposing that seam as a public extensibility point. This is dependency injection for testability, not for runtime configurability; there is no supported scenario where an end user of the library provides their own `IFactory`.

**How to apply:** Don't make `IFactory` public "for flexibility" without a concrete use case — its internal visibility is intentional, matching its purpose.

## VorbisReader: `SwitchStreams` return value keyed on format compatibility

**Decision:** `SwitchStreams(int index)` (`VorbisReader.cs:280-`) returns `true` only if the new stream's `Channels`/`SampleRate` differ from the old one's — not merely "did the index change" — and explicitly copies `ClipSamples` from the old decoder to the new one before switching.

**Why:** This answers "how does `VorbisReader` handle chained/concatenated Ogg files with different channel/sample-rate mid-file": callers (e.g. an audio playback pipeline) need to know specifically when they must reconfigure their output device/resampler, hence a bool keyed on format compatibility rather than a generic "switched" bool. The `ClipSamples` carry-through is a deliberate behavioral-consistency choice — per-decoder state that should feel like a `VorbisReader`-level setting to API consumers, not something that silently resets on every chained-stream transition.

## VorbisReader: constructor disposes partial resources on every failure path

**Decision:** The container-reader creation in `VorbisReader`'s constructor is wrapped in try/catch; on either `TryInit()` returning `false` **or throwing**, the code clears the callback, disposes the container reader, and (if `closeOnDispose`) disposes the caller's stream, then rethrows.

**Why:** Commit `272b61e`: when `TryInit()` threw, the local `containerReader` was never disposed, leaking the underlying stream resources — the prior code only cleaned up on the expected-failure path (`false` return), not the exceptional one.

**How to apply:** Any future constructor logic that can fail partway through resource acquisition must clean up on *every* failure path, not just the one that was originally anticipated — this bug is exactly what happens when only the "normal" failure case is handled.

## VorbisReader: `Dispose()` idempotency needed an explicit flag

**Decision:** `VorbisReader.cs:21,134-137` uses an explicit `_disposed` bool guard, even though the fields it cleans up (`_decoders`, `_containerReader`) are `readonly` and non-null after construction.

**Why:** Commit `f3b8ed3`: the "obvious" guard (`if (_decoders != null)`) looked like it provided idempotency but structurally never could, since those fields are never null once set in the constructor — a second `Dispose()` call would re-invoke `_containerReader.Dispose()` and re-iterate the already-cleared decoder list. This is a "looks right but isn't" trap: the null check reads as defensive but is provably always true.

**How to apply:** Don't remove `_disposed` as "redundant" because the fields look non-nullable — the guard's job is idempotency across multiple `Dispose()` calls, which a null check on a `readonly` field cannot provide.

## StreamStats: `InstantBitRate` is the only field with a concurrency guarantee

**Decision:** `StreamStats.cs:10-14,23-25,37-47`. `InstantBitRate` packs `(bits << 32 | samples)` into a single `long`, read via `Volatile.Read` and written via `Volatile.Write` — no lock, but a real atomicity guarantee. Every *other* field in `StreamStats` (`_audioBits`, `_totalSamples`, etc.) is plain, unsynchronized, written only from the decode thread. This replaced an earlier `Monitor` lock protecting parallel `int[2]` arrays (commit `b95249b`).

**Why:** `Volatile.Read`/`Write` make the packed bits/samples pair atomic so a concurrent reader never sees a torn combination. This is a deliberate, narrow concurrency guarantee: `InstantBitRate` is meant to be readable from a UI thread while decode runs on a background thread, consistent with the library's documented single-decode-thread contract (see `OpenAsync`, below) — everything else in `StreamStats` explicitly is *not* thread-safe.

**How to apply:** Don't assume any other `StreamStats` property is safe to read from a different thread than the one driving decode — only `InstantBitRate` has that guarantee, and it has it for a specific, narrow reason (packed-atomic read/write), not because the whole class is thread-safe.

## TagData: `GetTagSingle`'s last-wins semantics for repeated comment keys

**Decision:** `GetTagSingle(key, concatenate: false)` (the default, `TagData.cs:49-61`) returns the **last** value when a key repeats, not the first.

**Why:** Vorbis comments allow repeated keys (e.g. multiple `GENRE=` entries) with no defined precedence in the spec. "Last wins" is a specific interpretation choice among equally-defensible options (a "first wins" alternative would have been just as reasonable) — worth recording explicitly since a future contributor might "fix" this thinking it's a bug, when it's actually a considered choice with no spec-mandated correct answer.

## VorbisReader: `OpenAsync` + single-thread decode contract

**Decision:** `OpenAsync(string, ...)`/`OpenAsync(Stream, ...)` (`VorbisReader.cs:97-113`) are `Task.Run`-wrapped factory methods, paired with explicit XML-doc remarks that `ReadSamples`/`SeekTo` are not thread-safe and must be called from a single thread, recommending a dedicated background thread for the object's lifetime (commit `1ad5181`).

**Why:** This formalizes what was previously an implicit assumption — the whole decode/seek state machine (`_prevPacketStart`, `_currentPosition`, etc.) has zero synchronization — into an explicit API contract, giving UI/async callers a sanctioned way to avoid blocking on the synchronous three-header-packet read that happens inside the constructor. Only *opening* is made async-friendly; decode stays deliberately synchronous/single-threaded for simplicity. A full async-plumbing-through-~6-internal-contracts approach was evaluated separately and deferred as not worth the scope versus the CPU-to-I/O ratio of this workload.

**How to apply:** Don't add async overloads to `ReadSamples`/`SeekTo` without first revisiting that deferred async-plumbing evaluation — the current design's simplicity depends on decode staying synchronous and single-threaded.

## StreamDecoder: `Read(Span<float>)` replacing the offset/count overload

**Decision:** The canonical decode method (`StreamDecoder.cs:350-`) takes only a `Span<float> buffer`, no separate offset/count; the loop advances by slicing the span (`buffer = buffer.Slice(written)`) rather than tracking an integer index. The three-arg `Read(Span<float>, int, int)` overload is `[Obsolete]` and now just validates, slices, and delegates to the canonical method (commit `ccaae98`). The canonical overload also has no null-guard on `buffer` — `Span<float>` is a struct, so the compiler rejects null assignment entirely; a prior `if (buffer == null)` check was provably dead code and was removed (commit `f3b8ed3`) because it "confused readers into thinking the method might accept a null-like value."

**Why:** Eliminates offset arithmetic that existed solely to support the offset parameter, and lets `Span<float>` callers naturally pass an already-offset slice instead of the API needing to support offsets itself.

**How to apply:** Don't re-add a null check to the `Span<float>`-only overload — it can't happen, and the check itself is misleading to future readers. If porting patterns from the old `float[]`-based API, drop null-checks on any `Span<T>` parameter.

## Utils: `ConvertFromVorbisFloat32`'s deliberate BCL fallback for the exponent step

**Decision:** `Utils.ConvertFromVorbisFloat32` (`Utils.cs:48-61`) unpacks Vorbis's custom 32-bit float format mostly via bit manipulation (sign-extend, mask/shift mantissa), but the final `2^exponent` step uses `MathF.Pow` rather than further bit tricks.

**Why:** In-code comment: *"We could use bit tricks to calc the exponent, but it can't be more than 63 in either direction. This creates an issue, since the exponent field allows for a *lot* more than that... larger exponent values don't seem to be used by the Vorbis codebooks... Either way, we'll play it safe and let the BCL calculate it."* A deliberate correctness-over-micro-perf choice, with an explicit acknowledgment that the format technically allows exponent values the fast bit-trick path couldn't safely handle, even though no real codebook is known to use them.

**How to apply:** If ever tempted to "finish the job" and bit-trick the exponent step too, re-read this comment first — the author already considered it and stopped deliberately, not for lack of trying.

## Packaging: pinned `AssemblyVersion`/`FileVersion` vs. moving NuGet `Version`

**Decision:** `AssemblyVersion`/`FileVersion` stay pinned to `major.minor` while `Version` (the NuGet-visible package version) moves with git height/prerelease label. This was originally a manual freeze at `1.0.0.1` in `NVorbis.csproj` across alpha.1 → alpha.2 → beta.1; it is now enforced declaratively by **Nerdbank.GitVersioning (NBGV)** via `version.json`'s `assemblyVersion.precision: "minor"` (migration completed, PR #81). `NVorbis.csproj` no longer carries `Version`/`AssemblyVersion`/`FileVersion` properties — a comment at `NVorbis.csproj:10` points to `version.json`, and `version.json` carries the "why" comment above `assemblyVersion`.

**Why:** Avoids binding-redirect churn for consumers on every prerelease bump — a moving `AssemblyVersion` forces consumers with a strong-named reference to add a new binding redirect on every single prerelease, which is disproportionate churn for a prerelease train.

**How to apply:** Keep `assemblyVersion.precision` at `minor` in `version.json`; don't switch to MinVer (it would make `AssemblyVersion` track `Version` again, undoing the pin). After any versioning change, verify a full pack/build cycle shows `Version` advancing but `AssemblyVersion` static across prereleases.
