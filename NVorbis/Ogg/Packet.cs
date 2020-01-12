using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    internal class Packet : IPacket, IEquatable<Packet>
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

        IPageReader _reader;                // IntPtr
        IPacketReader _packetProvider;      // IntPtr
        IList<Tuple<long, int>> _dataSrc;   // IntPtr + segment_count * 12
        int _dataIndex;                     // 4
        int _dataOfs;                       // 4
        byte[] _dataBuf;                    // IntPtr + cur_segment_size

        ulong _bitBucket;           // 8
        int _bitCount;              // 4
        byte _overflowBits;         // 1
        PacketFlags _packetFlags;   // 1
        int _readBits;              // 4
        int _containerOverheadBits; // 4

        internal Packet(IPageReader reader, IPacketReader packetProvider, int pageIndex, int index, IList<Tuple<long, int>> data)
        {
            _reader = reader;
            _packetProvider = packetProvider;
            PageIndex = pageIndex;
            Index = index;
            _dataSrc = data;
        }

        public void Reset()
        {
            _dataIndex = 0;
            _dataOfs = 0;
            _dataBuf = null;
        }

        public void Done()
        {
            _packetProvider.InvalidatePacketCache(this);
            _dataBuf = null;
        }

        internal int Index { get; }
        internal int PageIndex { get; }

        public bool IsResync
        {
            get => GetFlag(PacketFlags.IsResync);
            internal set => SetFlag(PacketFlags.IsResync, value);
        }

        public bool IsShort
        {
            get => GetFlag(PacketFlags.IsShort);
            private set => SetFlag(PacketFlags.IsShort, value);
        }

        public long GranulePosition { get; set; }

        public long PageGranulePosition { get; internal set; }

        public bool IsParameterChange
        {
            get => GetFlag(PacketFlags.IsParameterChange);
            internal set => SetFlag(PacketFlags.IsParameterChange, value);
        }

        public bool IsEndOfStream
        {
            get => GetFlag(PacketFlags.IsEndOfStream);
            internal set => SetFlag(PacketFlags.IsEndOfStream, value);
        }

        public int BitsRead => _readBits;

        public int BitsRemaining
        {
            get
            {
                var ttl = _dataSrc[0].Item2;
                for (var i = 1; i < _dataSrc.Count; i++)
                {
                    ttl += _dataSrc[i].Item2;
                }
                return ttl * 8 - _readBits;
            }
        }

        public int ContainerOverheadBits
        {
            get => _containerOverheadBits;
            internal set => _containerOverheadBits = value;
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

        public ulong ReadBits(int count)
        {
            // short-circuit 0
            if (count == 0) return 0UL;

            var value = TryPeekBits(count, out _);

            SkipBits(count);

            return value;
        }

        public int Read(byte[] buffer, int index, int count)
        {
            if (index < 0 || index >= buffer.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (count < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
            {
                var value = (byte)TryPeekBits(8, out var bitsRead);
                if (bitsRead == 0)
                {
                    return i;
                }
                buffer[index++] = value;
                SkipBits(8);
            }
            return count;
        }

        public bool ReadBit() => 1 == ReadBits(1);

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

        int ReadNextByte()
        {
            if (_dataIndex == _dataSrc.Count) return -1;

            if (_dataOfs == 0)
            {
                var ofs = _dataSrc[_dataIndex].Item1;
                _dataBuf = new byte[_dataSrc[_dataIndex].Item2];

                var idx = 0;
                int cnt;
                while (idx < _dataBuf.Length && (cnt = _reader.Read(ofs + idx, _dataBuf, idx, _dataBuf.Length - idx)) > 0)
                {
                    idx += cnt;
                }
                if (idx < _dataBuf.Length)
                {
                    // uh-oh...  bad packet
                    _dataBuf = null;
                    _dataIndex = _dataSrc.Count;
                    return -1;
                }
            }

            var b = _dataBuf[_dataOfs];

            if (++_dataOfs == _dataSrc[_dataIndex].Item2)
            {
                _dataOfs = 0;
                if (++_dataIndex == _dataSrc.Count)
                {
                    _dataBuf = null;
                }
            }

            return b;
        }

        bool IEquatable<Packet>.Equals(Packet other)
        {
            return PageIndex == other.PageIndex && Index == other.Index;
        }
    }
}