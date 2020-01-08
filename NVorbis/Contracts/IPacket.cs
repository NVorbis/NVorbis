namespace NVorbis.Contracts
{
    public interface IPacket
    {
        bool IsResync { get; }
        bool IsShort { get; }
        long GranulePosition { get; set; }
        long PageGranulePosition { get; }
        bool IsEndOfStream { get; }
        long BitsRead { get; }
        int? GranuleCount { get; set; }

        ulong TryPeekBits(int count, out int bitsRead);
        void SkipBits(int count);
        ulong ReadBits(int count);
        bool ReadBit();
        int Read(byte[] buffer, int index, int count);
        void Done();
    }
}
