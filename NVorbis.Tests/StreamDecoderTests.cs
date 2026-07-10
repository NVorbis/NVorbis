using System;
using System.Collections.Generic;
using System.IO;
using NVorbis.Contracts;
using NVorbis.Ogg;
using Xunit;

namespace NVorbis.Tests
{
    // Targets StreamDecoder's malformed/non-Vorbis-input handling, which no real .ogg fixture
    // exercises (every fixture is a valid Vorbis stream). Header packets are hand-built here;
    // the setup (codebooks) header is deliberately never made valid -- synthesizing one is as
    // involved as writing an encoder -- so these tests stop at the point construction fails or
    // throws, which is exactly the behavior under test.
    public class StreamDecoderTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        // Packs fields LSB-first within each byte, matching DataPacket's bit order.
        private class BitWriter
        {
            private readonly List<byte> _bytes = new();
            private ulong _bucket;
            private int _bucketBits;

            public BitWriter Write(ulong value, int bits)
            {
                _bucket |= (value & (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1)) << _bucketBits;
                _bucketBits += bits;
                while (_bucketBits >= 8)
                {
                    _bytes.Add((byte)(_bucket & 0xFF));
                    _bucket >>= 8;
                    _bucketBits -= 8;
                }
                return this;
            }

            public BitWriter WriteBytes(params byte[] bytes)
            {
                foreach (var b in bytes) Write(b, 8);
                return this;
            }

            public byte[] ToArray()
            {
                var result = new List<byte>(_bytes);
                if (_bucketBits > 0)
                {
                    result.Add((byte)(_bucket & 0xFF));
                }
                return result.ToArray();
            }
        }

        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;

            public ByteArrayPacket(byte[] data) => _data = data;

            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;

            public override void Reset()
            {
                base.Reset();
                _pos = 0;
            }
        }

        // Minimal in-order IPacketProvider over a fixed list of header packets.
        private class FakeHeaderPacketProvider : IPacketProvider
        {
            private readonly List<IPacket> _packets;
            private int _index;

            public FakeHeaderPacketProvider(params IPacket[] packets) => _packets = new List<IPacket>(packets);

            public bool CanSeek => false;
            public int StreamSerial => 0;
            public IPacket PeekNextPacket() => _index < _packets.Count ? _packets[_index] : null;
            public IPacket GetNextPacket() => _index < _packets.Count ? _packets[_index++] : null;
            public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount) =>
                throw new NotSupportedException();
            public long GetGranuleCount() => throw new NotSupportedException();
        }

        private static readonly byte[] ValidStreamHeader =
            new BitWriter()
                .WriteBytes(0x01, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73, 0x00, 0x00, 0x00, 0x00) // "\x01vorbis\0\0\0\0"
                .Write(2, 8)            // channels
                .Write(44100, 32)       // sample rate
                .Write(0, 32)           // upper bitrate
                .Write(128000, 32)      // nominal bitrate
                .Write(0, 32)           // lower bitrate
                .Write(8, 4)            // block0Size exponent (2^8 = 256)
                .Write(11, 4)           // block1Size exponent (2^11 = 2048)
                .ToArray();

        private static byte[] ValidCommentsHeader(string vendor = "", int commentCount = 0) =>
            new BitWriter()
                .WriteBytes(0x03, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73) // "\x03vorbis"
                .Write((ulong)vendor.Length, 32)
                .WriteBytes(System.Text.Encoding.UTF8.GetBytes(vendor))
                .Write((ulong)commentCount, 32)
                .ToArray();

        private static byte[] InvalidSignaturePacket(params byte[] bytes) => new BitWriter().WriteBytes(bytes).ToArray();

        private static Exception ConstructAndCapture(params byte[][] packets)
        {
            var provider = new FakeHeaderPacketProvider(Array.ConvertAll(packets, p => (IPacket)new ByteArrayPacket(p)));
            try
            {
                new StreamDecoder(provider);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        // ── Non-Vorbis codec detection (GetInvalidStreamException) ────────────
        // These magic numbers are read as a single 64-bit LSB-first value, matching how real
        // OpusHead/fLaC/Speex/fishead/theora identification headers begin.

        [Fact]
        public void FirstPacket_OpusMagic_ThrowsWithOpusMessage()
        {
            var ex = ConstructAndCapture(new BitWriter().Write(0x646165487375704ful, 64).ToArray());
            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("OPUS", ex.Message);
        }

        [Fact]
        public void FirstPacket_FlacMagic_ThrowsWithFlacMessage()
        {
            // Only the low byte (0x7F) is checked.
            var ex = ConstructAndCapture(new BitWriter().Write(0x7F, 8).Write(0, 56).ToArray());
            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("FLAC", ex.Message);
        }

        [Fact]
        public void FirstPacket_SpeexMagic_ThrowsWithSpeexMessage()
        {
            var ex = ConstructAndCapture(new BitWriter().Write(0x2020207865657053ul, 64).ToArray());
            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("Speex", ex.Message);
        }

        [Fact]
        public void FirstPacket_SkeletonMagic_ThrowsWithSkeletonMessage()
        {
            var ex = ConstructAndCapture(new BitWriter().Write(0x0064616568736966ul, 64).ToArray());
            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("Skeleton", ex.Message);
        }

        [Fact]
        public void FirstPacket_TheoraMagic_ThrowsWithTheoraMessage()
        {
            var ex = ConstructAndCapture(new BitWriter().Write(0x61726f65687400ul, 64).ToArray());
            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("Theora", ex.Message);
        }

        [Fact]
        public void FirstPacket_UnrecognizedGarbage_ThrowsGenericMessage()
        {
            var ex = ConstructAndCapture(new BitWriter().Write(0xDEADBEEFCAFEBABEul, 64).ToArray());
            Assert.IsType<ArgumentException>(ex);
            Assert.Contains("Could not find Vorbis data", ex.Message);
        }

        [Fact]
        public void FirstPacket_TooShortForSniff_DoesNotThrowUnrelatedException()
        {
            // Fewer than 64 bits available -- ReadBits returns a truncated value; must still
            // resolve to a clean ArgumentException, not something like an IndexOutOfRange.
            var ex = ConstructAndCapture(new BitWriter().Write(0x01, 8).ToArray());
            Assert.IsType<ArgumentException>(ex);
        }

        // ── Header-stage validation and short packet sequences ────────────────

        [Fact]
        public void CommentsPacket_BadSignature_ThrowsArgumentException()
        {
            var ex = ConstructAndCapture(ValidStreamHeader, InvalidSignaturePacket(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
            Assert.IsType<ArgumentException>(ex);
        }

        [Fact]
        public void BooksPacket_BadSignature_ThrowsArgumentException()
        {
            var ex = ConstructAndCapture(ValidStreamHeader, ValidCommentsHeader(), InvalidSignaturePacket(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
            Assert.IsType<ArgumentException>(ex);
        }

        [Fact]
        public void BooksPacket_Missing_ThrowsArgumentException()
        {
            // Only 2 of the 3 required header packets are available.
            var ex = ConstructAndCapture(ValidStreamHeader, ValidCommentsHeader());
            Assert.IsType<ArgumentException>(ex);
        }

        [Fact]
        public void CommentsPacket_EmptyVendorString_DoesNotThrowFromReadString()
        {
            // Exercises ReadString's zero-length fast path; construction still fails afterward
            // (no valid books header supplied), but that failure must come from the books stage,
            // not from an exception inside comments parsing.
            var ex = ConstructAndCapture(ValidStreamHeader, ValidCommentsHeader(vendor: ""), InvalidSignaturePacket(0xFF));
            Assert.IsType<ArgumentException>(ex);
        }

        [Fact]
        public void CommentsPacket_VendorStringLongerThanPacket_ThrowsInvalidDataException()
        {
            // Declares a 100-byte vendor string but supplies none -- Extensions.Read comes back
            // short, and ReadString must surface that as a clean InvalidDataException rather
            // than silently truncating or throwing something unrelated.
            var packet = new BitWriter()
                .WriteBytes(0x03, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73)
                .Write(100, 32)
                .ToArray();

            var ex = ConstructAndCapture(ValidStreamHeader, packet);
            Assert.IsType<InvalidDataException>(ex);
        }

        // ── Public single-argument constructor (delegates to the internal one) ─

        [Fact]
        public void PublicConstructor_DelegatesAndSurfacesSameFailure()
        {
            var provider = new FakeHeaderPacketProvider(new ByteArrayPacket(InvalidSignaturePacket(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)));
            Assert.Throws<ArgumentException>(() => new StreamDecoder(provider));
        }

        // ── Disposed-instance access, using a real (valid) fixture ────────────

        private static IPacketProvider GetRealPacketProvider(string fileName, out ContainerReader containerReader)
        {
            var stream = File.OpenRead(TestFile(fileName));
            containerReader = new ContainerReader(stream, closeOnDispose: true);
            Assert.True(containerReader.TryInit());
            return containerReader.GetStreams()[0];
        }

        [Fact]
        public void TotalSamples_AfterDispose_ThrowsObjectDisposedException()
        {
            var provider = GetRealPacketProvider("3test.ogg", out var containerReader);
            using (containerReader)
            {
                var decoder = new StreamDecoder(provider);
                decoder.Dispose();
                Assert.Throws<ObjectDisposedException>(() => _ = decoder.TotalSamples);
            }
        }

        [Fact]
        public void TotalTime_MatchesTotalSamplesAndSampleRate()
        {
            var provider = GetRealPacketProvider("3test.ogg", out var containerReader);
            using (containerReader)
            {
                var decoder = new StreamDecoder(provider);
                var expected = TimeSpan.FromSeconds((double)decoder.TotalSamples / decoder.SampleRate);
                Assert.Equal(expected, decoder.TotalTime);
            }
        }

        [Fact]
        public void SeekTo_InvalidSeekOrigin_ThrowsArgumentOutOfRangeException()
        {
            var provider = GetRealPacketProvider("3test.ogg", out var containerReader);
            using (containerReader)
            {
                var decoder = new StreamDecoder(provider);
                Assert.Throws<ArgumentOutOfRangeException>(() => decoder.SeekTo(0L, (SeekOrigin)99));
            }
        }
    }
}
