/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (LGPL).                                    *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Linq;
using System.IO;

namespace NVorbis
{
    class VorbisCodebook
    {
        internal static VorbisCodebook Init(VorbisReader vorbis, OggPacket reader, int number)
        {
            var temp = new VorbisCodebook();
            temp.BookNum = number;
            temp.Init(reader);
            return temp;
        }

        private VorbisCodebook()
        {

        }

        internal void Init(OggPacket reader)
        {
            // first, check the sync pattern
            var chkVal = reader.ReadBits(24);
            if (chkVal != 0x564342UL) throw new InvalidDataException();

            // get the counts
            Dimensions = (int)reader.ReadBits(16);
            Entries = (int)reader.ReadBits(24);
            
            // init the storage
            Lengths = new int[Entries];

            InitLengthList(reader);
            InitTree();
            InitLookupTable(reader);
        }

        void InitLengthList(OggPacket reader)
        {
            var ordered = reader.ReadBit();
            if (!ordered)
            {
                var sparse = reader.ReadBit();
                for (int i = 0; i < Entries; i++)
                {
                    // if we're spare, read the flag bit, otherwise just grab the next value
                    if (!sparse || reader.ReadBit())
                    {
                        Lengths[i] = (int)reader.ReadBits(5) + 1;
                    }
                    else
                    {
                        Lengths[i] = 0;
                    }
                }
            }
            else
            {
                var curLen = (int)reader.ReadBits(5) + 1;
                for (var i = 0; i < Entries; )
                {
                    var num = (int)reader.ReadBits(Utils.ilog(Entries - i));

                    for (var j = 0; j < num; j++, i++)
                    {
                        Lengths[i] = curLen;
                    }

                    ++curLen;
                }
            }
        }

        void InitTree()
        {
            LTree = Huffman.BuildLinkedList(Enumerable.Range(0, Entries).ToArray(), Lengths);
            MaxBits = Lengths.Max();
        }

        void InitLookupTable(OggPacket reader)
        {
            MapType = (int)reader.ReadBits(4);
            if (MapType == 0) return;

            var minValue = reader.ReadVorbisFloat();
            var deltaValue = reader.ReadVorbisFloat();
            var valueBits = (int)reader.ReadBits(4) + 1;
            var sequence_p = reader.ReadBit();

            var lookupValueCount = Entries * Dimensions;
            var lookupTable = new float[lookupValueCount];
            if (MapType == 1)
            {
                lookupValueCount = lookup1_values();
            }

            var multiplicands = new uint[lookupValueCount];
            for (var i = 0; i < lookupValueCount; i++)
            {
                multiplicands[i] = (uint)reader.ReadBits(valueBits);
            }

            // now that we have the initial data read in, calculate the entry tree
            if (MapType == 1)
            {
                for (var idx = 0; idx < Entries; idx++)
                {
                    var last = 0.0;
                    var idxDiv = 1;
                    for (var i = 0; i < Dimensions; i++)
                    {
                        var moff = (idx / idxDiv) % lookupValueCount;
                        var value = (float)multiplicands[moff] * deltaValue + minValue + last;
                        lookupTable[idx * Dimensions + i] = (float)value;

                        if (sequence_p) last = value;

                        idxDiv *= lookupValueCount;
                    }
                }
            }
            else
            {
                for (var idx = 0; idx < Entries; idx++)
                {
                    var last = 0.0;
                    var moff = idx * Dimensions;
                    for (var i = 0; i < Dimensions; i++)
                    {
                        var value = multiplicands[moff] * deltaValue + minValue + last;
                        lookupTable[idx * Dimensions + i] = (float)value;

                        if (sequence_p) last = value;

                        ++moff;
                    }
                }
            }

            LookupTable = lookupTable;
        }

        int lookup1_values()
        {
            return (int)Math.Floor(Math.Pow(Entries, 1.0 / Dimensions));
        }

        internal int BookNum;

        internal int Dimensions;

        int Entries;

        int[] Lengths;

        float[] LookupTable;

        internal int MapType;

        HuffmanListNode<int> LTree;
        int MaxBits;

        internal void DecodeVQ(OggPacket packet, Action<float> writeValue)
        {
            var entry = DecodeScalar(packet);
            for (int ofs = entry * Dimensions, i = 0; i < Dimensions; ofs++, i++)
            {
                writeValue(LookupTable[ofs]);
            }
        }

        internal int DecodeScalar(OggPacket packet)
        {
            // try to get as many bits as possible...
            int bitCnt; // we really don't care how many bits were read; try to decode anyway...
            var bits = (int)packet.TryPeekBits(MaxBits, out bitCnt);
            if (bitCnt == 0) throw new InvalidDataException();

            // now go through the list and find the matching entry
            var node = LTree;
            while (node != null)
            {
                if (node.Bits == (bits & node.Mask))
                {
                    packet.SkipBits(node.Length);
                    return node.Value;
                }
                node = node.Next;
            }
            throw new InvalidDataException();
        }
    }
}
