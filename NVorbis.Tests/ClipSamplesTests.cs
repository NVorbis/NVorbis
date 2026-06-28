using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class ClipSamplesTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        [Fact]
        public void ClipSamples_DefaultIsTrue()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.True(reader.ClipSamples);
        }

        [Fact]
        public void HasClipped_InitiallyFalse()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.False(reader.HasClipped);
        }

        [Fact]
        public void ClipSamples_True_AllSamplesWithinRange()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.ClipSamples = true;
            var buf = new float[reader.SampleRate * reader.Channels];
            int count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count > 0);
            for (int i = 0; i < count; i++)
            {
                Assert.True(buf[i] >= -1f && buf[i] <= 1f,
                    $"buf[{i}] = {buf[i]} is outside [-1, 1] with ClipSamples=true");
            }
        }

        [Fact]
        public void ClipSamples_False_StillReturnsSamples()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.ClipSamples = false;
            var buf = new float[reader.SampleRate * reader.Channels];
            int count = reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(count > 0);
        }

        [Fact]
        public void HasClipped_RemainsfalseWithClipSamplesFalse()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.ClipSamples = false;
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.False(reader.HasClipped);
        }

        [Fact]
        public void ClipSamples_CanBeToggled()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            reader.ClipSamples = false;
            Assert.False(reader.ClipSamples);
            reader.ClipSamples = true;
            Assert.True(reader.ClipSamples);
        }

        [Fact]
        public void ClipSamples_SwitchStreams_PropagatedToNewStream()
        {
            // SwitchStreams carries the clipping setting from old to new decoder.
            var bytes1 = System.IO.File.ReadAllBytes(TestFile("1test.ogg"));
            var bytes2 = System.IO.File.ReadAllBytes(TestFile("2test.ogg"));
            var combined = new byte[bytes1.Length + bytes2.Length];
            Buffer.BlockCopy(bytes1, 0, combined, 0, bytes1.Length);
            Buffer.BlockCopy(bytes2, 0, combined, bytes1.Length, bytes2.Length);
            using var ms = new MemoryStream(combined);
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.ClipSamples = false;
            reader.FindNextStream();
            reader.SwitchStreams(1);
            Assert.False(reader.ClipSamples);
        }
    }
}
