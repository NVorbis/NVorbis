/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2013, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;

namespace NVorbis
{
    static class Huffman
    {
        static internal HuffmanListNode BuildLinkedList(int[] values, int[] lengthList, int[] codeList)
        {
            HuffmanListNode[] list = new HuffmanListNode[lengthList.Length];

            for (int i = 0; i < list.Length; i++)
            {
                list[i] = new HuffmanListNode
                {
                    Value = values[i],
                    Length = lengthList[i] <= 0 ? 99999 : lengthList[i],
                    Bits = codeList[i],
                    Mask = (1 << lengthList[i]) - 1,
                };
            }

            Array.Sort(list, SortCallback);

            for (int i = 1; i < list.Length && list[i].Length < 99999; i++)
            {
                list[i - 1].Next = list[i];
            }

            return list[0];
        }

        static int SortCallback(HuffmanListNode i1, HuffmanListNode i2)
        {
            var len = i1.Length - i2.Length;
            if (len == 0)
            {
                return i1.Bits - i2.Bits;
            }
            return len;
        }
    }

    class HuffmanListNode
    {
        internal int Value;

        internal int Length;
        internal int Bits;
        internal int Mask;

        internal HuffmanListNode Next;

        public int HitCount { get; set; }
    }
}
