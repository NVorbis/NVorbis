using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis
{
    abstract public class DataPacket : IPacket
    {
        /// <summary>
        /// Defines flags to apply to the current packet
        /// </summary>
        [Flags]
        // for now, let's use a byte... if we find we need more space, we can always expand it...
        protected enum PacketFlags : byte
        {
            /// <summary>
            /// Packet is first since reader had to resync with stream.
            /// </summary>
            IsResync = 0x01,
            /// <summary>
            /// Packet is the last in the logical stream.
            /// </summary>
            IsEndOfStream = 0x02,
            /// <summary>
            /// Packet does not have all its data available.
            /// </summary>
            IsShort = 0x04,
            /// <summary>
            /// Packet is the start of a new header sequence in the stream.
            /// </summary>
            IsParameterChange = 0x08,

            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User0 = 0x10,
            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User1 = 0x20,
            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User2 = 0x40,
            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User3 = 0x80,
        }

        ulong _bitBucket;
        int _bitCount;
        byte _overflowBits;
        PacketFlags _packetFlags;
        int _readBits;

        public int ContainerOverheadBits { get; set; }

        public bool IsParameterChange
        {
            get => GetFlag(PacketFlags.IsParameterChange);
            protected set => SetFlag(PacketFlags.IsParameterChange, value);
        }

        public long? GranulePosition { get; set; }

        public bool IsResync
        {
            get => GetFlag(PacketFlags.IsResync);
            set => SetFlag(PacketFlags.IsResync, value);
        }

        public bool IsShort
        {
            get => GetFlag(PacketFlags.IsShort);
            private set => SetFlag(PacketFlags.IsShort, value);
        }

        public bool IsEndOfStream
        {
            get => GetFlag(PacketFlags.IsEndOfStream);
            set => SetFlag(PacketFlags.IsEndOfStream, value);
        }

        public int BitsRead => _readBits;

        public int BitsRemaining => TotalBits - _readBits;

        abstract protected int TotalBits { get; }

        bool GetFlag(PacketFlags flag) => (_packetFlags & flag) == flag;

        void SetFlag(PacketFlags flag, bool value)
        {
            if (value)
            {
                _packetFlags |= flag;
            }
            else
            {
                _packetFlags &= ~flag;
            }
        }

        abstract protected int ReadNextByte();

        virtual public void Done()
        {
            // no-op for base
        }

        virtual public void Reset()
        {
            _bitBucket = 0;
            _bitCount = 0;
            _overflowBits = 0;
            _readBits = 0;
        }

        ulong IPacket.ReadBits(int count)
        {
            // short-circuit 0
            if (count == 0) return 0UL;

            var value = TryPeekBits(count, out _);

            SkipBits(count);

            return value;
        }

        public ulong TryPeekBits(int count, out int bitsRead)
        {
            if (count < 0 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0)
            {
                bitsRead = 0;
                return 0UL;
            }

            ulong value;
            while (_bitCount < count)
            {
                var val = ReadNextByte();
                if (val == -1)
                {
                    bitsRead = _bitCount;
                    value = _bitBucket;
                    return value;
                }
                _bitBucket = (ulong)(val & 0xFF) << _bitCount | _bitBucket;
                _bitCount += 8;

                if (_bitCount > 64)
                {
                    _overflowBits = (byte)(val >> (72 - _bitCount));
                }
            }

            value = _bitBucket;

            if (count < 64)
            {
                value &= (1UL << count) - 1;
            }

            bitsRead = count;
            return value;
        }

        public void SkipBits(int count)
        {
            if (count > 0)
            {
                if (_bitCount > count)
                {
                    // we still have bits left over...
                    if (count > 63)
                    {
                        _bitBucket = 0;
                    }
                    else
                    {
                        _bitBucket >>= count;
                    }
                    if (_bitCount > 64)
                    {
                        var overflowCount = _bitCount - 64;
                        _bitBucket |= (ulong)_overflowBits << (_bitCount - count - overflowCount);

                        if (overflowCount > count)
                        {
                            // ugh, we have to keep bits in overflow
                            _overflowBits >>= count;
                        }
                    }

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
                    _bitBucket = 0;

                    while (count > 8)
                    {
                        if (ReadNextByte() == -1)
                        {
                            count = 0;
                            IsShort = true;
                            break;
                        }
                        count -= 8;
                        _readBits += 8;
                    }

                    if (count > 0)
                    {
                        var temp = ReadNextByte();
                        if (temp == -1)
                        {
                            IsShort = true;
                        }
                        else
                        {
                            _bitBucket = (ulong)(temp >> count);
                            _bitCount = 8 - count;
                            _readBits += count;
                        }
                    }
                }
            }
        }
    }
}
