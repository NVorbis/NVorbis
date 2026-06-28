using NVorbis;
using NVorbis.Contracts;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    public class ResidueWriteVectorsTests
    {
        // Codebook stub: DecodeScalar returns a scripted entry sequence (ignoring the
        // packet), and the VQ value for (entry, dim) is entry*10 + dim so each
        // contribution is uniquely identifiable in the output buffer.
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
            public float this[int entry, int dim] => entry * 10 + dim;
            public int DecodeScalar(IPacket packet) => _entries.Count > 0 ? _entries.Dequeue() : -1;
        }

        // expose the protected WriteVectors on each concrete residue type
        private class TestResidue0 : Residue0
        {
            public bool Call(ICodebook cb, float[][] res, int channel, int offset, int partitionSize)
                => WriteVectors(cb, null, res, channel, offset, partitionSize);
        }
        private class TestResidue1 : Residue1
        {
            public bool Call(ICodebook cb, float[][] res, int channel, int offset, int partitionSize)
                => WriteVectors(cb, null, res, channel, offset, partitionSize);
        }
        private class TestResidue2 : Residue2
        {
            public TestResidue2(int channels)
            {
                typeof(Residue2).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!
                                .SetValue(this, channels);
            }
            public bool Call(ICodebook cb, float[][] res, int channel, int offset, int partitionSize)
                => WriteVectors(cb, null, res, channel, offset, partitionSize);
        }

        // ── Residue1 (per-channel, dimensions interleaved) ───────────────────

        [Fact]
        public void Residue1_WritesDimensionsContiguously()
        {
            var cb = new FakeCodebook(2, 0, 1);
            var res = new[] { new float[4] };
            var bad = new TestResidue1().Call(cb, res, 0, 0, 4);

            Assert.False(bad);
            // entry 0 → [cb(0,0), cb(0,1)] = [0,1]; entry 1 → [cb(1,0), cb(1,1)] = [10,11]
            Assert.Equal(new float[] { 0, 1, 10, 11 }, res[0]);
        }

        // ── Residue0 (per-channel, dimensions in separate passes) ────────────

        [Fact]
        public void Residue0_WritesDimensionMajor()
        {
            var cb = new FakeCodebook(2, 0, 1);
            var res = new[] { new float[4] };
            var bad = new TestResidue0().Call(cb, res, 0, 0, 4);

            Assert.False(bad);
            // dim-major: [cb(0,0), cb(1,0), cb(0,1), cb(1,1)] = [0,10,1,11]
            Assert.Equal(new float[] { 0, 10, 1, 11 }, res[0]);
        }

        // ── Residue2 (all channels interleaved in one pass) ──────────────────

        [Fact]
        public void Residue2_InterleavesAcrossChannels()
        {
            var cb = new FakeCodebook(2, 0, 1);
            var res = new[] { new float[2], new float[2] };
            var bad = new TestResidue2(2).Call(cb, res, 0, 0, 4);

            Assert.False(bad);
            // value stream cb(0,0),cb(0,1),cb(1,0),cb(1,1) = 0,1,10,11 dealt round-robin by channel
            Assert.Equal(new float[] { 0, 10 }, res[0]);
            Assert.Equal(new float[] { 1, 11 }, res[1]);
        }

        // ── bad-packet propagation (DecodeScalar == -1) ──────────────────────

        [Fact]
        public void Residue1_BadEntry_ReturnsTrueAndStops()
        {
            var cb = new FakeCodebook(2); // empty → DecodeScalar returns -1 immediately
            var res = new[] { new float[4] };
            var bad = new TestResidue1().Call(cb, res, 0, 0, 4);

            Assert.True(bad);
            Assert.Equal(new float[4], res[0]); // nothing written
        }

        [Fact]
        public void Residue0_BadEntry_ReturnsTrue()
        {
            var cb = new FakeCodebook(2);
            var res = new[] { new float[4] };
            Assert.True(new TestResidue0().Call(cb, res, 0, 0, 4));
        }

        [Fact]
        public void Residue2_BadEntry_ReturnsTrue()
        {
            var cb = new FakeCodebook(2);
            var res = new[] { new float[2], new float[2] };
            Assert.True(new TestResidue2(2).Call(cb, res, 0, 0, 4));
        }

        [Fact]
        public void Residue1_AppliesOffset()
        {
            var cb = new FakeCodebook(2, 0);
            var res = new[] { new float[6] };
            new TestResidue1().Call(cb, res, 0, 2, 2);
            // written at offset 2,3; 0,1 and 4,5 untouched
            Assert.Equal(new float[] { 0, 0, 0, 1, 0, 0 }, res[0]);
        }
    }
}
