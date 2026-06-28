using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class TruncatedStreamTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        [Fact]
        public void EmptyStream_ThrowsArgumentException()
        {
            using var ms = new MemoryStream(Array.Empty<byte>());
            Assert.Throws<ArgumentException>(() => new VorbisReader(ms, closeOnDispose: false));
        }

        [Fact]
        public void RandomBytes_ThrowsArgumentException()
        {
            var noise = new byte[512];
            new Random(42).NextBytes(noise);
            using var ms = new MemoryStream(noise);
            Assert.Throws<ArgumentException>(() => new VorbisReader(ms, closeOnDispose: false));
        }

        [Fact]
        public void TruncatedStream_AfterHeaders_ReturnsSomeSamples()
        {
            // Cut the file to 25% of its length; headers are still intact so
            // construction succeeds, but decoding stops before the real EOS.
            var bytes = File.ReadAllBytes(TestFile("3test.ogg"));
            var truncated = new byte[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, truncated, 0, truncated.Length);

            using var ms = new MemoryStream(truncated);
            using var reader = new VorbisReader(ms, closeOnDispose: false);

            var buf = new float[4096];
            int total = 0, count;
            while ((count = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                total += count;

            Assert.True(total > 0, "Expected audio samples before the truncation point");
        }

        [Fact]
        public void TruncatedStream_AfterHeaders_FewerSamplesThanFullFile()
        {
            var bytes = File.ReadAllBytes(TestFile("3test.ogg"));

            long fullSamples = CountSamples(bytes, bytes.Length);
            long halfSamples = CountSamples(bytes, bytes.Length / 2);

            Assert.True(halfSamples < fullSamples,
                "Truncated stream should yield fewer samples than the complete file");
        }

        [Fact]
        public void TruncatedStream_ReadAfterEos_ReturnsZero()
        {
            var bytes = File.ReadAllBytes(TestFile("3test.ogg"));
            var truncated = new byte[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, truncated, 0, truncated.Length);

            using var ms = new MemoryStream(truncated);
            using var reader = new VorbisReader(ms, closeOnDispose: false);

            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }

            // After exhausting a truncated stream, further reads must return 0.
            Assert.Equal(0, reader.ReadSamples(buf, 0, buf.Length));
        }

        [Fact]
        public void FullFile_TotalSamplesMatchesActualRead()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            long declared = reader.TotalSamples;

            var buf = new float[4096];
            long actual = 0;
            int count;
            while ((count = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                actual += count / reader.Channels;

            Assert.Equal(declared, actual);
        }

        private static long CountSamples(byte[] source, int length)
        {
            var truncated = new byte[length];
            Buffer.BlockCopy(source, 0, truncated, 0, length);
            using var ms = new MemoryStream(truncated);
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            var buf = new float[4096];
            long total = 0;
            int count;
            while ((count = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                total += count / reader.Channels;
            return total;
        }
    }
}
