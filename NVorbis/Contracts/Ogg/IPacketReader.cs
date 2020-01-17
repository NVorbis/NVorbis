namespace NVorbis.Contracts.Ogg
{
    interface IPacketReader
    {
        void InvalidatePacketCache(IPacket packet);
    }
}
