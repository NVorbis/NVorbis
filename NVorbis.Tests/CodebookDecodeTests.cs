using NVorbis;
using NVorbis.Contracts;
using System.Linq;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    public class CodebookDecodeTests
    {
        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;
            public ByteArrayPacket(byte[] data) => _data = data;
            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;
        }

        private static void SetField(object target, string name, object value) =>
            typeof(Codebook).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                            .SetValue(target, value);

        // build a Codebook backed by a real Huffman table generated from the given code lengths
        private static Codebook MakeCodebook(int[] lengths, int[] codes, int[] values)
        {
            var huffman = new Huffman();
            huffman.GenerateTable(values, lengths, codes);

            var cb = new Codebook();
            SetField(cb, "_prefixList", huffman.PrefixTree);
            SetField(cb, "_prefixBitLength", huffman.TableBits);
            SetField(cb, "_overflowList", huffman.OverflowList ?? new System.Collections.Generic.List<HuffmanListNode>());
            SetField(cb, "_maxBits", lengths.Max());
            return cb;
        }

        // ── DecodeScalar: prefix-table hits ──────────────────────────────────

        [Fact]
        public void DecodeScalar_SingleBitCodes_ReturnsMappedValue()
        {
            var cb = MakeCodebook(new[] { 1, 1 }, new[] { 0, 1 }, new[] { 100, 200 });

            Assert.Equal(100, cb.DecodeScalar(new ByteArrayPacket(new byte[] { 0x00 }))); // bit 0
            Assert.Equal(200, cb.DecodeScalar(new ByteArrayPacket(new byte[] { 0x01 }))); // bit 1
        }

        [Fact]
        public void DecodeScalar_AdvancesByCodeLength()
        {
            var cb = MakeCodebook(new[] { 1, 1 }, new[] { 0, 1 }, new[] { 100, 200 });
            var packet = new ByteArrayPacket(new byte[] { 0x00 });
            cb.DecodeScalar(packet);
            Assert.Equal(1, ((IPacket)packet).BitsRead);
        }

        [Fact]
        public void DecodeScalar_EmptyPacket_ReturnsMinusOne()
        {
            var cb = MakeCodebook(new[] { 1, 1 }, new[] { 0, 1 }, new[] { 100, 200 });
            Assert.Equal(-1, cb.DecodeScalar(new ByteArrayPacket(System.Array.Empty<byte>())));
        }

        // ── DecodeScalar: overflow-list walk (code longer than the prefix table) ─

        [Fact]
        public void DecodeScalar_OverflowCode_ResolvesViaOverflowList()
        {
            // length 11 exceeds MAX_TABLE_BITS (10), so the entry lands in the overflow list
            var cb = MakeCodebook(new[] { 11 }, new[] { 0 }, new[] { 42 });

            // 11 zero bits → matches bits=0, mask=(1<<11)-1
            var value = cb.DecodeScalar(new ByteArrayPacket(new byte[] { 0x00, 0x00 }));
            Assert.Equal(42, value);
        }

        [Fact]
        public void DecodeScalar_OverflowCode_AdvancesByFullLength()
        {
            var cb = MakeCodebook(new[] { 11 }, new[] { 0 }, new[] { 42 });
            var packet = new ByteArrayPacket(new byte[] { 0x00, 0x00 });
            cb.DecodeScalar(packet);
            Assert.Equal(11, ((IPacket)packet).BitsRead);
        }

        // ── VQ value indexer ─────────────────────────────────────────────────

        [Fact]
        public void Indexer_ReturnsLookupTableValue()
        {
            var cb = new Codebook();
            typeof(Codebook).GetProperty("Dimensions")!.GetSetMethod(true)!.Invoke(cb, new object[] { 2 });
            SetField(cb, "_lookupTable", new[] { 1f, 2f, 3f, 4f });

            // entry*Dimensions + dim
            Assert.Equal(1f, cb[0, 0]);
            Assert.Equal(2f, cb[0, 1]);
            Assert.Equal(3f, cb[1, 0]);
            Assert.Equal(4f, cb[1, 1]);
        }
    }
}
