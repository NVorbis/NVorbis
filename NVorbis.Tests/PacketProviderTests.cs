using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using NVorbis.Ogg;
using System;
using Xunit;

namespace NVorbis.Tests
{
    public class PacketProviderTests
    {
        /// <summary>
        /// Two-page audio stream where the first audio packet spans both pages:
        ///   page 1 (FDI) : isContinuation=false, isContinued=true,  packetCount=1, granulePos=-1
        ///   page 2       : isContinuation=true,  isContinued=false, packetCount=1, granulePos=256
        ///
        /// NormalizePacketIndex walkback for (page=2, packet=0):
        ///   pktIdx=0 &lt; 1 (isContinuation) → enter loop
        ///   pgIdx(2) &gt; FDI(1)           → decrement to 1, read page 1
        ///   pktIdx += 1 - 1 = 0         → no progress, still in loop
        ///   pgIdx(1) &lt;= FDI(1)          → SNAP to (FDI, 0), return true
        /// </summary>
        private sealed class ContinuedFirstPacketReader : IStreamPageReader
        {
            public int FirstDataPageIndex => 1;
            public int PageCount => 3;
            public bool HasAllPages => true;
            public long? MaxGranulePosition => 256;
            public Contracts.IPacketProvider PacketProvider => null;

            public int FindPage(long granulePos) => 2;

            public bool GetPage(int pageIndex, out long granulePos, out bool isResync,
                out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
            {
                isResync = false;
                pageOverhead = 27;
                if (pageIndex == 1)
                {
                    granulePos = -1;
                    isContinuation = false;
                    isContinued = true;
                    packetCount = 1;
                    return true;
                }
                if (pageIndex == 2)
                {
                    granulePos = 256;
                    isContinuation = true;
                    isContinued = false;
                    packetCount = 1;
                    return true;
                }
                granulePos = 0;
                isContinuation = false;
                isContinued = false;
                packetCount = 0;
                return false;
            }

            public Memory<byte>[] GetPagePackets(int pageIndex) =>
                new[] { new Memory<byte>(new byte[1]) };

            public void AddPage() { }
            public void SetEndOfStream() { }
        }

        [Fact]
        public void SeekTo_WalkbackReachesFDI_SnapsToStreamBeginningInsteadOfThrowing()
        {
            var reader = new ContinuedFirstPacketReader();
            var provider = new PacketProvider(reader, 0);

            // Without the FDI bound, GetPage(0) returns false → NormalizePacketIndex
            // returns false → SeekTo throws ArgumentOutOfRangeException.
            // With the fix, the snap returns (FDI, 0) and SeekTo succeeds with granulePos=0.
            var result = provider.SeekTo(128L, 1, _ => 256);

            Assert.Equal(0L, result);
        }
    }
}
