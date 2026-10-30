using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace NVorbis.Tests
{
    // Seek fidelity across EVERY fixture: a seek must (a) return, (b) land where it says, and
    // (c) deliver the same samples a linear decode delivers at that position.
    //
    // Three classes of seek failure have shipped in NVorbis, none of which the existing seek
    // tests (which assert SamplePosition or "read > 0") could see:
    //   1. the seek never returns      -- 0.10.5: Read spins forever after a first-page seek
    //   2. the seek throws on a fresh reader -- 0.10.5: SeekTo(0) before any ReadSamples
    //   3. the seek reports the requested SamplePosition but the AUDIO comes from elsewhere
    //      -- post-0.10.5 master: every first-data-page target snapped to the stream start
    // Targets are derived from each file's own Ogg page table (interior of the first data
    // page, every page boundary +/-1, interior of every page, the last sample), so any
    // fixture added to TestFiles is probed at ITS page boundaries automatically.
    //
    // VALID FILES ONLY.  Sample-exact seeking is only defined when the granule-position metadata
    // agrees with the audio.  Fixtures whose metadata is wrong are listed in CorruptFixtures and
    // skipped here; what the decoder owes those files is "don't hang, don't crash", which their
    // Issue*RegressionTests already assert.  Do not add positional assertions against them.
    public class SeekFidelityTests
    {
        private const int ProbeLength = 256;

        /// <summary>Fixtures with encoder-produced granule-position defects (metadata contradicts the packets' sample counts).</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> CorruptFixtures = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // libVorbis I 20070622 mis-counts the granule across long->short block boundaries (issue #28); the linear
            // decode drops a block at each mis-counted boundary while the seek path re-anchors on the page granules
            ["issue28test.ogg"] = "libvorbis 2007 granule mis-count (issue #28)",
            // written by the C# 'OggVorbisEncoder' package; the EOS page's granule overstates the audio by 336 samples
            // (acknowledged as an 'encoder-produced granule-position defect' in Issue40RegressionTests)
            ["issue40test.ogg"] = "last-page granule overstated by 336 (issue #40 attachment)",
        };

        public static IEnumerable<object[]> Fixtures()
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestFiles"), "*.ogg"))
            {
                if (!CorruptFixtures.ContainsKey(Path.GetFileName(file))) yield return new object[] { Path.GetFileName(file) };
            }
        }

        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Fixture_HasADataPage(string name)
        {
            var pages = OggPageTable.Read(TestFile(name));
            Assert.True(pages.DataPageCount > 0, "no page with a positive granule position");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void FreshOpen_SeekToEveryDerivedTarget_DeliversLinearDecodeSamples(string name)
        {
            var path = TestFile(name);
            var linear = SeekFidelity.LinearDecode(path, out var channels);
            var total = linear.Length / channels;

            var problems = new List<string>();
            foreach (var target in SeekFidelity.DerivedTargets(path, total))
            {
                // a fresh reader per target: nothing read yet, so this also covers class 2
                using var reader = new VorbisReader(path);
                reader.ClipSamples = false;
                var problem = SeekFidelity.TryProbe(reader, linear, channels, target, ProbeLength);
                if (problem != null) problems.Add(problem);
            }
            Assert.True(problems.Count == 0, $"{name} (total {total}, pages end at {string.Join(",", OggPageTable.Read(path).DataPageEndGranules)}):\n  " + string.Join("\n  ", problems));
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void OneReader_SeekToEveryDerivedTarget_BackwardsAndForwards_DeliversLinearDecodeSamples(string name)
        {
            var path = TestFile(name);
            var linear = SeekFidelity.LinearDecode(path, out var channels);
            var total = linear.Length / channels;

            using var reader = new VorbisReader(path);
            reader.ClipSamples = false;
            var targets = SeekFidelity.DerivedTargets(path, total);
            // forwards, then backwards, then alternating ends: decoder state from the previous
            // seek (and the samples read after it) must not leak into the next
            var order = new List<long>(targets);
            for (var i = targets.Count - 1; i >= 0; i--) order.Add(targets[i]);
            for (var i = 0; i < targets.Count / 2; i++) { order.Add(targets[i]); order.Add(targets[targets.Count - 1 - i]); }
            var problems = new List<string>();
            foreach (var target in order)
            {
                var problem = SeekFidelity.TryProbe(reader, linear, channels, target, ProbeLength);
                if (problem != null) problems.Add(problem);
            }
            Assert.True(problems.Count == 0, $"{name}:\n  " + string.Join("\n  ", problems));
        }
    }

    /// <summary>Seek + probe helpers shared by the seek fidelity and regression tests.</summary>
    internal static class SeekFidelity
    {
        /// <summary>Hang guard: on xunit 2 <c>[Fact(Timeout)]</c> does not interrupt a synchronous CPU-bound loop, so bound the wait ourselves. The thread is left to die with the process.</summary>
        public static T WithTimeout<T>(Func<T> op, int timeoutMs = 10_000)
        {
            T result = default;
            Exception error = null;
            var thread = new Thread(() => { try { result = op(); } catch (Exception e) { error = e; } }) { IsBackground = true, Name = "SeekFidelity.WithTimeout" };
            thread.Start();
            if (!thread.Join(timeoutMs))
            {
                Assert.Fail($"operation did not return within {timeoutMs} ms (seek/read is spinning)");
            }
            if (error != null)
            {
                throw new Xunit.Sdk.XunitException($"operation threw {error.GetType().Name}: {error.Message}", error);
            }
            return result;
        }

        public static float[] LinearDecode(string path, out int channels)
        {
            using var reader = new VorbisReader(path);
            reader.ClipSamples = false;
            channels = reader.Channels;
            var all = new float[reader.TotalSamples * channels];
            var read = 0;
            int count;
            while (read < all.Length && (count = reader.ReadSamples(all, read, all.Length - read)) > 0)
            {
                read += count;
            }
            if (read < all.Length) Array.Resize(ref all, read);   // TotalSamples over-promised (e.g. a trimmed tail): compare against what exists
            return all;
        }

        /// <summary>Seeks, checks the landing position, reads a probe and compares it sample-for-sample with the linear decode at the LANDED position.</summary>
        public static void SeekAndAssertProbeMatches(VorbisReader reader, float[] linear, int channels, long target, int probeLength)
        {
            var problem = TryProbe(reader, linear, channels, target, probeLength);
            if (problem != null) Assert.Fail(problem);
        }

        /// <summary>Same as <see cref="SeekAndAssertProbeMatches"/> but returns the problem text (null = OK) so a caller can report EVERY failing target at once.</summary>
        public static string TryProbe(VorbisReader reader, float[] linear, int channels, long target, int probeLength)
        {
            var total = linear.Length / channels;
            WithTimeout(() => { reader.SeekTo(target, SeekOrigin.Begin); return 0; });
            var landed = reader.SamplePosition;

            // A stream with a granule hole (issue #39) may legitimately snap FORWARD to the first existing sample; never backward.
            if (!(landed >= target && landed <= total)) return $"seek to {target} landed at {landed} (total {total})";
            if (landed > target && target == total) return null;

            var want = (int)Math.Min(probeLength, total - landed) * channels;
            var probe = new float[want];
            var got = WithTimeout(() =>
            {
                var n = 0; int c;
                while (n < want && (c = reader.ReadSamples(probe, n, want - n)) > 0) n += c;
                return n;
            });
            if (got != want) return $"seek to {target}: read {got} of {want} floats";
            int firstBad = -1, badCount = 0; float maxDiff = 0;
            for (var i = 0; i < want; i++)
            {
                var diff = Math.Abs(linear[landed * channels + i] - probe[i]);
                if (diff > 1e-5f) { if (firstBad < 0) firstBad = i; badCount++; }
                if (diff > maxDiff) maxDiff = diff;
            }
            if (firstBad >= 0)
                return $"seek to {target} (landed {landed}): {badCount}/{want} samples differ from the linear decode, first at frame {firstBad / channels} (linear {linear[landed * channels + firstBad]} vs seek {probe[firstBad]}), max |diff| {maxDiff}";
            if (reader.SamplePosition != landed + want / channels)
                return $"seek to {target}: SamplePosition {reader.SamplePosition} after reading {want / channels} frames from {landed}";
            return null;
        }

        /// <summary>Targets from the file's own page table: the stream start, the interior of the first data page, every page boundary +/-1, every page interior, the last sample.</summary>
        public static List<long> DerivedTargets(string path, long total)
        {
            var pages = OggPageTable.Read(path);
            // page granules are on the stream's own timeline; TotalSamples is 0-based from the stream start
            var offset = pages.LastGranule - total;
            var set = new SortedSet<long> { 0, 1, 5 };
            long prevEnd = 0;
            foreach (var end in pages.DataPageEndGranules)
            {
                var start = prevEnd;
                set.Add(start - offset); set.Add(start + 1 - offset); set.Add(end - 1 - offset); set.Add(end - offset);
                set.Add(start + (end - start) / 3 - offset); set.Add(start + 2 * (end - start) / 3 - offset);
                prevEnd = end;
            }
            // extra interior points on the first data page: the class-3 regression region
            var firstEnd = pages.FirstDataPageEndGranule - offset;
            foreach (var t in new long[] { 64, 128, 256, 512, 640, 768, 1024, 1025, 2048, 4096 }) if (t < firstEnd) set.Add(t);
            set.Add(total - 1);
            var list = new List<long>();
            foreach (var t in set) if (t >= 0 && t < total) list.Add(t);
            return list;
        }
    }

    /// <summary>Minimal Ogg page walker (RFC 3533) for the FIRST logical stream: granule positions of the pages that complete audio.</summary>
    internal sealed class OggPageTable
    {
        public List<long> DataPageEndGranules { get; } = new List<long>();
        public int DataPageCount => DataPageEndGranules.Count;
        public long FirstDataPageEndGranule => DataPageEndGranules.Count > 0 ? DataPageEndGranules[0] : 0;
        public long LastGranule => DataPageEndGranules.Count > 0 ? DataPageEndGranules[DataPageEndGranules.Count - 1] : 0;

        public static OggPageTable Read(string path)
        {
            var b = File.ReadAllBytes(path);
            var table = new OggPageTable();
            uint? serial = null;
            var pos = 0;
            while (pos + 27 <= b.Length && b[pos] == 'O' && b[pos + 1] == 'g' && b[pos + 2] == 'g' && b[pos + 3] == 'S')
            {
                var flags = b[pos + 5];
                var granule = BitConverter.ToInt64(b, pos + 6);
                var pageSerial = BitConverter.ToUInt32(b, pos + 14);
                int segments = b[pos + 26];
                if (pos + 27 + segments > b.Length) break;
                var body = 0;
                for (var i = 0; i < segments; i++) body += b[pos + 27 + i];
                serial ??= pageSerial;
                if (pageSerial == serial.Value)
                {
                    // granule -1 = page completes no packet; 0 = header pages (or no audio yet)
                    if (granule > 0 && (table.DataPageEndGranules.Count == 0 || granule > table.LastGranule)) table.DataPageEndGranules.Add(granule);
                    if ((flags & 0x04) != 0) break;   // EOS of the first logical stream: ignore chained streams
                }
                pos += 27 + segments + body;
            }
            return table;
        }
    }
}