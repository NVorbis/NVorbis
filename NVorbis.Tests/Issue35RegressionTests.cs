using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    // Regression tests for issue #35: "the number of samples returned by ReadSamples() does not
    // line up with SamplePosition ... seeking to SamplePosition = 0 before reading improves the
    // issue, but doesn't entirely fix it."
    //
    // Root cause: issue6test.ogg's granule timeline doesn't start at 0 (a stream cut or capture
    // starting mid-broadcast). Read-pickup used the raw file timeline while SeekTo(0)'s first-page
    // snap forced granulePos = 0, so the two paths disagreed by the timeline's true start offset --
    // matching the reporter's "seeking to 0 improves it, but doesn't fix it" symptom exactly.
    // StreamDecoder now learns the stream's start granule at construction time and normalizes
    // every position (SamplePosition, TotalSamples, seek targets) to be 0-based from there.
    //
    // issue37test.ogg additionally violates the Vorbis I spec: its setup header spills onto the
    // first data page (a continuation), so packet 0 there is header tail, not audio. The same
    // normalization logic must skip that packet when walking for the start granule, or it
    // computes garbage from decoding non-audio data as if it were a packet.
    public class Issue35RegressionTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        [Theory]
        [InlineData("issue6test.ogg")]
        [InlineData("issue37test.ogg")]
        public void CumulativeReadLength_MatchesSamplePosition(string file)
        {
            var rand = new Random(35 ^ file.GetHashCode());
            using var reader = new VorbisReader(TestFile(file));

            long expected = 0;
            for (int i = 0; i < 500; i++)
            {
                int readLen = rand.Next(1, reader.Channels * 4096 + 1);
                readLen -= readLen % reader.Channels;
                if (readLen == 0) readLen = reader.Channels;

                var buf = new float[readLen];
                int count = reader.ReadSamples(buf, 0, readLen);
                if (count == 0) break;

                expected += count / reader.Channels;
                Assert.Equal(expected, reader.SamplePosition);
            }
        }

        [Theory]
        [InlineData("issue6test.ogg")]
        [InlineData("issue37test.ogg")]
        public void SeekToZero_MatchesFreshOpenPosition(string file)
        {
            using var openReader = new VorbisReader(TestFile(file));
            var openBuf = new float[openReader.Channels * 4096];
            int openCount = openReader.ReadSamples(openBuf, 0, openBuf.Length);

            using var seekReader = new VorbisReader(TestFile(file));
            seekReader.SeekTo(0L, SeekOrigin.Begin);
            Assert.Equal(0L, seekReader.SamplePosition);
            var seekBuf = new float[seekReader.Channels * 4096];
            int seekCount = seekReader.ReadSamples(seekBuf, 0, seekBuf.Length);

            Assert.Equal(openCount, seekCount);
            Assert.Equal(openReader.SamplePosition, seekReader.SamplePosition);
        }

        [Theory]
        [InlineData("issue6test.ogg")]
        [InlineData("issue37test.ogg")]
        public void SamplePosition_Zero_AfterOpen(string file)
        {
            using var reader = new VorbisReader(TestFile(file));
            Assert.Equal(0L, reader.SamplePosition);
        }
    }
}
