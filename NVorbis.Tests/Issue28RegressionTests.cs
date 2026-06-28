using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    // Regression tests for issue #28: a file produced by a libvorbis encoder that
    // mis-counts the granule position of the final long block across a long->short
    // block-size boundary.  The backwards granule calculation in PacketProvider then
    // disagrees with the page's stored granulePos.  PacketProvider.GetIsVorbisBugDiff
    // recognizes that specific (longBlock/4 - shortBlock/4) discrepancy and compensates
    // instead of throwing "GranulePos mismatch".  These guard that the workaround stays
    // in place — decoding and seeking such a file must not throw.
    public class Issue28RegressionTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        private const string Issue28File = "issue28test.ogg";

        [Fact]
        public void Issue28_Open_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue28File));
            Assert.True(reader.Channels > 0);
            Assert.True(reader.SampleRate > 0);
        }

        [Fact]
        public void Issue28_TotalSamples_Positive()
        {
            using var reader = new VorbisReader(TestFile(Issue28File));
            Assert.True(reader.TotalSamples > 0);
        }

        [Fact]
        public void Issue28_DecodeToEnd_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue28File));
            var buf = new float[reader.Channels * 4096];
            long total = 0;
            int count;
            while ((count = reader.ReadSamples(buf, 0, buf.Length)) > 0)
            {
                total += count;
            }
            Assert.True(total > 0);
            Assert.True(reader.IsEndOfStream);
        }

        [Fact]
        public void Issue28_SeekMidStream_DoesNotThrow()
        {
            // the mid-stream seek runs FindPacket, which is where the granule-pos
            // mismatch workaround lives
            using var reader = new VorbisReader(TestFile(Issue28File));
            var target = reader.TotalSamples / 2;

            reader.SeekTo(target, SeekOrigin.Begin);

            var buf = new float[reader.Channels * 4096];
            Assert.True(reader.ReadSamples(buf, 0, buf.Length) > 0);
        }

        [Fact]
        public void Issue28_SeekNearEnd_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue28File));
            var target = Math.Max(0, reader.TotalSamples - 1024);

            reader.SeekTo(target, SeekOrigin.Begin);
            Assert.Equal(target, reader.SamplePosition);
        }

        [Fact]
        public void Issue28_SeekToSpecificSample_ReadsSamples()
        {
            // exact sample position from the issue report that landed on the mis-counted
            // long block; the seek must succeed and produce samples
            using var reader = new VorbisReader(TestFile(Issue28File));

            reader.SeekTo(678951L, SeekOrigin.Begin);

            var buf = new float[reader.Channels * 4096];
            var count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count > 0);
        }

        [Fact]
        public void Issue28_SeekToZero_AfterFullRead_CanReadAgain()
        {
            using var reader = new VorbisReader(TestFile(Issue28File));
            var buf = new float[reader.Channels * 4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }

            reader.SeekTo(0L, SeekOrigin.Begin);
            Assert.True(reader.ReadSamples(buf, 0, buf.Length) > 0);
        }
    }
}
