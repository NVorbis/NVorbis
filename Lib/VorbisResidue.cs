/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Linq;
using System.IO;

namespace NVorbis
{
    abstract class VorbisResidue
    {
        internal static VorbisResidue Init(VorbisStreamDecoder vorbis, DataPacket packet)
        {
            var type = (int)packet.ReadBits(16);

            VorbisResidue residue = null;
            switch (type)
            {
                case 0: residue = new Residue0(vorbis); break;
                case 1: residue = new Residue1(vorbis); break;
                case 2: residue = new Residue2(vorbis); break;
            }
            if (residue == null) throw new InvalidDataException();

            residue.Init(packet);
            return residue;
        }

        VorbisStreamDecoder _vorbis;

        protected VorbisResidue(VorbisStreamDecoder vorbis)
        {
            _vorbis = vorbis;
        }

        abstract internal float[][] Decode(DataPacket packet, bool[] doNotDecode, int channels, int blockSize);

        abstract protected void Init(DataPacket packet);

        class Residue0 : VorbisResidue
        {
            internal Residue0(VorbisStreamDecoder vorbis) : base(vorbis) { }

            protected override void Init(DataPacket packet)
            {
                _begin = (int)packet.ReadBits(24);
                _end = (int)packet.ReadBits(24);
                _partitionSize = (int)packet.ReadBits(24) + 1;
                _classifications = (int)packet.ReadBits(6) + 1;
                _classBookNum = (int)packet.ReadBits(8);

                _classBook = _vorbis.Books[_classBookNum];

                _cascade = new int[_classifications];
                var acc = 0;
                var maxBits = 0;
                for (int i = 0; i < _classifications; i++)
                {
                    var high_bits = 0;
                    var low_bits = (int)packet.ReadBits(3);
                    if (packet.ReadBit()) high_bits = (int)packet.ReadBits(5);
                    _cascade[i] = high_bits << 3 | low_bits;
                    acc += icount(_cascade[i]);
                    maxBits = Math.Max(Utils.ilog(_cascade[i]), maxBits);
                }
                _maxPasses = maxBits;

                var bookNums = new int[acc];
                for (var i = 0; i < acc; i++)
                {
                    bookNums[i] = (int)packet.ReadBits(8);
                }

                var bookIdx = 0;
                _books = new VorbisCodebook[_classifications][];
                _bookNums = new int[_classifications][];
                for (int i = 0; i < _classifications; i++)
                {
                    _books[i] = new VorbisCodebook[8];
                    _bookNums[i] = new int[8];
                    for (int j = 1, idx = 0; j < 256; j <<= 1, idx++)
                    {
                        if ((_cascade[i] & j) == j)
                        {
                            var bookNum = bookNums[bookIdx++];
                            _books[i][idx] = _vorbis.Books[bookNum];
                            _bookNums[i][idx] = bookNum;
                            if (_books[i][idx].MapType == 0) throw new InvalidDataException();
                        }
                    }
                }

                _classWordsPerCodeWord = _classBook.Dimensions;
                _nToRead = _end - _begin;
                _partsToRead = _nToRead / _partitionSize;
                _partWords = (_partsToRead + _classWordsPerCodeWord - 1) / _classWordsPerCodeWord;
            }

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

            protected int _begin;
            protected int _end;
            protected int _partitionSize;
            protected int _classifications;
            int _classBookNum;
            protected int _maxPasses;

            protected int[] _cascade;
            int[][] _bookNums;

            protected VorbisCodebook[][] _books;
            protected VorbisCodebook _classBook;

            protected int _classWordsPerCodeWord;
            protected int _nToRead;
            protected int _partsToRead;
            protected int _partWords;

            internal override float[][] Decode(DataPacket packet, bool[] doNotDecode, int channels, int blockSize)
            {
                var residue = ACache.Get<float>(doNotDecode.Length, blockSize);

                if (_nToRead > 0)
                {
                    var cls = ACache.Get<int>(channels, _partsToRead, _classWordsPerCodeWord);

                    foreach (var pass in Enumerable.Range(0, _maxPasses))
                    {
                        for (int i = 0, l = 0, offset = _begin; i < _partsToRead; l++)
                        {
                            if (pass == 0)
                            {
                                for (int j = 0; j < channels; j++)
                                {
                                    if (!doNotDecode[j])
                                    {
                                        var temp = _classBook.DecodeScalar(packet);
                                        for (int pi = _classWordsPerCodeWord - 1; pi >= 0; pi--)
                                        {
                                            cls[j][l][pi] = temp % _classifications;
                                            temp /= _classifications;
                                        }
                                    }
                                }
                            }

                            for (int k = 0; k < _classWordsPerCodeWord && i < _partsToRead; ++k, ++i, offset += _partitionSize)
                            {
                                for (int j = 0; j < channels; ++j)
                                {
                                    if (!doNotDecode[j])
                                    {
                                        var vqclass = cls[j][l][k];
                                        var codebook = _books[vqclass][pass];
                                        if (codebook != null && codebook.MapType != 0)
                                        {
                                            WriteVectors(codebook, packet, residue[j], offset, channel: j);
                                        }
                                    }
                                }
                            }
                        }
                    }

                    ACache.Return(ref cls);
                }

                return residue;
            }

            virtual protected void WriteVectors(VorbisCodebook codebook, DataPacket packet, float[] residue, int offset, int channel)
            {
                var step = _nToRead / codebook.Dimensions;

                for (int i = 0; i < step; i++)
                {
                    var j = 0;
                    var entry = codebook.DecodeScalar(packet);
                    for (var d = 0; d < codebook.Dimensions; d++)
                    {
                        residue[offset + i + j++ * step] += codebook[entry, d];
                    }
                }
            }
        }

        class Residue1 : Residue0
        {
            internal Residue1(VorbisStreamDecoder vorbis) : base(vorbis) { }

            protected override void WriteVectors(VorbisCodebook codebook, DataPacket packet, float[] residue, int offset, int channel)
            {
                for (int i = 0; i < _partitionSize;)
                {
                    var entry = codebook.DecodeScalar(packet);
                    for (var d = 0; d < codebook.Dimensions; d++)
                    {
                        residue[offset + i++] += codebook[entry, d];
                    }
                }
            }
        }

        class Residue2 : Residue1
        {
            internal Residue2(VorbisStreamDecoder vorbis) : base(vorbis) { }

            internal override float[][] Decode(DataPacket packet, bool[] doNotDecode, int channels, int blockSize)
            {
                var residue = ACache.Get<float>(channels, blockSize);

                if (doNotDecode.Contains(false))
                {
                    if (_nToRead > 0)
                    {
                        var cls = ACache.Get<int>(_partWords, _classWordsPerCodeWord);

                        for (int pass = 0; pass < _maxPasses; pass++)
                        {
                            for (int i = 0, l = 0, offset = _begin; i < _partsToRead; l++)
                            {
                                if (pass == 0)
                                {
                                    var temp = _classBook.DecodeScalar(packet);
                                    for (int c = _classWordsPerCodeWord - 1; c >= 0 && temp > 0; c--)
                                    {
                                        cls[l][c] = temp % _classifications;
                                        temp /= _classifications;
                                    }
                                }

                                for (int k = 0; k < _classWordsPerCodeWord && i < _partsToRead; k++)
                                {
                                    var vqclass = cls[l][k];
                                    var codebook = _books[vqclass][pass];
                                    if (codebook != null && codebook.MapType != 0)
                                    {
                                        var chPtr = 0;
                                        var t = offset / channels;

                                        for (int c = 0; c < _partitionSize / channels; )
                                        {
                                            var entry = codebook.DecodeScalar(packet);
                                            for (var d = 0; d < codebook.Dimensions; d++)
                                            {
                                                residue[chPtr++][t + c] += codebook[entry, d];
                                                if (chPtr == channels)
                                                {
                                                    chPtr = 0;
                                                    c++;
                                                }
                                            }
                                        }
                                    }
                                    ++i;
                                    offset += _partitionSize;
                                }
                            }
                        }

                        ACache.Return(ref cls);
                    }
                }

                return residue;
            }
        }
    }
}
