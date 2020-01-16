using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis.Contracts.Ogg
{
    interface IStreamPageReader
    {
        IPacketProvider PacketProvider { get; }

        void AddPage();

        ValueTuple<long, int>[] GetPagePackets(int pageIndex);

        int FindPage(long granulePos);

        bool GetPage(int pageIndex, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead);

        int FillBuffer(long offset, byte[] buffer, int index, int count);

        void SetEndOfStream();

        int PageCount { get; }

        bool HasAllPages { get; }

        long? MaxGranulePosition { get; }
    }
}
