using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class NonSeekableStreamTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        private sealed class NonSeekableStream : Stream
        {
            private readonly Stream _inner;
            public NonSeekableStream(Stream inner) => _inner = inner;
            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) =>
                _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private static VorbisReader OpenNonSeekable(string file)
        {
            var bytes = File.ReadAllBytes(TestFile(file));
            var inner = new MemoryStream(bytes);
            var ns = new NonSeekableStream(inner);
            return new VorbisReader(ns, closeOnDispose: true);
        }

        [Fact]
        public void ReadSamples_NonSeekableStream_ReturnsSamples()
        {
            using var reader = OpenNonSeekable("3test.ogg");
            var buf = new float[reader.SampleRate * reader.Channels];
            Assert.True(reader.ReadSamples(buf, 0, buf.Length) > 0);
        }

        [Fact]
        public void ReadSamples_NonSeekableStream_CanDrainToEos()
        {
            using var reader = OpenNonSeekable("1test.ogg");
            var buf = new float[4096];
            long total = 0;
            int count;
            while ((count = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                total += count;
            Assert.True(total > 0);
            Assert.True(reader.IsEndOfStream);
        }

        [Fact]
        public void SeekTo_NonSeekableStream_ThrowsInvalidOperationException()
        {
            using var reader = OpenNonSeekable("3test.ogg");
            Assert.Throws<InvalidOperationException>(() => reader.SeekTo(0L, SeekOrigin.Begin));
        }

        [Fact]
        public void TotalFrames_NonSeekableStream_ThrowsNotSupportedException()
        {
            // ForwardOnlyPacketProvider.GetGranuleCount() is not supported.
            using var reader = OpenNonSeekable("3test.ogg");
            Assert.Throws<NotSupportedException>(() => _ = reader.TotalFrames);
        }

        [Fact]
        public void Channels_NonSeekableStream_ReturnsPositiveValue()
        {
            using var reader = OpenNonSeekable("3test.ogg");
            Assert.True(reader.Channels > 0);
        }

        [Fact]
        public void SampleRate_NonSeekableStream_ReturnsPositiveValue()
        {
            using var reader = OpenNonSeekable("3test.ogg");
            Assert.True(reader.SampleRate > 0);
        }
    }
}
