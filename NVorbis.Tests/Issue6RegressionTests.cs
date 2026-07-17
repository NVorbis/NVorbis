using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    // Regression tests for issue #6: a file whose final EOS page has no data
    // packets.  The Vorbis spec requires the EOS page to have exactly one data
    // packet; this file violates that requirement.  PR #8 fixed the crash that
    // resulted from hitting such a page — NVorbis now handles it gracefully.
    public class Issue6RegressionTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        private const string Issue6File = "issue6test.ogg";

        [Fact]
        public void Issue6_Open_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue6File));
            Assert.True(reader.Channels > 0);
        }

        [Fact]
        public void Issue6_ReadSamples_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue6File));
            var buf = new float[4096];
            var count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count >= 0);
        }

        [Fact]
        public void Issue6_DrainToEos_DoesNotCrash()
        {
            using var reader = new VorbisReader(TestFile(Issue6File));
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            Assert.True(reader.IsEndOfStream);
        }

        [Fact]
        public void Issue6_TotalFrames_Positive()
        {
            using var reader = new VorbisReader(TestFile(Issue6File));
            Assert.True(reader.TotalFrames > 0);
        }

        [Fact]
        public void Issue6_SeekToZero_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue6File));
            reader.SeekTo(0L, SeekOrigin.Begin);
            Assert.Equal(0L, reader.FramePosition);
        }

        [Fact]
        public void Issue6_SeekToZeroAfterEos_CanReadSamples()
        {
            using var reader = new VorbisReader(TestFile(Issue6File));
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            reader.SeekTo(0L, SeekOrigin.Begin);
            var count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count > 0);
        }
    }
}
