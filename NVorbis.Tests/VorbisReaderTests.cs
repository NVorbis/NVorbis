using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class VorbisReaderTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        [Fact]
        public void ReadSamples_EmptySpan_ReturnsZero()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var result = reader.ReadSamples(Span<float>.Empty);
            Assert.Equal(0, result);
        }

        [Fact]
        public void ReadSamples_SpanShorterThanOneFrame_ReturnsZero()
        {
            // count = buffer.Length - buffer.Length % Channels
            // When buffer.Length < Channels, count == 0: no complete frame fits.
            // Old guard "!buffer.IsEmpty" passed through to Read(buf, 0, 0) for non-empty buffers.
            // Correct guard is "count > 0", matching the array overload.
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            if (reader.Channels < 2)
            {
                // Mono streams have no sub-frame case; skip.
                return;
            }
            var buffer = new float[reader.Channels - 1];
            var result = reader.ReadSamples(new Span<float>(buffer));
            Assert.Equal(0, result);
        }

        [Fact]
        public void ReadSamples_AlignedSpan_ReturnsData()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buffer = new float[reader.SampleRate * reader.Channels];
            var result = reader.ReadSamples(new Span<float>(buffer));
            Assert.True(result > 0, "Expected samples from a valid stream");
        }

        [Fact]
        public void ReadSamples_SpanLengthNotAligned_ClampsToFrameBoundary()
        {
            // A buffer one float over a frame boundary returns the same count
            // as one that's exactly on the boundary.
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            int channels = reader.Channels;
            int aligned = reader.SampleRate * channels;

            var bufAligned = new float[aligned];
            var bufExtra = new float[aligned + 1];

            int r1 = reader.ReadSamples(new Span<float>(bufAligned));
            reader.SeekTo(0L, SeekOrigin.Begin);
            int r2 = reader.ReadSamples(new Span<float>(bufExtra));

            Assert.Equal(r1, r2);
        }
    }
}
