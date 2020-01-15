using System;

namespace NVorbis.Contracts.Ogg
{
    interface IPageData : IPageReader
    {
        long PageOffset { get; }
        int StreamSerial { get; }
        int SequenceNumber { get; }
        PageFlags PageFlags { get; }
        long GranulePosition { get; }
        short PacketCount { get; }
        bool? IsResync { get; }
        bool IsContinued { get; }
        int PageOverhead { get; }

        ValueTuple<long, int>[] GetPackets();

        int Read(long offset, byte[] buffer, int index, int count);
    }
}
