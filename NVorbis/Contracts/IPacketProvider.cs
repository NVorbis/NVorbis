using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace NVorbis.Contracts
{
    public interface IPacketProvider : IDisposable
    {
        int StreamSerial { get; }

        Action<IPacket> ParameterChangeCallback { get; set; }

        IPacket GetNextPacket();

        IPacket PeekNextPacket();

        IPacket GetPacket(int packetIndex);

        long GetGranuleCount();

        IPacket FindPacket(long granulePos, Func<IPacket, IPacket, int> packetGranuleCountCallback);

        void SeekToPacket(IPacket packet, int preRoll);
    }
}
