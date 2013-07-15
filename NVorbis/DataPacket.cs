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
    /// <summary>
    /// A single data packet from a logical Vorbis stream.
    /// </summary>
    public abstract class DataPacket
    {
        ulong _bitBucket;
        int _bitCount;
        long _readBits;

        /// <summary>
        /// Creates a new instance with the specified length.
        /// </summary>
        /// <param name="length">The length of the packet.</param>
        protected DataPacket(int length)
        {
            Length = length;
        }

        /// <summary>
        /// Reads the next byte of the packet.
        /// </summary>
        /// <returns>The next byte if available, otherwise -1.</returns>
        abstract protected int ReadNextByte();

        /// <summary>
        /// Indicates that the packet has been read and its data is not longer needed.
        /// </summary>
        virtual public void Done()
        {
        }

        /// <summary>
        /// Attempts to read the specified number of bits from the packet, but may return fewer.  Does not advance the position counter.
        /// </summary>
        /// <param name="count">The number of bits to attempt to read.</param>
        /// <param name="bitsRead">The number of bits actually read.</param>
        /// <returns>The value of the bits read.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not between 0 and 64.</exception>
        public ulong TryPeekBits(int count, out int bitsRead)
        {
            ulong value = 0;
            int bitShift = 0;

            if (count < 0 || count > 64) throw new ArgumentOutOfRangeException("count");
            if (count == 0)
            {
                bitsRead = 0;
                return 0UL;
            }

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

        /// <summary>
        /// Reads the specified number of bits from the packet.  Does not advance the position counter.
        /// </summary>
        /// <param name="count">The number of bits to read.</param>
        /// <returns>The value of the bits read.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not between 0 and 64.</exception>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered before reading all the requested bits.</exception>
        public ulong PeekBits(int count)
        {
            int bitsRead;
            var bits = TryPeekBits(count, out bitsRead);
            if (bitsRead < count) throw new EndOfStreamException();
            return bits;
        }

        /// <summary>
        /// Advances the position counter by the specified number of bits.
        /// </summary>
        /// <param name="count">The number of bits to advance.</param>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered before advancing the requested number of bits.</exception>
        public void SkipBits(int count)
        {
            if (count == 0)
            {
                // no-op
            }
            else if (_bitCount > count)
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

        /// <summary>
        /// Gets whether the packet was found after a stream resync.
        /// </summary>
        public bool IsResync { get; internal set; }

        /// <summary>
        /// Gets the position of the last granule in the packet.
        /// </summary>
        public long GranulePosition { get; set; }

        /// <summary>
        /// Gets the position of the last granule in the page the packet is in.
        /// </summary>
        public long PageGranulePosition { get; internal set; }

        /// <summary>
        /// Gets the length of the packet.
        /// </summary>
        public int Length { get; protected set; }

        /// <summary>
        /// Gets whether the packet is the last one in the logical stream.
        /// </summary>
        public bool IsEndOfStream { get; internal set; }

        /// <summary>
        /// Gets the number of bits read from the packet.
        /// </summary>
        public long BitsRead { get { return _readBits; } }

        /// <summary>
        /// Gets the number of granules in the packet.  If <c>null</c>, the packet has not been decoded yet.
        /// </summary>
        public long? GranuleCount { get; set; }

        internal int PageSequenceNumber { get; set; }

        /// <summary>
        /// Reads the specified number of bits from the packet and advances the position counter.
        /// </summary>
        /// <param name="count">The number of bits to read.</param>
        /// <returns>The value of the bits read.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The number of bits specified is not between 0 and 64.</exception>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered before reading all the requested bits.</exception>
        public ulong ReadBits(int count)
        {
            // short-circuit 0
            if (count == 0) return 0UL;

            var value = PeekBits(count);

            SkipBits(count);

            return value;
        }

        /// <summary>
        /// Reads the next byte from the packet.  Does not advance the position counter.
        /// </summary>
        /// <returns>The byte read from the packet.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the byte.</exception>
        public byte PeekByte()
        {
            return (byte)PeekBits(8);
        }

        /// <summary>
        /// Reads the next byte from the packet and advances the position counter.
        /// </summary>
        /// <returns>The byte read from the packet.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the byte.</exception>
        public byte ReadByte()
        {
            return (byte)ReadBits(8);
        }

        /// <summary>
        /// Reads the specified number of bytes from the packet and advances the position counter.
        /// </summary>
        /// <param name="count">The number of bytes to read.</param>
        /// <returns>A byte array holding the data read.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered before reading all the requested bytes.</exception>
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

        /// <summary>
        /// Reads the specified number of bytes from the packet into the buffer specified and advances the position counter.
        /// </summary>
        /// <param name="buffer">The buffer to read into.</param>
        /// <param name="index">The index into the buffer to start placing the read data.</param>
        /// <param name="count">The number of bytes to read.</param>
        /// <returns>The number of bytes read.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is less than 0 or <paramref name="index"/> + <paramref name="count"/> is past the end of <paramref name="buffer"/>.</exception>
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

        /// <summary>
        /// Reads the next bit from the packet and advances the position counter.
        /// </summary>
        /// <returns>The value of the bit read.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while trying to read the bit.</exception>
        public bool ReadBit()
        {
            return ReadBits(1) == 1;
        }

        /// <summary>
        /// Retrieves the next 16 bits from the packet as a <see cref="short"/> and advances the position counter.
        /// </summary>
        /// <returns>The value of the next 16 bits.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the bits.</exception>
        public short ReadInt16()
        {
            return (short)ReadBits(16);
        }

        /// <summary>
        /// Retrieves the next 32 bits from the packet as a <see cref="int"/> and advances the position counter.
        /// </summary>
        /// <returns>The value of the next 32 bits.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the bits.</exception>
        public int ReadInt32()
        {
            return (int)ReadBits(32);
        }

        /// <summary>
        /// Retrieves the next 64 bits from the packet as a <see cref="long"/> and advances the position counter.
        /// </summary>
        /// <returns>The value of the next 64 bits.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the bits.</exception>
        public long ReadInt64()
        {
            return (long)ReadBits(64);
        }

        /// <summary>
        /// Retrieves the next 16 bits from the packet as a <see cref="ushort"/> and advances the position counter.
        /// </summary>
        /// <returns>The value of the next 16 bits.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the bits.</exception>
        public ushort ReadUInt16()
        {
            return (ushort)ReadBits(16);
        }

        /// <summary>
        /// Retrieves the next 32 bits from the packet as a <see cref="uint"/> and advances the position counter.
        /// </summary>
        /// <returns>The value of the next 32 bits.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the bits.</exception>
        public uint ReadUInt32()
        {
            return (uint)ReadBits(32);
        }

        /// <summary>
        /// Retrieves the next 64 bits from the packet as a <see cref="ulong"/> and advances the position counter.
        /// </summary>
        /// <returns>The value of the next 64 bits.</returns>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while reading the bits.</exception>
        public ulong ReadUInt64()
        {
            return (ulong)ReadBits(64);
        }

        /// <summary>
        /// Advances the position counter by the specified number of bytes.
        /// </summary>
        /// <param name="count">The number of bytes to advance.</param>
        /// <exception cref="EndOfStreamException">The end of the packet was encountered while advancing.</exception>
        public void SkipBytes(int count)
        {
            SkipBits(count * 8);
        }
    }
}
