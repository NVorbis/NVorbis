namespace NVorbis.Contracts.Ogg
{
    interface IForwardOnlyPacketProvider : IPacketProvider
    {
        void AddPage(byte[] buf, bool isResync);
        void SetEndOfStream();
    }
}
