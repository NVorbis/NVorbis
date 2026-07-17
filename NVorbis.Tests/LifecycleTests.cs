using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class LifecycleTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        // ── After Dispose ────────────────────────────────────────────────────

        [Fact]
        public void ReadSamples_AfterDispose_ThrowsObjectDisposedException()
        {
            var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.Dispose();
            var buf = new float[1024];
            Assert.Throws<ObjectDisposedException>(() => reader.ReadSamples(buf, 0, buf.Length));
        }

        [Fact]
        public void SeekTo_AfterDispose_ThrowsObjectDisposedException()
        {
            var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reader.SeekTo(0L, SeekOrigin.Begin));
        }

        [Fact]
        public void TotalFrames_AfterDispose_ThrowsObjectDisposedException()
        {
            var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = reader.TotalFrames);
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.Dispose();
            reader.Dispose();
        }

        // ── After EOS ────────────────────────────────────────────────────────

        [Fact]
        public void ReadSamples_AfterEos_ReturnsZero()
        {
            using var reader = new VorbisReader(TestFile("1test.ogg"));
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            Assert.Equal(0, reader.ReadSamples(buf, 0, buf.Length));
        }

        [Fact]
        public void ReadSamples_AfterEosRepeated_ConsistentlyReturnsZero()
        {
            using var reader = new VorbisReader(TestFile("1test.ogg"));
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(0, reader.ReadSamples(buf, 0, buf.Length));
            }
        }

        [Fact]
        public void IsEndOfStream_BeforeRead_IsFalse()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.False(reader.IsEndOfStream);
        }

        [Fact]
        public void IsEndOfStream_AfterDrain_IsTrue()
        {
            using var reader = new VorbisReader(TestFile("1test.ogg"));
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            Assert.True(reader.IsEndOfStream);
        }

        [Fact]
        public void IsEndOfStream_AfterSeekBack_IsFalse()
        {
            using var reader = new VorbisReader(TestFile("1test.ogg"));
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            reader.SeekTo(0L, SeekOrigin.Begin);
            Assert.False(reader.IsEndOfStream);
        }
    }
}
