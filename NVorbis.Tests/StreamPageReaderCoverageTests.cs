using System;
using System.Collections.Generic;
using System.IO;
using NVorbis.Contracts.Ogg;
using NVorbis.Ogg;
using Xunit;

namespace NVorbis.Tests
{
    // Additional StreamPageReader coverage beyond the basic cache test in
    // StreamPageReaderTests.cs -- targets AddPage validation, resync (negative
    // offset) handling, FindPage's forward/bisection search branches, and
    // GetPage's page-index/resync branches.
    public class StreamPageReaderCoverageTests
    {
        private sealed class Page
        {
            public long Offset;
            public int SequenceNumber;
            public long GranulePosition;
            public short PacketCount = 1;
            public bool? IsResync = false;
            public bool IsContinued;
            public int PageOverhead = 27;
            public Memory<byte>[] Packets = { new(new byte[] { 0x01 }) };
        }

        // Drives IPageData either directly (set Current, matching how the real page
        // reader would have already positioned itself before StreamPageReader.AddPage
        // is called) or via a queued ReadNextPage sequence that calls back into the
        // owning StreamPageReader.AddPage, mirroring the production wiring described
        // in StreamPageReader's constructor comment.
        private sealed class FakePageData : IPageData
        {
            private readonly Dictionary<long, Page> _byOffset = new();
            public readonly Queue<Page> Pending = new();
            public readonly HashSet<long> FailOffsets = new();
            public readonly List<long> ReadPageAtLog = new();
            public Action AddPageCallback;

            public Page Current;

            public long PageOffset => Current.Offset;
            public int StreamSerial => 1;
            public int SequenceNumber => Current.SequenceNumber;
            public PageFlags PageFlags => PageFlags.None;
            public long GranulePosition => Current.GranulePosition;
            public short PacketCount => Current.PacketCount;
            public bool? IsResync => Current.IsResync;
            public bool IsContinued => Current.IsContinued;
            public int PageOverhead => Current.PageOverhead;
            public long ContainerBits => 0;
            public long WasteBits => 0;

            public void SetCurrent(Page page)
            {
                Current = page;
                _byOffset[page.Offset] = page;
            }

            public void Lock() { }
            public bool Release() => true;

            public bool ReadNextPage()
            {
                if (Pending.Count == 0)
                {
                    return false;
                }
                SetCurrent(Pending.Dequeue());
                AddPageCallback?.Invoke();
                return true;
            }

            public bool ReadPageAt(long offset)
            {
                ReadPageAtLog.Add(offset);
                if (FailOffsets.Contains(offset) || !_byOffset.TryGetValue(offset, out var page))
                {
                    return false;
                }
                Current = page;
                return true;
            }

            public Memory<byte>[] GetPackets() => Current.Packets;
            public void Dispose() { }
        }

        private static (StreamPageReader spr, FakePageData fake) Make()
        {
            var fake = new FakePageData();
            StreamPageReader spr = null;
            spr = new StreamPageReader(fake, streamSerial: 1);
            fake.AddPageCallback = () => spr.AddPage();
            return (spr, fake);
        }

        // ── AddPage validation ────────────────────────────────────────────────

        [Fact]
        public void AddPage_GranulePositionRegressed_ThrowsInvalidDataException()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 1000 });
            spr.AddPage();

            fake.SetCurrent(new Page { Offset = 20, SequenceNumber = 2, GranulePosition = 500 });
            var ex = Assert.Throws<InvalidDataException>(() => spr.AddPage());
            Assert.Contains("regressed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AddPage_GranuleMinusOneWithoutSingleContinuedPacket_ThrowsInvalidDataException()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 1000 });
            spr.AddPage();

            fake.SetCurrent(new Page
            {
                Offset = 20,
                SequenceNumber = 2,
                GranulePosition = -1,
                IsContinued = false,
                PacketCount = 2,
            });
            var ex = Assert.Throws<InvalidDataException>(() => spr.AddPage());
            Assert.Contains("continued packet", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ── Resync (negative stored offset) handling ────────────────────────────

        [Fact]
        public void GetPagePackets_ResyncPage_NegatesOffsetBeforeReadingFromDisk()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 100 });
            spr.AddPage();

            // sequence jump (2 -> 9, not lastSeq+1) marks this page a resync: stored as -offset.
            var resyncPage = new Page { Offset = 20, SequenceNumber = 9, GranulePosition = 200, Packets = new Memory<byte>[] { new(new byte[] { 0xAB }) } };
            fake.SetCurrent(resyncPage);
            spr.AddPage();

            var packets = spr.GetPagePackets(1);

            Assert.Equal(20, Assert.Single(fake.ReadPageAtLog)); // negated back to positive before the disk read
            Assert.Equal(0xAB, packets[0].Span[0]);
        }

        [Fact]
        public void FindPage_ResyncPage_NegatesOffsetInGetPageRaw()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 100 });
            spr.AddPage();

            fake.SetCurrent(new Page { Offset = 20, SequenceNumber = 9, GranulePosition = 500 }); // resync
            spr.AddPage();

            var result = spr.FindPage(500);

            Assert.Equal(2, result);
            Assert.Contains(20, fake.ReadPageAtLog);
        }

        // ── FindPage: exact granule match on last known page ────────────────────

        [Fact]
        public void FindPage_SameGranuleAsLastPage_ReturnsLastIndexPlusOne()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 1000 });
            spr.AddPage();

            var result = spr.FindPage(1000);

            Assert.Equal(1, result);
        }

        // ── FindPage: no pages available at all ─────────────────────────────────

        [Fact]
        public void FindPage_NoPagesAvailable_ThrowsArgumentOutOfRangeException()
        {
            var (spr, _) = Make();

            Assert.Throws<ArgumentOutOfRangeException>(() => spr.FindPage(0));
        }

        // ── FindPage: forward search runs off the end of the stream ─────────────

        [Fact]
        public void FindPage_ForwardSearchExhaustsStream_ThrowsArgumentOutOfRangeException()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 1000 });
            spr.AddPage();
            // no more pages queued -- forward search will hit end-of-stream

            Assert.Throws<ArgumentOutOfRangeException>(() => spr.FindPage(5000));
        }

        // ── FindPage: bisection direct hit and read failure ─────────────────────

        [Fact]
        public void FindPage_BisectionDirectHit_ReturnsIndexPlusOne()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 100 });
            spr.AddPage();
            fake.SetCurrent(new Page { Offset = 20, SequenceNumber = 2, GranulePosition = 200 });
            spr.AddPage();
            fake.SetCurrent(new Page { Offset = 30, SequenceNumber = 3, GranulePosition = 300 });
            spr.AddPage();

            var result = spr.FindPage(200);

            Assert.Equal(2, result);
        }

        [Fact]
        public void FindPage_BisectionReadFailure_ThrowsArgumentOutOfRangeException()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 100 });
            spr.AddPage();
            fake.SetCurrent(new Page { Offset = 20, SequenceNumber = 2, GranulePosition = 200 });
            spr.AddPage();
            fake.SetCurrent(new Page { Offset = 30, SequenceNumber = 3, GranulePosition = 300 });
            spr.AddPage();

            fake.FailOffsets.Add(20); // the page bisection lands on first

            Assert.Throws<ArgumentOutOfRangeException>(() => spr.FindPage(200));
        }

        // ── GetPage: resync branch and known-index read failure ─────────────────

        [Fact]
        public void GetPage_ResyncPageIndex_SetsIsResyncAndNegatesOffset()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 100 });
            spr.AddPage();
            fake.SetCurrent(new Page { Offset = 20, SequenceNumber = 9, GranulePosition = 200 }); // resync
            spr.AddPage();

            var ok = spr.GetPage(1, out var granulePos, out var isResync, out _, out _, out _, out _);

            Assert.True(ok);
            Assert.True(isResync);
            Assert.Equal(200, granulePos);
            Assert.Contains(20, fake.ReadPageAtLog);
        }

        [Fact]
        public void GetPage_KnownIndexReadFails_ReturnsFalse()
        {
            var (spr, fake) = Make();

            fake.SetCurrent(new Page { Offset = 10, SequenceNumber = 1, GranulePosition = 100 });
            spr.AddPage();
            fake.FailOffsets.Add(10);

            var ok = spr.GetPage(0, out var granulePos, out var isResync, out var isContinuation, out var isContinued, out var packetCount, out var pageOverhead);

            Assert.False(ok);
            Assert.Equal(0, granulePos);
            Assert.False(isResync);
            Assert.False(isContinuation);
            Assert.False(isContinued);
            Assert.Equal(0, packetCount);
            Assert.Equal(0, pageOverhead);
        }
    }
}
