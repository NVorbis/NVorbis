namespace NVorbis.Contracts.Ogg
{
    interface IPacketReader : IPacketProvider
    {
        int PagesRead { get; }

        void AddPage(PageFlags flags, bool isResync, int seqNbr, long pageOffset);
        void InvalidatePacketCache(IPacket packet);
        int GetTotalPageCount();
        void SetEndOfStream();
    }
}
