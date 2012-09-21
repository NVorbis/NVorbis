using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NVorbis
{
    interface IPacketProvider : IDisposable
    {
        void Init();

        bool FindNextStream(int currentStreamSerial);

        DataPacket GetNextPacket(int streamSerial);

        long GetLastGranulePos(int streamSerial);

        int GetTotalPageCount(int streamSerial);

        bool CanSeek { get; }

        long ContainerBits { get; }

        int FindPacket(int streamSerial, long granulePos, Func<DataPacket, DataPacket, DataPacket, int> packetGranuleCountCallback);

        void SeekToPacket(int streamSerial, int packetIndex);

        DataPacket GetPacket(int streamSerial, int packetIndex);
    }
}
