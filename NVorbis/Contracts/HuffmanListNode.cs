using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis.Contracts
{
    class HuffmanListNode
    {
        internal int Value;

        internal int Length;
        internal int Bits;
        internal int Mask;

        internal HuffmanListNode Next;
    }
}
