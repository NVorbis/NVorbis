using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class ChainedStreamTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        private static MemoryStream ConcatOgg(string file1, string file2)
        {
            var a = File.ReadAllBytes(TestFile(file1));
            var b = File.ReadAllBytes(TestFile(file2));
            var combined = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, combined, 0, a.Length);
            Buffer.BlockCopy(b, 0, combined, a.Length, b.Length);
            return new MemoryStream(combined);
        }

        [Fact]
        public void FindNextStream_ChainedOgg_ReturnsTrue()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            Assert.True(reader.FindNextStream());
        }

        [Fact]
        public void FindNextStream_ChainedOgg_AddedToStreamsList()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.FindNextStream();
            Assert.Equal(2, reader.Streams.Count);
        }

        [Fact]
        public void FindNextStream_SingleOgg_ReturnsFalse()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            Assert.False(reader.FindNextStream());
        }

        [Fact]
        public void StreamIndex_DefaultIsZero()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.FindNextStream();
            Assert.Equal(0, reader.StreamIndex);
        }

        [Fact]
        public void SwitchStreams_ToIndex1_ChangesStreamIndex()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.FindNextStream();
            reader.SwitchStreams(1);
            Assert.Equal(1, reader.StreamIndex);
        }

        [Fact]
        public void SwitchStreams_ToSameStream_ReturnsFalse()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.FindNextStream();
            Assert.False(reader.SwitchStreams(0));
        }

        [Fact]
        public void SwitchStreams_OutOfRange_Throws()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.FindNextStream();
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.SwitchStreams(2));
        }

        [Fact]
        public void ReadSamples_SecondStream_ReturnsSamples()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.FindNextStream();
            reader.SwitchStreams(1);
            var buf = new float[reader.SampleRate * reader.Channels];
            Assert.True(reader.ReadSamples(buf, 0, buf.Length) > 0);
        }

        [Fact]
        public void ReadSamples_FirstStreamDrained_IsEndOfStream()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            var buf = new float[4096];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }
            Assert.True(reader.IsEndOfStream);
        }

        [Fact]
        public void NewStream_Event_RaisedForSecondStream()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            int eventCount = 0;
            reader.NewStream += (_, _) => eventCount++;
            reader.FindNextStream();
            Assert.Equal(1, eventCount);
        }

        [Fact]
        public void NewStream_IgnoreStream_StreamNotAdded()
        {
            using var ms = ConcatOgg("1test.ogg", "2test.ogg");
            using var reader = new VorbisReader(ms, closeOnDispose: false);
            reader.NewStream += (_, ea) => ea.IgnoreStream = true;
            reader.FindNextStream();
            Assert.Single(reader.Streams);
        }
    }
}
