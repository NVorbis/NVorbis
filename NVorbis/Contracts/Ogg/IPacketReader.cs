namespace NVorbis.Contracts.Ogg
{
    interface IPacketReader
    {
        void InvalidatePacketCache(IPacket packet);
        int FillBuffer(long offet, byte[] buffer, int index, int count);
    }
}
