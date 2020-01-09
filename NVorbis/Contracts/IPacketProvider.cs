using System;

namespace NVorbis.Contracts
{
    public delegate int GetPacketGranuleCount(IPacket packet, bool isFirst);

    public interface IPacketProvider : IDisposable
    {
        int StreamSerial { get; }

        Action<IPacket> ParameterChangeCallback { get; set; }

        IPacket GetNextPacket();

        IPacket PeekNextPacket();

        IPacket GetPacket(int packetIndex);

        long GetGranuleCount();

        IPacket FindPacket(long granulePos, GetPacketGranuleCount getPacketGranuleCount);

        void SeekToPacket(IPacket packet, int preRoll);
    }
}
