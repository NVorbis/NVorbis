namespace NVorbis.Contracts
{
    /// <summary>
    /// Describes a packet of data from a data stream.
    /// </summary>
    public interface IPacket
    {
        /// <summary>
        /// Gets whether this packet occurs immediately following a loss of sync in the stream.
        /// </summary>
        bool IsResync { get; }

        /// <summary>
        /// Gets whether this packet did not read its full data.
        /// </summary>
        bool IsShort { get; }

        /// <summary>
        /// Gets the granule position of the packet, if known.
        /// </summary>
        long GranulePosition { get; }

        /// <summary>
        /// Gets the granule position of the framing page the packet is in.
        /// </summary>
        long PageGranulePosition { get; }

        /// <summary>
        /// Gets whether the packet is the start of a new header set.
        /// </summary>
        bool IsParameterChange { get; }

        /// <summary>
        /// Gets whether the packet is the last packet of the stream.
        /// </summary>
        bool IsEndOfStream { get; }

        /// <summary>
        /// Gets the number of bits read from the packet.
        /// </summary>
        int BitsRead { get; }

        /// <summary>
        /// Gets the number of bits left in the packet.
        /// </summary>
        int BitsRemaining { get; }

        /// <summary>
        /// Gets the number of container overhead bits associated with this packet.
        /// </summary>
        int ContainerOverheadBits { get; }

        /// <summary>
        /// Attempts to read the specified number of bits from the packet.  Does not advance the read position.
        /// </summary>
        /// <param name="count">The number of bits to read.</param>
        /// <param name="bitsRead">Outputs the actual number of bits read.</param>
        /// <returns>The value of the bits read.</returns>
        ulong TryPeekBits(int count, out int bitsRead);

        /// <summary>
        /// Advances the read position by the the specified number of bits.
        /// </summary>
        /// <param name="count">The number of bits to skip reading.</param>
        void SkipBits(int count);

        /// <summary>
        /// Reads the specified number of bits from the packet and advances the read position.
        /// </summary>
        /// <param name="count">The number of bits to read.</param>
        /// <returns>The value read.  If not enough bits remained, this will be a truncated value.</returns>
        ulong ReadBits(int count);

        /// <summary>
        /// Reads one bit from the packet and advances the read position.
        /// </summary>
        /// <returns><see langword="true"/> if the bit was a one, otehrwise <see langword="false"/>.</returns>
        bool ReadBit();

        /// <summary>
        /// Reads into the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to read into.</param>
        /// <param name="index">The index into the buffer to use.</param>
        /// <param name="count">The number of bytes to read into the buffer.</param>
        /// <returns>The number of bytes actually read into the buffer.</returns>
        int Read(byte[] buffer, int index, int count);

        /// <summary>
        /// Frees the buffers and caching for the packet instance.
        /// </summary>
        void Done();

        /// <summary>
        /// Resets the read buffers to the beginning of the packet.
        /// </summary>
        void Reset();
    }
}
