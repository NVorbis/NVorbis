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
        }

        private static ContainerReader MakeReader()
        {
            var original = ContainerReader.CreatePageReader;
            ContainerReader.CreatePageReader = (s, cod, cb) => new FakePageReader();
            try
            {
                return new ContainerReader(new MemoryStream(), closeOnDispose: false);
            }
            finally
            {
                ContainerReader.CreatePageReader = original;
            }
        }

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

        // Known defect (documented in DESIGN_DECISIONS.md): when a WeakReference has been
        // collected, GetStreams's cleanup branch calls list.RemoveAt(i) against the *output*
        // list (which never contained the dead entry) using the *source* list's index, so it
        // throws instead of silently dropping the stale entry. This test pins the current
        // (broken) behavior so a future fix is a deliberate, visible change.
        [Fact]
        public void GetStreams_CollectedWeakReference_ThrowsDueToIndexMismatchBug()
        {
            var reader = MakeReader();
            var dead = MakeDeadWeakReference();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(dead.TryGetTarget(out _), "test setup invariant: reference must be collected");

            PacketProviders(reader).Add(dead);

            Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetStreams());
        }
    }
}
