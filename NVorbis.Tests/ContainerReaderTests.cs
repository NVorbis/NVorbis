using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using NVorbis.Ogg;
using Xunit;

namespace NVorbis.Tests
{
    public class ContainerReaderTests
    {
        // Minimal no-op IPageReader so ContainerReader's constructor/Dispose/property
        // plumbing has something to call into without touching real Ogg page parsing.
        private sealed class FakePageReader : IPageReader
        {
            public long ContainerBits => 0;
            public long WasteBits => 0;
            public void Lock() { }
            public bool Release() => false;
            public bool ReadNextPage() => false;
            public bool ReadPageAt(long offset) => false;
            public void Dispose() { }
        }

        private sealed class FakePacketProvider : IPacketProvider
        {
            public bool CanSeek => false;
            public int StreamSerial => 1;
            public IPacket GetNextPacket() => null;
            public IPacket PeekNextPacket() => null;
            public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount) => 0;
            public long GetGranuleCount() => 0;
            public Func<GranuleDiscrepancy, GranuleDiscrepancyResolution?> GranuleDiscrepancyHandler { get; set; }
        }

        private static ContainerReader MakeReader() =>
            new ContainerReader(new MemoryStream(), false, (s, cod, cb) => new FakePageReader());

        private static List<WeakReference<IPacketProvider>> PacketProviders(ContainerReader reader) =>
            (List<WeakReference<IPacketProvider>>)typeof(ContainerReader)
                .GetField("_packetProviders", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(reader)!;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<IPacketProvider> MakeDeadWeakReference() =>
            new(new FakePacketProvider());

        [Fact]
        public void GetStreams_LiveWeakReference_ReturnsProvider()
        {
            var reader = MakeReader();
            var provider = new FakePacketProvider();
            PacketProviders(reader).Add(new WeakReference<IPacketProvider>(provider));

            var streams = reader.GetStreams();

            Assert.Single(streams);
            Assert.Same(provider, streams[0]);
        }

        [Fact]
        public void GetStreams_NoProviders_ReturnsEmpty()
        {
            var reader = MakeReader();
            Assert.Empty(reader.GetStreams());
        }

        // Fixed defect: GetStreams's cleanup branch
        // used to call list.RemoveAt(i) against the *output* list (which never contained the
        // dead entry) using the *source* list's index, throwing instead of silently dropping
        // the stale entry. Now prunes the source list (_packetProviders) instead.
        [Fact]
        public void GetStreams_CollectedWeakReference_IsSilentlyDropped()
        {
            var reader = MakeReader();
            var dead = MakeDeadWeakReference();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(dead.TryGetTarget(out _), "test setup invariant: reference must be collected");

            var live = new FakePacketProvider();
            PacketProviders(reader).Add(dead);
            PacketProviders(reader).Add(new WeakReference<IPacketProvider>(live));

            var streams = reader.GetStreams();

            Assert.Single(streams);
            Assert.Same(live, streams[0]);
            Assert.Single(PacketProviders(reader)); // dead entry pruned from source list too
        }
    }
}
