using System;
using System.Collections.Generic;

namespace NVorbis.Contracts.Ogg
{
    interface IPageReader : IDisposable
    {
        void Lock();
        bool Release();

        long ContainerBits { get; }
        long WasteBits { get; }

        bool ReadNextPage();

        bool ReadPageAt(long offset);

        void ReadAllPages();

        // individual page level items
        long PageOffset { get; }
        int StreamSerial { get; }
        int SequenceNumber { get; }
        PageFlags PageFlags { get; }
        long GranulePosition { get; }
        short PacketCount { get; }
        bool? IsResync { get; }
        bool IsContinued { get; }
        int PageOverhead { get; }

        List<Tuple<long, int>> GetPackets();

        int Read(long offset, byte[] buffer, int index, int count);
    }
}
