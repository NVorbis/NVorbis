using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using NVorbis.Ogg;
using System;
using Xunit;

namespace NVorbis.Tests
{
    public class StreamPageReaderTests
    {
        private sealed class MockPageData : IPageData
        {
            public int ReadPageAtCallCount;

            // Fixed page state: a single non-continued audio page at offset 100
            public long PageOffset => 100;
            public int StreamSerial => 0x1234;
            public int SequenceNumber => 1;
            public PageFlags PageFlags => PageFlags.None;
            public long GranulePosition => 1024;
            public short PacketCount => 1;
            public bool? IsResync => false;
            public bool IsContinued => false;
            public int PageOverhead => 27;
            public long ContainerBits => 0;
            public long WasteBits => 0;

            public void Lock() { }
            public bool Release() => true;
            public bool ReadNextPage() => false;
            public bool ReadPageAt(long offset) { ReadPageAtCallCount++; return true; }
            public Memory<byte>[] GetPackets() => new[] { new Memory<byte>(new byte[] { 0x01, 0x02 }) };
            public void Dispose() { }
        }

        [Fact]
        public void GetPageThenGetPackets_ReadsPageFromDiskOnce()
        {
            var mock = new MockPageData();
            var spr = new StreamPageReader(mock, mock.StreamSerial);

            // register the page (uses current mock state — no ReadPageAt)
            spr.AddPage();

            // first call: must read from disk
            spr.GetPage(0, out _, out _, out _, out _, out _, out _);
            var afterGetPage = mock.ReadPageAtCallCount;

            // second call: packet data should already be cached
            spr.GetPagePackets(0);
            var afterGetPackets = mock.ReadPageAtCallCount;

            Assert.Equal(1, afterGetPage);
            Assert.Equal(1, afterGetPackets); // no second read
        }

        [Fact]
        public void GetPagePackets_AfterGetPage_ReturnsCorrectData()
        {
            var mock = new MockPageData();
            var spr = new StreamPageReader(mock, mock.StreamSerial);
            spr.AddPage();

            spr.GetPage(0, out _, out _, out _, out _, out _, out _);
            var packets = spr.GetPagePackets(0);

            Assert.Single(packets);
            Assert.Equal(new byte[] { 0x01, 0x02 }, packets[0].ToArray());
        }
    }
}
