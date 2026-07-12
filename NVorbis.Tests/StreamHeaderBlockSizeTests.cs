using System;
using System.Collections.Generic;
using System.IO;
using NVorbis.Contracts;
using Xunit;

namespace NVorbis.Tests
{
    // Vorbis I spec §4.2.2: legal block sizes are 64..8192 with blocksize[0] <= blocksize[1].
    // The 4-bit exponent field can encode 1..32768, so out-of-range values must be rejected
    // at the header instead of reaching Mdct (where e.g. n < 64 corrupts its setup tables).
    // Header packets are hand-built; no real .ogg fixture carries an illegal block size.
    public class StreamHeaderBlockSizeTests
    {
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

        private static byte[] StreamHeaderWithBlockSizes(int block0Exp, int block1Exp) =>
            new BitWriter()
                .WriteBytes(0x01, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73, 0x00, 0x00, 0x00, 0x00) // "\x01vorbis\0\0\0\0"
                .Write(2, 8)            // channels
                .Write(44100, 32)       // sample rate
                .Write(0, 32)           // upper bitrate
                .Write(128000, 32)      // nominal bitrate
                .Write(0, 32)           // lower bitrate
                .Write((ulong)block0Exp, 4)
                .Write((ulong)block1Exp, 4)
                .ToArray();

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

        [Theory]
        [InlineData(5, 11)]     // block0 = 32, below minimum
        [InlineData(0, 11)]     // block0 = 1
        [InlineData(8, 14)]     // block1 = 16384, above maximum
        [InlineData(11, 8)]     // block0 > block1
        [InlineData(15, 15)]    // both 32768
        public void StreamHeader_IllegalBlockSizes_ThrowsArgumentException(int block0Exp, int block1Exp)
        {
            var ex = ConstructAndCapture(StreamHeaderWithBlockSizes(block0Exp, block1Exp));
            Assert.IsType<ArgumentException>(ex);
        }

        [Theory]
        [InlineData(6, 6)]      // 64/64 — smallest legal
        [InlineData(6, 13)]     // 64/8192 — full legal span
        [InlineData(13, 13)]    // 8192/8192 — largest legal
        public void StreamHeader_LegalBlockSizes_PassesHeaderStage(int block0Exp, int block1Exp)
        {
            // Follow the header with a comments packet that declares a vendor string longer
            // than the packet. That stage throws InvalidDataException -- so seeing it proves
            // the stream header stage accepted these block sizes (rejection would surface as
            // ArgumentException before the comments packet is ever parsed).
            var badComments = new BitWriter()
                .WriteBytes(0x03, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73) // "\x03vorbis"
                .Write(100, 32)
                .ToArray();

            var ex = ConstructAndCapture(StreamHeaderWithBlockSizes(block0Exp, block1Exp), badComments);
            Assert.IsType<InvalidDataException>(ex);
        }
    }
}
