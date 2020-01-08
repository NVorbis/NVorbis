using NVorbis.Ogg;

namespace NVorbis.Contracts.Ogg
{
    interface IPacketReader
    {
        void AddPage(PageFlags flags, bool isResync, int seqNbr, long pageOffset, short packetCount, long granulePos);
        void InvalidatePacketCache(IPacket packet);
        void SetEndOfStream();
    }
}
