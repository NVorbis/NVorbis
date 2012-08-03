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
    abstract class VorbisResidue
    {
        internal static VorbisResidue Init(VorbisReader vorbis, OggPacket reader)
        {
            var type = (int)reader.ReadBits(16);

            VorbisResidue residue = null;
            switch (type)
            {
                case 0: residue = new Residue0(vorbis); break;
                case 1: residue = new Residue1(vorbis); break;
                case 2: residue = new Residue2(vorbis); break;
            }
            if (residue == null) throw new InvalidDataException();

            residue.Init(reader);
            return residue;
        }

        VorbisReader _vorbis;

        protected VorbisResidue(VorbisReader vorbis)
        {
            _vorbis = vorbis;
        }

        abstract internal float[][] Decode(OggPacket reader, bool[] doNotDecode, int channels, int blockSize);

        abstract protected void Init(OggPacket reader);

        class Residue0 : VorbisResidue
        {
            internal Residue0(VorbisReader vorbis) : base(vorbis) { }

            protected override void Init(OggPacket reader)
            {
                _begin = (int)reader.ReadBits(24);
                _end = (int)reader.ReadBits(24);
                _partitionSize = (int)reader.ReadBits(24) + 1;
                _classifications = (int)reader.ReadBits(6) + 1;
                _classBookNum = (int)reader.ReadBits(8);

                _classBook = _vorbis.Books[_classBookNum];

                _cascade = new int[_classifications];
                var acc = 0;
                var maxBits = 0;
                for (int i = 0; i < _classifications; i++)
                {
                    var high_bits = 0;
                    var low_bits = (int)reader.ReadBits(3);
                    if (reader.ReadBit()) high_bits = (int)reader.ReadBits(5);
                    _cascade[i] = high_bits << 3 | low_bits;
                    acc += icount(_cascade[i]);
                    maxBits = Math.Max(Utils.ilog(_cascade[i]), maxBits);
                }
                _maxPasses = maxBits;

                var bookNums = new int[acc];
                for (var i = 0; i < acc; i++)
                {
                    bookNums[i] = (int)reader.ReadBits(8);
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

            internal override float[][] Decode(OggPacket reader, bool[] doNotDecode, int channels, int blockSize)
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
                                        var temp = _classBook.DecodeScalar(reader);
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
                                            WriteVectors(codebook, reader, residue[j], offset, channel: j);
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

            virtual protected void WriteVectors(VorbisCodebook codebook, OggPacket reader, float[] residue, int offset, int channel)
            {
                var step = _nToRead / codebook.Dimensions;

                for (int i = 0; i < step; i++)
                {
                    var j = 0;
                    codebook.DecodeVQ(
                        reader,
                        f =>
                        {
                            residue[offset + i + j++ * step] += f;
                        }
                    );
                }
            }
        }

        class Residue1 : Residue0
        {
            internal Residue1(VorbisReader vorbis) : base(vorbis) { }

            protected override void WriteVectors(VorbisCodebook codebook, OggPacket reader, float[] residue, int offset, int channel)
            {
                for (int i = 0; i < _partitionSize;)
                {
                    codebook.DecodeVQ(
                        reader,
                        f =>
                        {
                            residue[offset + i++] += f;
                        }
                    );
                }
            }
        }

        class Residue2 : Residue1
        {
            internal Residue2(VorbisReader vorbis) : base(vorbis) { }

            internal override float[][] Decode(OggPacket reader, bool[] doNotDecode, int channels, int blockSize)
            {
                var residue = ACache.Get<float>(channels, blockSize);

                if (doNotDecode.Any(p => !p))
                {
                    if (_nToRead > 0)
                    {
                        var cls = ACache.Get<int>(_partWords, _classWordsPerCodeWord);

                        foreach (var pass in Enumerable.Range(0, _maxPasses))
                        {
                            for (int i = 0, l = 0, offset = _begin; i < _partsToRead; l++)
                            {
                                if (pass == 0)
                                {
                                    var temp = _classBook.DecodeScalar(reader);
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
                                            codebook.DecodeVQ(
                                                reader,
                                                f =>
                                                {
                                                    residue[chPtr++][t + c] += f;
                                                    if (chPtr == channels)
                                                    {
                                                        chPtr = 0;
                                                        c++;
                                                    }
                                                }
                                            );
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
