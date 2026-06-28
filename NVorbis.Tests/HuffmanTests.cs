using System.Linq;
using Xunit;

namespace NVorbis.Tests
{
    public class HuffmanTests
    {
        private static Huffman Generate(int[] lengths, int[] codes, int[] values = null)
        {
            var h = new Huffman();
            h.GenerateTable(values ?? Enumerable.Range(0, lengths.Length).ToArray(), lengths, codes);
            return h;
        }

        [Fact]
        public void TableBits_EqualsMaxLength_WhenUnderCap()
        {
            var h = Generate(new[] { 3 }, new[] { 0 });
            Assert.Equal(3, h.TableBits);
            Assert.Equal(1 << 3, h.PrefixTree.Count);
        }

        [Fact]
        public void PrefixTree_MapsCodesToValues()
        {
            // two 1-bit codes: code 0 → value 10, code 1 → value 20
            var h = Generate(new[] { 1, 1 }, new[] { 0, 1 }, new[] { 10, 20 });
            Assert.Equal(1, h.TableBits);
            Assert.Equal(2, h.PrefixTree.Count);
            Assert.Equal(10, h.PrefixTree[0].Value);
            Assert.Equal(20, h.PrefixTree[1].Value);
        }

        [Fact]
        public void PrefixTree_ShorterCode_FillsMultipleSlots()
        {
            // one 1-bit code (bits=0) in a 2-bit table fills both slots whose low bit is 0
            var h = Generate(new[] { 1, 2 }, new[] { 0, 1 }, new[] { 7, 9 });
            Assert.Equal(2, h.TableBits);
            // idx 0b00 and 0b10 → the length-1 entry (value 7)
            Assert.Equal(7, h.PrefixTree[0].Value);
            Assert.Equal(7, h.PrefixTree[2].Value);
            // idx 0b01 → length-2 entry (value 9)
            Assert.Equal(9, h.PrefixTree[1].Value);
        }

        [Fact]
        public void UnusedEntries_AreSkipped()
        {
            // length <= 0 marks an unused entry; it must not appear in the prefix tree
            var h = Generate(new[] { 1, 0 }, new[] { 0, 0 }, new[] { 5, 99 });
            Assert.DoesNotContain(h.PrefixTree.Where(n => n != null), n => n.Value == 99);
            Assert.Contains(h.PrefixTree.Where(n => n != null), n => n.Value == 5);
        }

        [Fact]
        public void AllUnused_ProducesEmptyTable()
        {
            var h = Generate(new[] { 0, 0 }, new[] { 0, 0 });
            Assert.Equal(0, h.TableBits);
            Assert.Single(h.PrefixTree); // 1 << 0
            Assert.Null(h.OverflowList);
        }

        [Fact]
        public void LengthOverCap_GoesToOverflowList()
        {
            // MAX_TABLE_BITS is 10; an 11-bit code can't fit the prefix table
            var h = Generate(new[] { 11 }, new[] { 0 }, new[] { 42 });
            Assert.Equal(10, h.TableBits);
            Assert.NotNull(h.OverflowList);
            Assert.Contains(h.OverflowList, n => n.Value == 42);
        }

        [Fact]
        public void Sorted_ByLengthThenBits()
        {
            // entries provided out of order; prefix fill should still resolve correctly
            var h = Generate(new[] { 2, 1 }, new[] { 1, 0 }, new[] { 100, 200 });
            // value 200 has length 1, bits 0 → fills 0b00 and 0b10
            Assert.Equal(200, h.PrefixTree[0].Value);
            Assert.Equal(200, h.PrefixTree[2].Value);
            // value 100 has length 2, bits 1 → 0b01
            Assert.Equal(100, h.PrefixTree[1].Value);
        }
    }
}
