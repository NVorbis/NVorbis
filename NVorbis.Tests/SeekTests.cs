using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class SeekTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        // 3test.ogg is long enough for seek tests
        private const string OggFile = "3test.ogg";

        [Fact]
        public void SeekOriginCurrent_PositiveOffset_AdvancesPosition()
        {
            using var reader = new VorbisReader(TestFile(OggFile));

            // advance ~1 second to establish a non-zero position
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);

            var before = reader.SamplePosition;
            const long offset = 1000L;

            reader.SeekTo(offset, SeekOrigin.Current);

            Assert.Equal(before + offset, reader.SamplePosition);
        }

        [Fact]
        public void SeekOriginCurrent_ZeroOffset_KeepsPosition()
        {
            using var reader = new VorbisReader(TestFile(OggFile));

            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);

            var before = reader.SamplePosition;

            reader.SeekTo(0L, SeekOrigin.Current);

            Assert.Equal(before, reader.SamplePosition);
        }

        [Fact]
        public void SeekOriginCurrent_LargeOffset_DoesNotGoBackward()
        {
            using var reader = new VorbisReader(TestFile(OggFile));

            // start at 2 seconds in
            var buf = new float[reader.SampleRate * reader.Channels * 2];
            reader.ReadSamples(buf, 0, buf.Length);

            var before = reader.SamplePosition;
            const long offset = 5000L;

            reader.SeekTo(offset, SeekOrigin.Current);

            // must be strictly forward
            Assert.True(reader.SamplePosition > before,
                $"Expected position > {before} but got {reader.SamplePosition}");
            Assert.Equal(before + offset, reader.SamplePosition);
        }

        [Fact]
        public void SeekOriginBegin_SeeksToAbsolutePosition()
        {
            using var reader = new VorbisReader(TestFile(OggFile));

            reader.SeekTo(1000L, SeekOrigin.Begin);

            Assert.Equal(1000L, reader.SamplePosition);
        }
    }
}
