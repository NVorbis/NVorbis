using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    // Direct coverage for PacketProvider.GetIsVorbisBugDiff: the bit-math that recognizes
    // the libvorbis long->short granule miscount. The recognized difference is always
    // (1<<a) - (1<<b) for a > b, i.e. a single contiguous run of 1 bits; anything with a
    // gap in the 1 bits is not the bug. (issue #28 exercises this end-to-end; this pins the
    // function in isolation.)
    public class PacketProviderBugDiffTests
    {
        private class StubStreamPageReader : IStreamPageReader
        {
            public IPacketProvider PacketProvider => null;
            public void AddPage() { }
            public Memory<byte>[] GetPagePackets(int pageIndex) => null;
            public int FindPage(long granulePos) => -1;
            public bool GetPage(int pageIndex, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
            {
                granulePos = 0; isResync = false; isContinuation = false; isContinued = false; packetCount = 0; pageOverhead = 0;
                return false;
            }
            public void SetEndOfStream() { }
            public int PageCount => 0;
            public bool HasAllPages => true;
            public long? MaxGranulePosition => 0;
            public int FirstDataPageIndex => 0;
        }

        private static readonly Type _ppType = typeof(VorbisReader).Assembly.GetType("NVorbis.Ogg.PacketProvider")!;
        private static readonly MethodInfo _bugDiff =
            _ppType.GetMethod("GetIsVorbisBugDiff", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static bool IsBugDiff(long diff)
        {
            var pp = Activator.CreateInstance(
                _ppType,
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new object[] { new StubStreamPageReader(), 0 },
                null)!;
            return (bool)_bugDiff.Invoke(pp, new object[] { diff })!;
        }

        [Theory]
        [InlineData(448)]  // 2048/256 → 512-64, 0b111000000
        [InlineData(192)]  // 1024/256 → 256-64, 0b11000000
        [InlineData(480)]  // 2048/128 → 512-32, 0b111100000
        [InlineData(96)]   // 128-32, 0b1100000
        [InlineData(6)]    // 8-2,  0b110
        [InlineData(7)]    // 8-1,  0b111
        public void ContiguousOneRun_IsBugDiff(long diff)
        {
            Assert.True(IsBugDiff(diff));
        }

        [Fact]
        public void NegativeDiff_UsesAbsoluteValue()
        {
            Assert.True(IsBugDiff(-448));
        }

        [Theory]
        [InlineData(5)]    // 0b101  — gap
        [InlineData(10)]   // 0b1010 — gap
        [InlineData(20)]   // 0b10100 — gap
        [InlineData(100)]  // 0b1100100 — gap
        [InlineData(320)]  // 0b101000000 — gap
        public void GappedBits_AreNotBugDiff(long diff)
        {
            Assert.False(IsBugDiff(diff));
        }
    }
}
