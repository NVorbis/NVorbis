using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class SeekEdgeCaseTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        // ── SeekOrigin.End ───────────────────────────────────────────────────

        [Fact]
        public void SeekTo_End_ZeroOffset_SeeksToEndOfStream()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            long total = reader.TotalSamples;
            reader.SeekTo(0L, SeekOrigin.End);
            Assert.Equal(total, reader.SamplePosition);
        }

        [Fact]
        public void SeekTo_End_PositiveOffset_SeeksToTotalMinusOffset()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            long total = reader.TotalSamples;
            long offset = Math.Min(1000L, total);
            reader.SeekTo(offset, SeekOrigin.End);
            Assert.Equal(total - offset, reader.SamplePosition);
        }

        [Fact]
        public void SeekTo_End_PositiveOffset_CanReadSamples()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            long offset = Math.Min(1000L, reader.TotalSamples);
            reader.SeekTo(offset, SeekOrigin.End);
            var buf = new float[offset * reader.Channels];
            Assert.True(reader.ReadSamples(buf, 0, buf.Length) > 0);
        }

        // ── Negative resulting position ──────────────────────────────────────

        [Fact]
        public void SeekTo_Begin_NegativePosition_Throws()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.SeekTo(-1L, SeekOrigin.Begin));
        }

        [Fact]
        public void SeekTo_End_OffsetBeyondTotal_Throws()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            long overflow = reader.TotalSamples + 1;
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.SeekTo(overflow, SeekOrigin.End));
        }

        [Fact]
        public void SeekTo_Current_NegativeDelta_Throws()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.SeekTo(-1L, SeekOrigin.Current));
        }

        // ── SamplePosition property setter ───────────────────────────────────

        [Fact]
        public void SamplePosition_Setter_UpdatesPosition()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.SamplePosition = 1000L;
            Assert.Equal(1000L, reader.SamplePosition);
        }

        [Fact]
        public void SamplePosition_Setter_CanReadSamples()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.SamplePosition = 500L;
            var buf = new float[reader.SampleRate * reader.Channels];
            Assert.True(reader.ReadSamples(buf, 0, buf.Length) > 0);
        }

        // ── TimePosition property setter ─────────────────────────────────────

        [Fact]
        public void TimePosition_Setter_UpdatesPosition()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var target = TimeSpan.FromSeconds(1);
            reader.TimePosition = target;
            // position may be slightly off due to rounding to nearest packet boundary
            Assert.True(Math.Abs((reader.TimePosition - target).TotalSeconds) < 0.1,
                $"Expected ~1s but got {reader.TimePosition.TotalSeconds:F3}s");
        }

        [Fact]
        public void TimePosition_Getter_ReflectsSamplePosition()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.SeekTo(1000L, SeekOrigin.Begin);
            double expected = 1000.0 / reader.SampleRate;
            Assert.True(Math.Abs(reader.TimePosition.TotalSeconds - expected) < 1e-6,
                $"TimePosition {reader.TimePosition.TotalSeconds} != expected {expected}");
        }

        // ── SeekTo(TimeSpan, SeekOrigin) overload ────────────────────────────

        [Fact]
        public void SeekTo_TimeSpan_Begin_SeeksCorrectly()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.SeekTo(TimeSpan.FromSeconds(1), SeekOrigin.Begin);
            Assert.True(Math.Abs((reader.TimePosition - TimeSpan.FromSeconds(1)).TotalSeconds) < 0.1);
        }

        [Fact]
        public void SeekTo_TimeSpan_Current_AdvancesPosition()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.SeekTo(TimeSpan.FromSeconds(1), SeekOrigin.Begin);
            var before = reader.SamplePosition;
            reader.SeekTo(TimeSpan.FromSeconds(1), SeekOrigin.Current);
            Assert.True(reader.SamplePosition > before);
        }

        [Fact]
        public void SeekTo_TimeSpan_End_SeeksFromEnd()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            long total = reader.TotalSamples;
            reader.SeekTo(TimeSpan.Zero, SeekOrigin.End);
            Assert.Equal(total, reader.SamplePosition);
        }
    }
}
