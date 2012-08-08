/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis
{
    class OggPacket
    {
        Stream _stream;

        List<long> _offsets;
        List<int> _lengths;
        int _curIdx;
        int _curOfs;

        ulong _bitBucket;
        int _bitCount;
        long _readBits;

        internal OggPacket(Stream stream, long startPos, int length)
        {
            _stream = stream;

            _offsets = new List<long>();
            _lengths = new List<int>();
            _curIdx = 0;
            _curOfs = 0;

            _offsets.Add(startPos);
            _lengths.Add(length);

            Length = length;
        }

        internal void MergeWith(OggPacket continuation)
        {
            _offsets.AddRange(continuation._offsets);
            _lengths.AddRange(continuation._lengths);

            Length += continuation.Length;
        }

        public void Reset()
        {
            _curIdx = 0;
            _curOfs = 0;
            _stream.Position = _offsets[0];

            _bitBucket = 0UL;
            _bitCount = 0;
            _readBits = 0L;
        }

        int ReadNextByte()
        {
            if (_curIdx == _offsets.Count) return -1;

            var pos = _offsets[_curIdx] + _curOfs;
            if (_stream.Position != pos) _stream.Seek(pos, SeekOrigin.Begin);
            var b = _stream.ReadByte();
            ++_curOfs;
            if (_curOfs >= _lengths[_curIdx])
            {
                ++_curIdx;
                _curOfs = 0;
            }
            return b;
        }

        public ulong TryPeekBits(int count, out int bitsRead)
        {
            ulong value = 0;
            int bitShift = 0;

            if (count <= 0 || count > 64) throw new ArgumentOutOfRangeException("count");

            if (count + _bitCount > 64)
            {
                bitShift = 8;
                count -= 8;
            }

            while (_bitCount < count)
            {
                var val = ReadNextByte();
                if (val == -1)
                {
                    count = _bitCount;
                    bitShift = 0;
                    break;
                }
                _bitBucket = (ulong)(val & 0xFF) << _bitCount | _bitBucket;
                _bitCount += 8;
            }

            value = _bitBucket;

            if (count < 64)
            {
                value &= (1UL << count) - 1;
            }

            if (bitShift > 0)
            {
                value |= PeekBits(bitShift) << (count - bitShift);
            }

            bitsRead = count;
            return value;
        }

        public ulong PeekBits(int count)
        {
            int bitsRead;
            var bits = TryPeekBits(count, out bitsRead);
            if (bitsRead < count) throw new EndOfStreamException();
            return bits;
        }

        public void SkipBits(int count)
        {
            if (_bitCount > count)
            {
                // we still have bits left over...
                _bitBucket >>= count;
                _bitCount -= count;
                _readBits += count;
            }
            else if (_bitCount == count)
            {
                _bitBucket = 0UL;
                _bitCount = 0;
                _readBits += count;
            }
            else //  _bitCount < count
            {
                // we have to move more bits than we have available...
                count -= _bitCount;
                _readBits += _bitCount;
                _bitCount = 0;

                while (count > 8)
                {
                    if (ReadNextByte() == -1) throw new EndOfStreamException();
                    count -= 8;
                    _readBits += 8;
                }

                if (count > 0)
                {
                    PeekBits(count);
                    _bitBucket >>= count;
                    _bitCount -= count;
                    _readBits += count;
                }
            }
        }


        public bool IsResync { get; internal set; }
        public long GranulePosition { get; set; }
        public long PageGranulePosition { get; internal set; }
        public int Length { get; private set; }
        public bool IsEndOfStream { get; internal set; }

        public long BitsRead { get { return _readBits; } }

        internal bool IsContinued { get; set; }
        internal bool IsContinuation { get; set; }
        internal int PageSequenceNumber { get; set; }


        public ulong ReadBits(int count)
        {
            if (count < 0 || count > 64) throw new ArgumentOutOfRangeException("count");

            var value = PeekBits(count);

            SkipBits(count);

            return value;
        }

        public byte PeekByte()
        {
            return (byte)PeekBits(8);
        }

        public byte ReadByte()
        {
            return (byte)ReadBits(8);
        }

        public byte[] ReadBytes(int count)
        {
            var buf = new List<byte>(count);

            while (buf.Count < count)
            {
                try
                {
                    buf.Add(ReadByte());
                }
                catch (EndOfStreamException)
                {
                    break;
                }
            }

            return buf.ToArray();
        }

        public int Read(byte[] buffer, int index, int count)
        {
            if (index < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException("index");
            for (int i = 0; i < count; i++)
            {
                try
                {
                    buffer[index++] = ReadByte();
                }
                catch (EndOfStreamException)
                {
                    return i;
                }
            }
            return count;
        }

        public bool ReadBit()
        {
            return ReadBits(1) == 1;
        }

        public float ReadVorbisFloat()
        {
            var temp = ReadBits(32);
            var mantissa = (float)(temp & 0x1fffff);
            var exponent = (float)((temp & 0x7fe00000) >> 21);
            if ((temp & 0x80000000) != 0) mantissa *= -1;
            return (float)((double)mantissa * Math.Pow(2, exponent - 788));
        }

        public short ReadInt16()
        {
            return (short)ReadBits(16);
        }

        public int ReadInt32()
        {
            return (int)ReadBits(32);
        }

        public ulong ReadInt64()
        {
            return (ulong)ReadBits(64);
        }

        public ushort ReadUInt16()
        {
            return (ushort)ReadBits(16);
        }

        public uint ReadUInt32()
        {
            return (uint)ReadBits(32);
        }

        public ulong ReadUInt64()
        {
            return (ulong)ReadBits(64);
        }

        public void SkipBytes(int count)
        {
            SkipBits(count * 8);
        }
    }
}
