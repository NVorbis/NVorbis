using System;

namespace NVorbis.Contracts
{
    /// <summary>
    /// Encapsulates a method that calculates the number of granules decodable from the specified packet.
    /// </summary>
    /// <param name="packet">The <see cref="IPacket"/> to calculate.</param>
    /// <returns>The calculated number of granules.</returns>
    public delegate int GetPacketGranuleCount(IPacket packet);

    /// <summary>
    /// Facts about a positive granule discrepancy the container found while reconstructing a
    /// page boundary: its calculated end-granule exceeds the encoder's stored granule.  The
    /// container reports these facts to the codec without interpreting them.
    /// </summary>
    public readonly struct GranuleDiscrepancy
    {
        /// <summary>The granule position being sought.</summary>
        public long TargetGranule { get; }
        /// <summary>The container's calculated boundary granule.</summary>
        public long CalculatedEndGranule { get; }
        /// <summary>The number of granules in the prior page's last packet.</summary>
        public long PreviousPageLastPacketLength { get; }
        /// <summary>Calculated minus stored; always greater than zero here.</summary>
        public long Diff { get; }

        /// <summary>Creates a new <see cref="GranuleDiscrepancy"/>.</summary>
        public GranuleDiscrepancy(long targetGranule, long calculatedEndGranule, long previousPageLastPacketLength, long diff)
        {
            TargetGranule = targetGranule;
            CalculatedEndGranule = calculatedEndGranule;
            PreviousPageLastPacketLength = previousPageLastPacketLength;
            Diff = diff;
        }
    }

    /// <summary>
    /// The codec's resolution of a <see cref="GranuleDiscrepancy"/>: the packet the container
    /// should seek to and the granule at the start of that packet.
    /// </summary>
    public readonly struct GranuleDiscrepancyResolution
    {
        /// <summary>Page-relative packet index to seek to; negative steps into the prior page.</summary>
        public int PacketOffset { get; }
        /// <summary>The granule at the start of that packet.</summary>
        public long GranulePos { get; }

        /// <summary>Creates a new <see cref="GranuleDiscrepancyResolution"/>.</summary>
        public GranuleDiscrepancyResolution(int packetOffset, long granulePos)
        {
            PacketOffset = packetOffset;
            GranulePos = granulePos;
        }
    }

    /// <summary>
    /// Describes an interface for a packet stream reader.
    /// </summary>
    public interface IPacketProvider
    {
        /// <summary>
        /// Gets whether the provider supports seeking.
        /// </summary>
        bool CanSeek { get; }

        /// <summary>
        /// Gets the serial number of this provider's data stream.
        /// </summary>
        int StreamSerial { get; }

        /// <summary>
        /// Gets the next packet in the stream and advances to the next packet position.
        /// </summary>
        /// <returns>The <see cref="IPacket"/> instance for the next packet if available, otherwise <see langword="null"/>.</returns>
        IPacket GetNextPacket();

        /// <summary>
        /// Gets the next packet in the stream without advancing to the next packet position.
        /// </summary>
        /// <returns>The <see cref="IPacket"/> instance for the next packet if available, otherwise <see langword="null"/>.</returns>
        IPacket PeekNextPacket();

        /// <summary>
        /// Seeks the stream to the packet that is prior to the requested granule position by the specified preroll number of packets.
        /// </summary>
        /// <param name="granulePos">The granule position to seek to.</param>
        /// <param name="preRoll">The number of packets to seek backward prior to the granule position.</param>
        /// <param name="getPacketGranuleCount">A <see cref="GetPacketGranuleCount"/> delegate that returns the number of granules in the specified packet.</param>
        /// <returns>The granule position at the start of the packet containing the requested position.</returns>
        long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount);

        /// <summary>
        /// Gets the total number of granule available in the stream.
        /// </summary>
        long GetGranuleCount();

        /// <summary>
        /// Optional hook, invoked only when a <see cref="GranuleDiscrepancy"/>'s Diff is positive.
        /// A non-null result means the codec accounts for the discrepancy and the container seeks to
        /// the returned packet/granule; a null result (or unset hook) means the container treats it as
        /// a genuine timeline gap.  Mirrors the set-once new-stream callback idiom.
        /// </summary>
        Func<GranuleDiscrepancy, GranuleDiscrepancyResolution?> GranuleDiscrepancyHandler { get; set; }
    }
}
