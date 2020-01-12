using NVorbis.Contracts;
using System;
using System.Collections.Generic;

namespace NVorbis
{
    class Huffman : IHuffman, IComparer<HuffmanListNode>
    {
        const int MAX_TABLE_BITS = 10;

        public int TableBits { get; private set; }
        public IReadOnlyList<HuffmanListNode> PrefixTree { get; private set; }
        public HuffmanListNode OverflowNode { get; private set; }

        public void GenerateTable(IReadOnlyList<int> values, int[] lengthList, int[] codeList)
        {
            HuffmanListNode[] list = new HuffmanListNode[lengthList.Length];

            var maxLen = 0;
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = new HuffmanListNode
                {
                    Value = values[i],
                    Length = lengthList[i] <= 0 ? 99999 : lengthList[i],
                    Bits = codeList[i],
                    Mask = (1 << lengthList[i]) - 1,
                };
                if (lengthList[i] > 0 && maxLen < lengthList[i])
                {
                    maxLen = lengthList[i];
                }
            }

            Array.Sort(list, 0, list.Length, this);

            var tableBits = maxLen > MAX_TABLE_BITS ? MAX_TABLE_BITS : maxLen;

            var prefixList = new List<HuffmanListNode>(1 << tableBits);
            HuffmanListNode firstOverflowNode = null;
            for (int i = 0; i < list.Length && list[i].Length < 99999; i++)
            {
                if (firstOverflowNode == null)
                {
                    var itemBits = list[i].Length;
                    if (itemBits > tableBits)
                    {
                        firstOverflowNode = list[i];
                    }
                    else
                    {
                        var maxVal = 1 << (tableBits - itemBits);
                        var item = list[i];
                        for (int j = 0; j < maxVal; j++)
                        {
                            var idx = (j << itemBits) | item.Bits;
                            while (prefixList.Count <= idx)
                            {
                                prefixList.Add(null);
                            }
                            prefixList[idx] = item;
                        }
                    }
                }
                else
                {
                    list[i - 1].Next = list[i];
                }
            }

            while (prefixList.Count < 1 << tableBits)
            {
                prefixList.Add(null);
            }

            TableBits = tableBits;
            PrefixTree = prefixList;
            OverflowNode = firstOverflowNode;
        }

        int IComparer<HuffmanListNode>.Compare(HuffmanListNode x, HuffmanListNode y)
        {
            var len = x.Length - y.Length;
            if (len == 0)
            {
                return x.Bits - y.Bits;
            }
            return len;
        }
    }
}
