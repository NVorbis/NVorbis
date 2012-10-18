/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;

namespace NVorbis
{
    static class Huffman
    {
        static internal HuffmanListNode<T> BuildLinkedList<T>(T[] values, int[] lengthList, int[] codeList)
        {
            HuffmanListNode<T>[] list = new HuffmanListNode<T>[lengthList.Length];

            for (int i = 0; i < list.Length; i++)
            {
                list[i] = new HuffmanListNode<T>
                {
                    Value = values[i],
                    Length = lengthList[i] <= 0 ? 99999 : lengthList[i],
                    Bits = codeList[i],
                    Mask = (1 << lengthList[i]) - 1,
                };
            }

            Array.Sort(
                list,
                (i1, i2) =>
                {
                    var len = i1.Length - i2.Length;
                    if (len == 0)
                    {
                        return i1.Bits - i2.Bits;
                    }
                    return len;
                }
            );

            for (int i = 1; i < list.Length && list[i].Length < 99999; i++)
            {
                list[i - 1].Next = list[i];
            }

            return list[0];
        }
    }

    class HuffmanListNode<T>
    {
        internal T Value;

        internal int Length;
        internal int Bits;
        internal int Mask;

        internal HuffmanListNode<T> Next;

        public int HitCount { get; set; }
    }
}
