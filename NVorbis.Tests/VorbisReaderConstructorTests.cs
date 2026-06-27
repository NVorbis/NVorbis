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
        }

        private static void WithFactory(
            Func<Stream, bool, Contracts.IContainerReader> factory,
            Action body)
        {
            var original = VorbisReader.CreateContainerReader;
            VorbisReader.CreateContainerReader = factory;
            try { body(); }
            finally { VorbisReader.CreateContainerReader = original; }
        }

        // When TryInit() throws, containerReader must be disposed — it was leaked before the fix.
        [Fact]
        public void Constructor_TryInitThrows_DisposesContainerReader()
        {
            StubContainerReader stub = null;
            WithFactory(
                (_, __) => stub = new StubContainerReader(() => throw new InvalidOperationException("TryInit failed")),
                () =>
                {
                    Assert.Throws<InvalidOperationException>(() =>
                        new VorbisReader(Stream.Null, closeOnDispose: false));
                    Assert.True(stub.Disposed, "containerReader must be disposed when TryInit throws");
                });
        }

        // When TryInit() returns false, containerReader must also be disposed (pre-existing behaviour preserved).
        [Fact]
        public void Constructor_TryInitReturnsFalse_DisposesContainerReader()
        {
            StubContainerReader stub = null;
            WithFactory(
                (_, __) => stub = new StubContainerReader(() => false),
                () =>
                {
                    Assert.Throws<ArgumentException>(() =>
                        new VorbisReader(Stream.Null, closeOnDispose: false));
                    Assert.True(stub.Disposed, "containerReader must be disposed when TryInit returns false");
                });
        }

        // When TryInit() throws and closeOnDispose is true, the stream must be disposed.
        [Fact]
        public void Constructor_TryInitThrows_ClosesStreamWhenCloseOnDispose()
        {
            var ms = new MemoryStream(new byte[1]);
            WithFactory(
                (_, __) => new StubContainerReader(() => throw new InvalidOperationException()),
                () =>
                {
                    Assert.Throws<InvalidOperationException>(() =>
                        new VorbisReader(ms, closeOnDispose: true));
                    Assert.Throws<ObjectDisposedException>(() => ms.ReadByte());
                });
        }

        // When TryInit() throws and closeOnDispose is false, the stream must NOT be disposed.
        [Fact]
        public void Constructor_TryInitThrows_LeavesStreamOpenWhenNotCloseOnDispose()
        {
            var ms = new MemoryStream(new byte[1]);
            WithFactory(
                (_, __) => new StubContainerReader(() => throw new InvalidOperationException()),
                () =>
                {
                    Assert.Throws<InvalidOperationException>(() =>
                        new VorbisReader(ms, closeOnDispose: false));
                    Assert.True(ms.CanRead, "stream must remain open when closeOnDispose is false");
                });
        }
    }
}
