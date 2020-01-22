using System;

namespace NVorbis.Contracts
{
    /// <summary>
    /// Describes an interface for reading statistics about the current stream.
    /// </summary>
    public interface IStreamStats
    {
        /// <summary>
        /// Resets the counters for bit rate and bits.
        /// </summary>
        void ResetStats();

        /// <summary>
        /// Gets the calculated bit rate of audio stream data for the everything decoded so far.
        /// </summary>
        int EffectiveBitRate { get; }

        /// <summary>
        /// Gets the calculated bit rate per second of audio for the last two packets.
        /// </summary>
        int InstantBitRate { get; }

        /// <summary>
        /// Gets the number of framing bits used by the container.
        /// </summary>
        long ContainerBits { get; }

        /// <summary>
        /// Gets the number of bits read that do not contribute to the output audio.  Does not include framing bits from the container.
        /// </summary>
        long OverheadBits { get; }

        /// <summary>
        /// Gets the number of bits read that contribute to the output audio.
        /// </summary>
        long AudioBits { get; }

        /// <summary>
        /// Gets the number of bits skipped.
        /// </summary>
        long WasteBits { get; }

        /// <summary>
        /// Gets the number of packets read.
        /// </summary>
        int PacketCount { get; }

        /// <summary>
        /// Gets the calculated latency per page
        /// </summary>
        [Obsolete("No longer supported.", true)]
        TimeSpan PageLatency { get; }

        /// <summary>
        /// Gets the calculated latency per packet
        /// </summary>
        [Obsolete("No longer supported.", true)]
        TimeSpan PacketLatency { get; }

        /// <summary>
        /// Gets the calculated latency per second of output
        /// </summary>
        [Obsolete("No longer supported.", true)]
        TimeSpan SecondLatency { get; }

        /// <summary>
        /// Gets the number of pages read so far in the current stream
        /// </summary>
        [Obsolete("No longer supported.", true)]
        int PagesRead { get; }

        /// <summary>
        /// Gets the total number of pages in the current stream
        /// </summary>
        [Obsolete("No longer supported.", true)]
        int TotalPages { get; }

        /// <summary>
        /// Gets whether the stream has been clipped since the last reset
        /// </summary>
        [Obsolete("Use IStreamDecoder.HasClipped instead.  VorbisReader.HasClipped will return the same value for the stream it is handling.", true)]
        bool Clipped { get; }
    }
}
