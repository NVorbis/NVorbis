using NVorbis.Contracts;
using System;
using System.IO;

namespace NVorbis
{
    class Residue0 : IResidue
    {
        static int icount(int v)
        {
            var ret = 0;
            while (v != 0)
            {
                ret += (v & 1);
                v >>= 1;
            }
            return ret;
        }

        int _channels;
        int _begin;
        int _end;
        int _partitionSize;
        int _classifications;
        int _maxStages;

        ICodebook[][] _books;
        ICodebook _classBook;

        int[] _cascade, _entryCache;
        int[][] _decodeMap;
        int[][][] _partWordCache;


        virtual public void Init(IPacket packet, int channels, ICodebook[] codebooks)
        {
            // this is pretty well stolen directly from libvorbis...  BSD license
            _begin = (int)packet.ReadBits(24);
            _end = (int)packet.ReadBits(24);
            _partitionSize = (int)packet.ReadBits(24) + 1;
            _classifications = (int)packet.ReadBits(6) + 1;
            _classBook = codebooks[(int)packet.ReadBits(8)];

            _cascade = new int[_classifications];
            var acc = 0;
            for (int i = 0; i < _classifications; i++)
            {
                var low_bits = (int)packet.ReadBits(3);
                if (packet.ReadBit())
                {
                    _cascade[i] = (int)packet.ReadBits(5) << 3 | low_bits;
                }
                else
                {
                    _cascade[i] = low_bits;
                }
                acc += icount(_cascade[i]);
            }

            var bookNums = new int[acc];
            for (var i = 0; i < acc; i++)
            {
                bookNums[i] = (int)packet.ReadBits(8);
                if (codebooks[bookNums[i]].MapType == 0) throw new InvalidDataException();
            }

            var entries = _classBook.Entries;
            var dim = _classBook.Dimensions;
            var partvals = 1;
            while (dim > 0)
            {
                partvals *= _classifications;
                if (partvals > entries) throw new InvalidDataException();
                --dim;
            }

            // now the lookups
            _books = new Codebook[_classifications][];

            acc = 0;
            var maxstage = 0;
            int stages;
            for (int j = 0; j < _classifications; j++)
            {
                stages = Utils.ilog(_cascade[j]);
                _books[j] = new Codebook[stages];
                if (stages > 0)
                {
                    maxstage = Math.Max(maxstage, stages);
                    for (int k = 0; k < stages; k++)
                    {
                        if ((_cascade[j] & (1 << k)) > 0)
                        {
                            _books[j][k] = codebooks[bookNums[acc++]];
                        }
                    }
                }
            }
            _maxStages = maxstage;

            _decodeMap = new int[partvals][];
            for (int j = 0; j < partvals; j++)
            {
                var val = j;
                var mult = partvals / _classifications;
                _decodeMap[j] = new int[_classBook.Dimensions];
                for (int k = 0; k < _classBook.Dimensions; k++)
                {
                    var deco = val / mult;
                    val -= deco * mult;
                    mult /= _classifications;
                    _decodeMap[j][k] = deco;
                }
            }

            _entryCache = new int[_partitionSize];

            _partWordCache = new int[channels][][];
            var maxPartWords = ((_end - _begin) / _partitionSize + _classBook.Dimensions - 1) / _classBook.Dimensions;
            for (int ch = 0; ch < channels; ch++)
            {
                _partWordCache[ch] = new int[maxPartWords][];
            }

            _channels = channels;
        }

        virtual public void Decode(IPacket packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer)
        {
            // this is pretty well stolen directly from libvorbis...  BSD license
            var end = _end < blockSize / 2 ? _end : blockSize / 2;
            var n = end - _begin;

            if (n > 0 && Array.IndexOf(doNotDecodeChannel, false) != -1)
            {
                var partVals = n / _partitionSize;

                var partWords = (partVals + _classBook.Dimensions - 1) / _classBook.Dimensions;
                for (int j = 0; j < _channels; j++)
                {
                    Array.Clear(_partWordCache[j], 0, partWords);
                }

                for (int s = 0; s < _maxStages; s++)
                {
                    for (int i = 0, l = 0; i < partVals; l++)
                    {
                        if (s == 0)
                        {
                            for (int j = 0; j < _channels; j++)
                            {
                                var idx = _classBook.DecodeScalar(packet);
                                if (idx >= 0 && idx < _decodeMap.Length)
                                {
                                    _partWordCache[j][l] = _decodeMap[idx];
                                }
                                else
                                {
                                    i = partVals;
                                    s = _maxStages;
                                    break;
                                }
                            }
                        }
                        for (int k = 0; i < partVals && k < _classBook.Dimensions; k++, i++)
                        {
                            var offset = _begin + i * _partitionSize;
                            for (int j = 0; j < _channels; j++)
                            {
                                var idx = _partWordCache[j][l][k];
                                if ((_cascade[idx] & (1 << s)) != 0)
                                {
                                    var book = _books[idx][s];
                                    if (book != null)
                                    {
                                        if (WriteVectors(book, packet, buffer, j, offset, _partitionSize))
                                        {
                                            // bad packet...  exit now and try to use what we already have
                                            i = partVals;
                                            s = _maxStages;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        virtual protected bool WriteVectors(ICodebook codebook, IPacket packet, float[][] residue, int channel, int offset, int partitionSize)
        {
            var res = residue[channel];
            var step = partitionSize / codebook.Dimensions;

            for (int i = 0; i < step; i++)
            {
                if ((_entryCache[i] = codebook.DecodeScalar(packet)) == -1)
                {
                    return true;
                }
            }
            for (int i = 0; i < codebook.Dimensions; i++)
            {
                for (int j = 0; j < step; j++, offset++)
                {
                    res[offset] += codebook[_entryCache[j], i];
                }
            }
            return false;
        }
    }
}
