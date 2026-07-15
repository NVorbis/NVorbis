using NVorbis.Contracts;
using System;
using System.Runtime.CompilerServices;

namespace NVorbis
{
    /// <summary>
    /// Provides a concrete base implementation of <see cref="IPacket"/>.
    /// </summary>
    abstract public class DataPacket : IPacket
    {
        /// <summary>
        /// Defines flags to apply to the current packet
        /// </summary>
        [Flags]
        // Sized as byte deliberately: User0-User4 are reserved for container-specific subclasses (e.g.
        // Ogg.Packet) to stash their own per-packet flags without a second field. Expandable if needed.
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
            /// Flag for use by inheritors.
            /// </summary>
            User0 = 0x08,
            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User1 = 0x10,
            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User2 = 0x20,
            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User3 = 0x40,
            /// <summary>
            /// Flag for use by inheritors.
            /// </summary>
            User4 = 0x80,
        }

        // Hand-rolled bit reader (per-bit hottest path). Bits accumulate LSB-first into a 64-bit bucket;
        // _overflowBits holds the odd byte that would push past 64, so the bucket can carry >64 bits
        // mid-shift without a wider type or an array window.
        ulong _bitBucket;
        int _bitCount;
        byte _overflowBits;
        PacketFlags _packetFlags;
        int _readBits;

        /// <summary>
        /// Gets the number of container overhead bits associated with this packet.
        /// </summary>
        public int ContainerOverheadBits { get; set; }

        /// <summary>
        /// Gets the granule position of the packet, if known.
        /// </summary>
        public long? GranulePosition { get; set; }

        /// <summary>
        /// Gets whether this packet occurs immediately following a loss of sync in the stream.
        /// </summary>
        public bool IsResync
        {
            get => GetFlag(PacketFlags.IsResync);
            set => SetFlag(PacketFlags.IsResync, value);
        }

        /// <summary>
        /// Gets whether this packet did not read its full data.
        /// </summary>
        public bool IsShort
        {
            get => GetFlag(PacketFlags.IsShort);
            private set => SetFlag(PacketFlags.IsShort, value);
        }

        /// <summary>
        /// Gets whether the packet is the last packet of the stream.
        /// </summary>
        public bool IsEndOfStream
        {
            get => GetFlag(PacketFlags.IsEndOfStream);
            set => SetFlag(PacketFlags.IsEndOfStream, value);
        }

        /// <summary>
        /// Gets the number of bits read from the packet.
        /// </summary>
        public int BitsRead => _readBits;

        /// <summary>
        /// Gets the number of bits left in the packet.
        /// </summary>
        public int BitsRemaining => TotalBits - _readBits;

        /// <summary>
        /// Gets the total number of bits in the packet.
        /// </summary>
        abstract protected int TotalBits { get; }

        // bitwise test instead of Enum.HasFlag, which boxes on .NET Framework; this runs per-packet
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

        /// <summary>
        /// Reads the next byte in the packet.
        /// </summary>
        /// <returns>The next byte in the packet, or <c>-1</c> if no more data is available.</returns>
        abstract protected int ReadNextByte();

        /// <summary>
        /// Copies up to <paramref name="destination"/>.Length bytes from the current read position into
        /// <paramref name="destination"/>, returning the count actually copied (<c>0</c> at end of data).
        /// The base implementation loops <see cref="ReadNextByte"/>; subclasses backed by a contiguous
        /// buffer should override with a bulk copy so the bit-reader refill avoids a virtual call and a
        /// span re-materialization per byte.
        /// </summary>
        protected virtual int FetchBytes(Span<byte> destination)
        {
            int count = 0;
            while (count < destination.Length)
            {
                int b = ReadNextByte();
                if (b == -1) break;
                destination[count++] = (byte)b;
            }
            return count;
        }

        /// <summary>
        /// Frees the buffers and caching for the packet instance.
        /// </summary>
        virtual public void Done()
        {
            // no-op for base
        }

        /// <summary>
        /// Resets the read buffers to the beginning of the packet.
        /// </summary>
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

        /// <summary>
        /// Attempts to read the specified number of bits from the packet.  Does not advance the read position.
        /// </summary>
        /// <param name="count">The number of bits to read.</param>
        /// <param name="bitsRead">Outputs the actual number of bits read.</param>
        /// <returns>The value of the bits read.</returns>
        // (uint) bounds check: one unsigned compare instead of two, and it hands the JIT the range
        // fact count in [0, 64] which lets it drop redundant checks downstream. The common case (the
        // bucket already holds enough bits) is kept tiny and inlinable; the refill loop is a separate
        // NoInlining method so it can't bloat the hot path out of an inline budget.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong TryPeekBits(int count, out int bitsRead)
        {
            if ((uint)count > 64) throw new ArgumentOutOfRangeException(nameof(count));

            if (_bitCount < count)
            {
                return RefillAndPeek(count, out bitsRead);
            }

            bitsRead = count;
            ulong value = _bitBucket;
            if (count < 64)
            {
                value &= (1UL << count) - 1;
            }
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        ulong RefillAndPeek(int count, out int bitsRead)
        {
            // Pull bytes in bulk via FetchBytes and fill the bucket as full as the single-overflow-byte
            // scheme allows. want = (71 - _bitCount) / 8 keeps every insert shift <= 63: a byte inserted
            // at shift 64 would wrap (C# masks ulong shift counts mod 64) and corrupt the low bits, so
            // at most one straddling byte ever spills into _overflowBits, exactly as the byte-at-a-time
            // path guaranteed by stopping the instant it had enough.
            Span<byte> buffer = stackalloc byte[8];
            while (_bitCount < count)
            {
                int want = (71 - _bitCount) / 8;
                if (want > 8) want = 8;
                if (want < 1) want = 1;

                int got = FetchBytes(buffer.Slice(0, want));
                if (got == 0)
                {
                    bitsRead = _bitCount;
                    return _bitBucket;
                }

                for (int i = 0; i < got; i++)
                {
                    int val = buffer[i];
                    _bitBucket = (ulong)val << _bitCount | _bitBucket;
                    _bitCount += 8;

                    if (_bitCount > 64)
                    {
                        _overflowBits = (byte)(val >> (72 - _bitCount));
                    }
                }
            }

            bitsRead = count;
            ulong value = _bitBucket;
            if (count < 64)
            {
                value &= (1UL << count) - 1;
            }
            return value;
        }

        /// <summary>
        /// Advances the read position by the the specified number of bits.
        /// </summary>
        /// <param name="count">The number of bits to skip reading.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SkipBits(int count)
        {
            // count > 64 must throw, not silently proceed: with overflow bits present, the internal
            // (64 - count) shift goes negative and C# masks shift amounts mod 64 rather than saturating,
            // corrupting _bitBucket. Callers needing to skip more must chunk into <=64-bit pieces.
            if ((uint)count > 64) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;

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
                SkipBitsSlow(count);
            }
        }

        // Cold path: skipping past what the bucket holds, refilling straight from the source. Kept out
        // of the inlinable SkipBits so the hot in-bucket case stays tiny.
        [MethodImpl(MethodImplOptions.NoInlining)]
        void SkipBitsSlow(int count)
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
