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

            var before = reader.FramePosition;
            const long offset = 1000L;

            reader.SeekTo(offset, SeekOrigin.Current);

            Assert.Equal(before + offset, reader.FramePosition);
        }

        [Fact]
        public void SeekOriginCurrent_ZeroOffset_KeepsPosition()
        {
            using var reader = new VorbisReader(TestFile(OggFile));

            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);

            var before = reader.FramePosition;

            reader.SeekTo(0L, SeekOrigin.Current);

            Assert.Equal(before, reader.FramePosition);
        }

        [Fact]
        public void SeekOriginCurrent_LargeOffset_DoesNotGoBackward()
        {
            using var reader = new VorbisReader(TestFile(OggFile));

            // start at 2 seconds in
            var buf = new float[reader.SampleRate * reader.Channels * 2];
            reader.ReadSamples(buf, 0, buf.Length);

            var before = reader.FramePosition;
            const long offset = 5000L;

            reader.SeekTo(offset, SeekOrigin.Current);

            // must be strictly forward
            Assert.True(reader.FramePosition > before,
                $"Expected position > {before} but got {reader.FramePosition}");
            Assert.Equal(before + offset, reader.FramePosition);
        }

        [Fact]
        public void SeekOriginBegin_SeeksToAbsolutePosition()
        {
            using var reader = new VorbisReader(TestFile(OggFile));

            reader.SeekTo(1000L, SeekOrigin.Begin);

            Assert.Equal(1000L, reader.FramePosition);
        }

        // Regression tests for issue #37: seeking to position 0 on a file whose last
        // header page has granule = -1 (spec-compliant) triggered a false positive in
        // the libogg-bug detector, landing the reader on a header packet and throwing
        // "Could not read pre-roll packet".

        private const string Issue37File = "issue37test.ogg";

        [Fact]
        public void Issue37_SeekToZeroAfterEos_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue37File));
            var buf = new float[4096];

            // drain to EOS
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }

            // must not throw
            reader.SeekTo(0L, SeekOrigin.Begin);
            Assert.Equal(0L, reader.FramePosition);
        }

        [Fact]
        public void Issue37_SeekToZeroAfterEos_CanReadSamplesAgain()
        {
            using var reader = new VorbisReader(TestFile(Issue37File));
            var buf = new float[4096];

            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }

            reader.SeekTo(0L, SeekOrigin.Begin);

            var count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count > 0, "Expected samples after seeking to start");
        }

        [Fact]
        public void Issue37_SeekToZeroWithoutEos_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue37File));

            reader.SeekTo(0L, SeekOrigin.Begin);

            Assert.Equal(0L, reader.FramePosition);
        }

        [Fact]
        public void Issue37_SeekViaTimePositionAfterEos_DoesNotThrow()
        {
            using var reader = new VorbisReader(TestFile(Issue37File));
            var buf = new float[4096];

            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }

            reader.SeekTo(TimeSpan.Zero);
            Assert.Equal(0L, reader.FramePosition);
        }

        [Fact]
        public void Issue37_SeekToEarlyNonZeroPosition_DoesNotThrow()
        {
            // Position 64 falls within the first-page shortcut range; verify
            // the shortcut branch still lands at the right sample offset.
            using var reader = new VorbisReader(TestFile(Issue37File));
            var target = Math.Min(64L, reader.TotalFrames - 1);
            reader.SeekTo(target, SeekOrigin.Begin);
            Assert.Equal(target, reader.FramePosition);
        }

        [Fact]
        public void Issue37_SeekToMiddle_DoesNotThrow()
        {
            // Mid-stream seek exercises the normal FindPacket path (past FDI).
            using var reader = new VorbisReader(TestFile(Issue37File));
            var mid = reader.TotalFrames / 2;
            if (mid == 0) return;
            reader.SeekTo(mid, SeekOrigin.Begin);
            Assert.Equal(mid, reader.FramePosition);
        }

        [Fact]
        public void Issue37_SeekToMiddle_CanReadSamples()
        {
            using var reader = new VorbisReader(TestFile(Issue37File));
            var mid = reader.TotalFrames / 2;
            if (mid == 0) return;
            reader.SeekTo(mid, SeekOrigin.Begin);
            Assert.Equal(mid, reader.FramePosition);
            var buf = new float[4096];
            var count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count > 0, "Expected samples after seeking to midpoint");
        }

        [Fact]
        public void Issue37_SeekToMiddleAfterEos_DoesNotThrow()
        {
            // Drain to EOS then seek to a non-zero position; exercises the
            // normal path from a post-EOS state.
            using var reader = new VorbisReader(TestFile(Issue37File));
            var mid = reader.TotalFrames / 2;
            if (mid == 0) return;
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            reader.SeekTo(mid, SeekOrigin.Begin);
            Assert.Equal(mid, reader.FramePosition);
        }

        [Fact]
        public void Issue37_SeekToMiddleAfterEos_CanReadSamples()
        {
            using var reader = new VorbisReader(TestFile(Issue37File));
            var mid = reader.TotalFrames / 2;
            if (mid == 0) return;
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            reader.SeekTo(mid, SeekOrigin.Begin);
            Assert.Equal(mid, reader.FramePosition);
            var count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count > 0, "Expected samples after seeking to midpoint post-EOS");
        }
    }
}
