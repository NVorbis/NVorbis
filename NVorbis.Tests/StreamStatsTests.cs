using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class StreamStatsTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        [Fact]
        public void StreamStats_AfterReading_PacketCountPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.PacketCount > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_AudioBitsPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.AudioBits > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_EffectiveBitRatePositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.EffectiveBitRate > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_InstantBitRatePositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.InstantBitRate > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_ContainerBitsPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.ContainerBits > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_OverheadBitsPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.OverheadBits > 0);
        }

        [Fact]
        public void StreamStats_ResetStats_ResetsPacketCount()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            reader.StreamStats.ResetStats();
            Assert.Equal(0, reader.StreamStats.PacketCount);
        }

        [Fact]
        public void StreamStats_ResetStats_ResetsAudioBits()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            reader.StreamStats.ResetStats();
            Assert.Equal(0L, reader.StreamStats.AudioBits);
        }

        [Fact]
        public void ContainerOverheadBits_AfterReading_Positive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.ContainerOverheadBits > 0);
        }

        [Fact]
        public void StreamStats_MoreReads_PacketCountIncreases()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            int after1 = reader.StreamStats.PacketCount;
            reader.ReadSamples(buf, 0, buf.Length);
            int after2 = reader.StreamStats.PacketCount;
            Assert.True(after2 > after1);
        }
    }
}
