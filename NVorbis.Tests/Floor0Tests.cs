using System;
using System.Collections.Generic;
using System.Reflection;
using NVorbis.Contracts;
using Xunit;

namespace NVorbis.Tests
{
    public class Floor0Tests
    {
        static readonly Type _floor0Type =
            typeof(VorbisReader).Assembly.GetType("NVorbis.Floor0")!;

        static readonly MethodInfo _toBark =
            _floor0Type.GetMethod("toBARK", BindingFlags.NonPublic | BindingFlags.Static)!;

        static readonly MethodInfo _synthBark =
            _floor0Type.GetMethod("SynthesizeBarkCurve", BindingFlags.NonPublic | BindingFlags.Instance)!;

        static float ToBark(float lsp) =>
            (float)_toBark.Invoke(null, new object[] { lsp })!;

        static object MakeFloor0(int rate, int barkMapSize)
        {
            var instance = Activator.CreateInstance(_floor0Type, nonPublic: true)!;
            _floor0Type.GetField("_rate", BindingFlags.NonPublic | BindingFlags.Instance)!
                       .SetValue(instance, rate);
            _floor0Type.GetField("_bark_map_size", BindingFlags.NonPublic | BindingFlags.Instance)!
                       .SetValue(instance, barkMapSize);
            return instance;
        }

        static int[] SynthesizeBarkCurve(object instance, int n) =>
            (int[])_synthBark.Invoke(instance, new object[] { n })!;

        // ── toBARK precision ────────────────────────────────────────────────

        [Fact]
        public void ToBark_ReturnType_IsFloat()
        {
            Assert.Equal(typeof(float), _toBark.ReturnType);
        }

        [Fact]
        public void ToBark_HalfNyquist_IsPositive()
        {
            Assert.True(ToBark(11025f) > 0f);
        }

        [Fact]
        public void ToBark_Zero_IsZero()
        {
            Assert.Equal(0f, ToBark(0f));
        }

        [Fact]
        public void ToBark_IsMonotonicallyIncreasing()
        {
            Assert.True(ToBark(11025f) < ToBark(22050f));
        }

        // ── SynthesizeBarkCurve ─────────────────────────────────────────────

        [Fact]
        public void SynthesizeBarkCurve_TerminatorIsMinusOne()
        {
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: 256);
            int n = 512;
            int[] map = SynthesizeBarkCurve(floor0, n);
            Assert.Equal(-1, map[n]);
        }

        [Fact]
        public void SynthesizeBarkCurve_BinsAreNonDecreasing()
        {
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: 256);
            int[] map = SynthesizeBarkCurve(floor0, 512);
            for (int i = 1; i < 511; i++)
            {
                Assert.True(map[i] >= map[i - 1],
                    $"map[{i}]={map[i]} < map[{i - 1}]={map[i - 1]}");
            }
        }

        [Fact]
        public void SynthesizeBarkCurve_AllBinsInRange()
        {
            int barkMapSize = 256;
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: barkMapSize);
            int[] map = SynthesizeBarkCurve(floor0, 512);
            for (int i = 0; i < 511; i++)
            {
                Assert.InRange(map[i], 0, barkMapSize - 1);
            }
        }

        [Fact]
        public void SynthesizeBarkCurve_FirstBinIsZero()
        {
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: 256);
            int[] map = SynthesizeBarkCurve(floor0, 512);
            Assert.Equal(0, map[0]);
        }

        [Fact]
        public void SynthesizeBarkCurve_LastDataBinIsMaxBark()
        {
            int barkMapSize = 256;
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: barkMapSize);
            int n = 512;
            int[] map = SynthesizeBarkCurve(floor0, n);
            Assert.Equal(barkMapSize - 1, map[n - 2]);
        }

        // ── Synthetic Init/Unpack/Apply smoke test ────────────────────────────
        //
        // Floor type 0 is virtually unused in the wild (no known encoder past
        // libvorbis's own beta 4 emits it), so no real
        // .ogg fixture exercises this path. These tests hand-build a minimal,
        // spec-shaped floor0 header/data packet and drive the full Init -> Unpack
        // -> Apply pipeline directly, to catch "throws/crashes/produces NaN" on
        // input libvorbis itself would still decode, without chasing full
        // branch coverage on a code path nothing real ever produces.

        // Packs fields LSB-first within each byte, matching DataPacket's bit order.
        private class BitWriter
        {
            private readonly List<byte> _bytes = new();
            private ulong _bucket;
            private int _bucketBits;

            public BitWriter Write(ulong value, int bits)
            {
                _bucket |= (value & ((1UL << bits) - 1)) << _bucketBits;
                _bucketBits += bits;
                while (_bucketBits >= 8)
                {
                    _bytes.Add((byte)(_bucket & 0xFF));
                    _bucket >>= 8;
                    _bucketBits -= 8;
                }
                return this;
            }

            public byte[] ToArray()
            {
                var result = new List<byte>(_bytes);
                if (_bucketBits > 0)
                {
                    result.Add((byte)(_bucket & 0xFF));
                }
                return result.ToArray();
            }
        }

        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;

            public ByteArrayPacket(byte[] data) => _data = data;

            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;
        }

        // Ignores the packet entirely (mirrors ResidueWriteVectorsTests.FakeCodebook) --
        // Floor0.Unpack only reads the book-selector bits itself; decoding is
        // delegated to ICodebook.DecodeScalar, which this fake short-circuits.
        private class FakeCodebook : ICodebook
        {
            private readonly Queue<int> _entries;

            public FakeCodebook(int dimensions, params int[] entries)
            {
                Dimensions = dimensions;
                _entries = new Queue<int>(entries);
            }

            public int Dimensions { get; }
            public int Entries => 0;
            public int MapType => 1;
            public void Init(IPacket packet, IHuffman huffman) { }
            // Constant per-step delta -- Unpack's "averaging" step accumulates these into a
            // monotonically increasing coefficient set, mimicking the roughly evenly-spaced LSP
            // angles a real encoder would produce (as opposed to values clustered together, which
            // drive this filter's all-pole math toward a singularity -- see comment below).
            public float this[int entry, int dim] => 0.35f;
            public int DecodeScalar(IPacket packet) => _entries.Count > 0 ? _entries.Dequeue() : -1;
        }

        // order=8 (spread across ~0.35..2.8 radians after accumulation, comfortably inside
        // (0, pi) and away from the exact w=+-2 poles Apply hits at the first/last bark bin --
        // see RoundTrip_NonZeroAmplitude_ProducesFiniteResidue), rate=44100, barkMapSize=256,
        // ampBits=6 (0-63), ampOfs=32, 1 codebook (index 0)
        private static byte[] MakeInitPacket() =>
            new BitWriter()
                .Write(8, 8)        // order
                .Write(44100, 16)   // rate
                .Write(256, 16)     // bark_map_size
                .Write(6, 6)        // ampBits
                .Write(32, 8)       // ampOfs
                .Write(0, 4)        // numBooks - 1 (=> 1 book)
                .Write(0, 8)        // codebooks[0] index
                .ToArray();

        private static Floor0 MakeInitializedFloor0(int blockSize, out ICodebook[] codebooks)
        {
            codebooks = new ICodebook[] { new FakeCodebook(dimensions: 1, entries: new[] { 0, 0, 0, 0, 0, 0, 0, 0 }) };
            var floor0 = new Floor0();
            floor0.Init(new ByteArrayPacket(MakeInitPacket()), channels: 1, blockSize, blockSize, codebooks);
            return floor0;
        }

        [Fact]
        public void RoundTrip_NonZeroAmplitude_ProducesFiniteResidue()
        {
            const int blockSize = 1024;
            var floor0 = MakeInitializedFloor0(blockSize, out _);

            // Amp (6 bits) = 40 (> 0, so the book-decode path runs); bookNum (1 bit) = 0.
            var unpackPacket = new ByteArrayPacket(new BitWriter().Write(40, 6).Write(0, 1).ToArray());
            var data = floor0.Unpack(unpackPacket, blockSize, channel: 0);

            var residue = new float[blockSize / 2];
            Array.Fill(residue, 1f);

            floor0.Apply(data, blockSize, residue);

            Assert.All(residue, sample => Assert.True(float.IsFinite(sample), $"non-finite sample: {sample}"));
        }

        [Fact]
        public void RoundTrip_ZeroAmplitude_ZerosResidue()
        {
            const int blockSize = 1024;
            var floor0 = MakeInitializedFloor0(blockSize, out _);

            // Amp (6 bits) = 0 skips the book-decode path entirely.
            var unpackPacket = new ByteArrayPacket(new BitWriter().Write(0, 6).Write(0, 1).ToArray());
            var data = floor0.Unpack(unpackPacket, blockSize, channel: 0);

            var residue = new float[blockSize / 2];
            Array.Fill(residue, 1f);

            floor0.Apply(data, blockSize, residue);

            Assert.All(residue, sample => Assert.Equal(0f, sample));
        }

        [Fact]
        public void Unpack_BookNumberOutOfRange_DegradesToZeroAmpInsteadOfThrowing()
        {
            const int blockSize = 1024;
            var floor0 = MakeInitializedFloor0(blockSize, out var codebooks);

            // Only 1 book exists (bookBits = 1), so a value of 1 is out of range;
            // Floor0.Unpack is documented to treat this as corrupt data and zero the floor.
            var unpackPacket = new ByteArrayPacket(new BitWriter().Write(40, 6).Write(1, 1).ToArray());
            var data = floor0.Unpack(unpackPacket, blockSize, channel: 0);

            Assert.False(data.ExecuteChannel);

            var residue = new float[blockSize / 2];
            Array.Fill(residue, 1f);
            floor0.Apply(data, blockSize, residue);

            Assert.All(residue, sample => Assert.Equal(0f, sample));
        }

        [Theory]
        [InlineData(0, 1, 1)]   // order = 0
        [InlineData(2, 0, 1)]   // rate = 0
        [InlineData(2, 1, 0)]   // bark_map_size = 0
        public void Init_InvalidHeaderFields_ThrowsInvalidDataException(int order, int rate, int barkMapSize)
        {
            var packet = new ByteArrayPacket(
                new BitWriter()
                    .Write((ulong)order, 8)
                    .Write((ulong)rate, 16)
                    .Write((ulong)barkMapSize, 16)
                    .Write(6, 6)
                    .Write(32, 8)
                    .Write(0, 4)
                    .Write(0, 8)
                    .ToArray());

            var codebooks = new ICodebook[] { new FakeCodebook(dimensions: 1) };
            var floor0 = new Floor0();

            Assert.Throws<System.IO.InvalidDataException>(
                () => floor0.Init(packet, channels: 1, block0Size: 1024, block1Size: 1024, codebooks));
        }
    }
}
