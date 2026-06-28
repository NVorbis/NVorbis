using NVorbis.Contracts;
using System;
using Xunit;

namespace NVorbis.Tests
{
    public class ExtensionsTests
    {
        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;
            public ByteArrayPacket(byte[] data) => _data = data;
            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;
        }

        // ── Read zero-count edge cases ───────────────────────────────────────

        [Fact]
        public void Read_ZeroCountAtBufferEnd_ReturnsZero()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x01 });
            var buf = new byte[4];
            // index == buffer.Length with count 0 is a valid no-op read
            Assert.Equal(0, packet.Read(buf, buf.Length, 0));
        }

        [Fact]
        public void Read_ZeroCountEmptyBuffer_ReturnsZero()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x01 });
            Assert.Equal(0, packet.Read(Array.Empty<byte>(), 0, 0));
        }

        [Fact]
        public void Read_NegativeIndex_Throws()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x01 });
            Assert.Throws<ArgumentOutOfRangeException>(() => packet.Read(new byte[4], -1, 0));
        }

        [Fact]
        public void Read_CountExceedsBuffer_Throws()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x01 });
            Assert.Throws<ArgumentOutOfRangeException>(() => packet.Read(new byte[4], 2, 3));
        }

        [Fact]
        public void Read_CopiesBytes()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            var buf = new byte[4];
            Assert.Equal(4, packet.Read(buf, 0, 4));
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, buf);
        }

        // ── ReadBytes ────────────────────────────────────────────────────────

        [Fact]
        public void ReadBytes_Zero_ReturnsEmptyArray()
        {
            // previously threw ArgumentOutOfRangeException because the guard rejected index == buffer.Length
            IPacket packet = new ByteArrayPacket(new byte[] { 0x01 });
            Assert.Empty(packet.ReadBytes(0));
        }

        [Fact]
        public void ReadBytes_TruncatedStream_ReturnsShortArray()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x11, 0x22 });
            // ask for more than exist; result is trimmed to what was available
            var result = packet.ReadBytes(5);
            Assert.Equal(new byte[] { 0x11, 0x22 }, result);
        }

        // ── flag accessors (boxing-free GetFlag) ─────────────────────────────

        [Fact]
        public void Flags_AreIndependent()
        {
            var packet = new ByteArrayPacket(new byte[] { 0x00 }) { IsResync = true, IsEndOfStream = false };
            Assert.True(packet.IsResync);
            Assert.False(packet.IsEndOfStream);
        }
    }
}
