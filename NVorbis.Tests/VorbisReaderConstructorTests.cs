using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class VorbisReaderConstructorTests
    {
        // Minimal Contracts.IContainerReader stub with configurable TryInit behaviour.
        private sealed class StubContainerReader : Contracts.IContainerReader
        {
            private readonly Func<bool> _tryInit;

            public bool Disposed { get; private set; }
            public Contracts.NewStreamHandler NewStreamCallback { get; set; }
            public bool CanSeek => false;
            public long ContainerBits => 0;
            public long WasteBits => 0;

            public StubContainerReader(Func<bool> tryInit) => _tryInit = tryInit;

            public bool TryInit() => _tryInit();
            public bool FindNextStream() => false;
            public IReadOnlyList<Contracts.IPacketProvider> GetStreams() =>
                Array.Empty<Contracts.IPacketProvider>();
            public void Dispose() => Disposed = true;

            // Test-only hook to simulate the container discovering another logical stream
            // after construction (e.g. a chained/concatenated file), so tests can subscribe
            // to VorbisReader.NewStream first and then observe how it's handled.
            public void RaiseNewStream(Contracts.IPacketProvider pp) => NewStreamCallback(pp);
        }

        private sealed class FakePacketProvider : Contracts.IPacketProvider
        {
            public bool CanSeek => false;
            public int StreamSerial => 1;
            public Contracts.IPacket GetNextPacket() => null;
            public Contracts.IPacket PeekNextPacket() => null;
            public long SeekTo(long granulePos, int preRoll, Contracts.GetPacketGranuleCount getPacketGranuleCount) => 0;
            public long GetGranuleCount() => 0;
            public Func<Contracts.GranuleDiscrepancy, Contracts.GranuleDiscrepancyResolution?> GranuleDiscrepancyHandler { get; set; }
        }

        // Minimal Contracts.IStreamDecoder spy that only tracks whether Dispose() was called.
        private sealed class SpyStreamDecoder : Contracts.IStreamDecoder
        {
            public bool Disposed { get; private set; }
            public int Channels => 0;
            public int SampleRate => 0;
            public int UpperBitrate => 0;
            public int NominalBitrate => 0;
            public int LowerBitrate => 0;
            public Contracts.ITagData Tags => null;
            public TimeSpan TotalTime => TimeSpan.Zero;
            public long TotalFrames => 0;
            [Obsolete]
            public long TotalSamples => 0;
            public TimeSpan TimePosition { get; set; }
            public long FramePosition { get; set; }
            [Obsolete]
            public long SamplePosition { get; set; }
            public bool ClipSamples { get; set; }
            public bool HasClipped => false;
            public bool IsEndOfStream => false;
            public Contracts.IStreamStats Stats => null;
            public void SeekTo(TimeSpan timePosition, SeekOrigin seekOrigin = SeekOrigin.Begin) { }
            public void SeekTo(long framePosition, SeekOrigin seekOrigin = SeekOrigin.Begin) { }
            public int Read(Span<float> buffer) => 0;
            [Obsolete]
            public int Read(Span<float> buffer, int offset, int count) => 0;
            public void Dispose() => Disposed = true;
        }

        // Fixed defect: when a NewStream handler set ea.IgnoreStream, the freshly-constructed
        // decoder (which owns real resources) was dropped without calling Dispose().
        [Fact]
        public void ProcessNewStream_StreamIgnoredViaEvent_DisposesDecoder()
        {
            var firstProvider = new FakePacketProvider();
            var secondProvider = new FakePacketProvider();
            var firstDecoder = new SpyStreamDecoder();
            var secondDecoder = new SpyStreamDecoder();
            var decoderByProvider = new Dictionary<Contracts.IPacketProvider, Contracts.IStreamDecoder>
            {
                [firstProvider] = firstDecoder,
                [secondProvider] = secondDecoder,
            };

            StubContainerReader stub = null;
            stub = new StubContainerReader(() =>
            {
                // Accepted: no NewStream subscriber exists yet at this point in construction.
                stub.RaiseNewStream(firstProvider);
                return true;
            });

            using var reader = new VorbisReader(Stream.Null, false, (_, __) => stub, pp => decoderByProvider[pp]);
            reader.NewStream += (_, ea) => ea.IgnoreStream = true;

            stub.RaiseNewStream(secondProvider);

            Assert.True(secondDecoder.Disposed, "decoder must be disposed when the NewStream handler sets IgnoreStream");
            Assert.False(firstDecoder.Disposed, "the accepted first stream's decoder must remain undisposed");
        }

        // When TryInit() throws, containerReader must be disposed — it was leaked before the fix.
        [Fact]
        public void Constructor_TryInitThrows_DisposesContainerReader()
        {
            var stub = new StubContainerReader(() => throw new InvalidOperationException("TryInit failed"));
            Assert.Throws<InvalidOperationException>(() =>
                new VorbisReader(Stream.Null, false, (_, __) => stub, pp => throw new InvalidOperationException("not expected")));
            Assert.True(stub.Disposed, "containerReader must be disposed when TryInit throws");
        }

        // When TryInit() returns false, containerReader must also be disposed (pre-existing behaviour preserved).
        [Fact]
        public void Constructor_TryInitReturnsFalse_DisposesContainerReader()
        {
            var stub = new StubContainerReader(() => false);
            Assert.Throws<ArgumentException>(() =>
                new VorbisReader(Stream.Null, false, (_, __) => stub, pp => throw new InvalidOperationException("not expected")));
            Assert.True(stub.Disposed, "containerReader must be disposed when TryInit returns false");
        }

        // When TryInit() throws and closeOnDispose is true, the stream must be disposed.
        [Fact]
        public void Constructor_TryInitThrows_ClosesStreamWhenCloseOnDispose()
        {
            var ms = new MemoryStream(new byte[1]);
            Assert.Throws<InvalidOperationException>(() =>
                new VorbisReader(ms, true,
                    (_, __) => new StubContainerReader(() => throw new InvalidOperationException()),
                    pp => throw new InvalidOperationException("not expected")));
            Assert.Throws<ObjectDisposedException>(() => ms.ReadByte());
        }

        // When TryInit() throws and closeOnDispose is false, the stream must NOT be disposed.
        [Fact]
        public void Constructor_TryInitThrows_LeavesStreamOpenWhenNotCloseOnDispose()
        {
            var ms = new MemoryStream(new byte[1]);
            Assert.Throws<InvalidOperationException>(() =>
                new VorbisReader(ms, false,
                    (_, __) => new StubContainerReader(() => throw new InvalidOperationException()),
                    pp => throw new InvalidOperationException("not expected")));
            Assert.True(ms.CanRead, "stream must remain open when closeOnDispose is false");
        }
    }
}
