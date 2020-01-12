using System;
using System.Collections.Generic;

namespace NVorbis.Contracts
{
    interface IHuffman
    {
        int TableBits { get; }
        IReadOnlyList<HuffmanListNode> PrefixTree { get; }
        HuffmanListNode OverflowNode { get; }

        void GenerateTable(IReadOnlyList<int> value, int[] lengthList, int[] codeList);
    }
}
