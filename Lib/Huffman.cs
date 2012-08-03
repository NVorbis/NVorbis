/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * This Huffman algorithm was inspired by csvorbis, a C# port of Jorbis.    *
 * JOrbis is a Java port of libvorbis.                                      *
 *                                                                          *
 * csvorbis was written by Mark Crichton <crichton@gimp.org>.               *
 * JOrbis was written by the JOrbis team.                                   *
 * libvorbis is Copyright Xiph.org Foundation.                              *
 *                                                                          *
 * Original code written by ymnk <ymnk@jcraft.com> in 2000, and is          *
 * Copyright (C) 2000 ymnk, JCraft, Inc.                                    *
 *                                                                          *
 * See COPYING for license terms (LGPL all the way back to libvorbis).      *
 *                                                                          *
 ***************************************************************************/
using System;

namespace NVorbis
{
    static class Huffman
    {
        static int[] CalculateCodes(int[] lengthList)
        {
            var marker = new int[33];
            var r = new int[lengthList.Length];

            for (var i = 0; i < lengthList.Length; i++)
            {
                var len = lengthList[i];
                if (len > 0)
                {
                    var entry = marker[len];

                    // make sure we're not overpopulating...
                    if (len < 32 && (entry >> len) != 0) return null;

                    r[i] = entry;

                    // adjust the tree to account for the value above
                    for (int j = len; j > 0; j--)
                    {
                        if ((marker[j] & 1) != 0)
                        {
                            if (j == 1)
                            {
                                marker[j]++;
                            }
                            else
                            {
                                marker[j] = marker[j - 1] << 1;
                            }
                            break;
                        }
                        marker[j]++;
                    }

                    // prune the tree
                    for (int j = len + 1; j < 33; j++)
                    {
                        if ((marker[j] >> 1) != entry) break;

                        entry = marker[j];
                        marker[j] = marker[j - 1] << 1;
                    }
                }
            }

            // as a final step, bitreverse the values (so we traverse in the correct direction)...
            for (int i = 0; i < r.Length; i++)
            {
                int temp = 0;
                for (int j = 0; j < lengthList[i]; j++)
                {
                    temp <<= 1;
                    temp |= (r[i] >> j) & 1;
                }
                r[i] = temp;
            }

            return r;
        }

        static internal HuffmanListNode<T> BuildLinkedList<T>(T[] values, int[] lengthList)
        {
            var codeList = CalculateCodes(lengthList);

            HuffmanListNode<T>[] list = new HuffmanListNode<T>[lengthList.Length];

            for (int i = 0; i < list.Length; i++)
            {
                list[i] = new HuffmanListNode<T>
                {
                    Value = values[i],
                    Length = lengthList[i] == 0 ? 99999 : lengthList[i],
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
    }
}
