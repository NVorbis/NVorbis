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

        // ── ReadBit / PeekByte / ReadByte ────────────────────────────────────

        [Fact]
        public void ReadBit_ReadsLsbFirst()
        {
            // 0b00000101 -> bit0=1, bit1=0, bit2=1
            IPacket packet = new ByteArrayPacket(new byte[] { 0b00000101 });
            Assert.True(packet.ReadBit());
            Assert.False(packet.ReadBit());
            Assert.True(packet.ReadBit());
        }

        [Fact]
        public void PeekByte_DoesNotAdvancePosition()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xAB, 0xCD });
            Assert.Equal(0xAB, packet.PeekByte());
            Assert.Equal(0xAB, packet.PeekByte());
            Assert.Equal(0xAB, packet.ReadByte());
            Assert.Equal(0xCD, packet.ReadByte());
        }

        [Fact]
        public void ReadByte_AdvancesPosition()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x11, 0x22 });
            Assert.Equal(0x11, packet.ReadByte());
            Assert.Equal(0x22, packet.ReadByte());
        }

        // ── ReadInt16 / ReadUInt16 ────────────────────────────────────────────

        [Fact]
        public void ReadInt16_ReadsLittleEndian()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x34, 0x12 });
            Assert.Equal(0x1234, packet.ReadInt16());
        }

        [Fact]
        public void ReadInt16_NegativeValue_SignExtends()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xFF, 0xFF });
            Assert.Equal(-1, packet.ReadInt16());
        }

        [Fact]
        public void ReadUInt16_ReadsLittleEndian()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xFF, 0xFF });
            Assert.Equal((ushort)0xFFFF, packet.ReadUInt16());
        }

        // ── ReadInt32 / ReadUInt32 ────────────────────────────────────────────

        [Fact]
        public void ReadInt32_ReadsLittleEndian()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x78, 0x56, 0x34, 0x12 });
            Assert.Equal(0x12345678, packet.ReadInt32());
        }

        [Fact]
        public void ReadInt32_NegativeValue_SignExtends()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            Assert.Equal(-1, packet.ReadInt32());
        }

        [Fact]
        public void ReadUInt32_ReadsLittleEndian()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            Assert.Equal(0xFFFFFFFFu, packet.ReadUInt32());
        }

        // ── ReadInt64 / ReadUInt64 ────────────────────────────────────────────

        [Fact]
        public void ReadInt64_ReadsLittleEndian()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xEF, 0xCD, 0xAB, 0x90, 0x78, 0x56, 0x34, 0x12 });
            Assert.Equal(0x1234567890ABCDEFL, packet.ReadInt64());
        }

        [Fact]
        public void ReadInt64_NegativeValue_SignExtends()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            Assert.Equal(-1L, packet.ReadInt64());
        }

        [Fact]
        public void ReadUInt64_ReadsLittleEndian()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            Assert.Equal(0xFFFFFFFFFFFFFFFFUL, packet.ReadUInt64());
        }

        // ── SkipBytes ─────────────────────────────────────────────────────────

        [Fact]
        public void SkipBytes_AdvancesPastSkippedData()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x00, 0x00, 0x00, 0x42 });
            packet.SkipBytes(3);
            Assert.Equal(0x42, packet.ReadByte());
        }

        [Fact]
        public void SkipBytes_MoreThanEightBytes_SkipsAllOfThem()
        {
            // exercises the count > 8 loop branch (SkipBits(64) per 8-byte chunk)
            var data = new byte[12];
            data[10] = 0x99;
            IPacket packet = new ByteArrayPacket(data);
            packet.SkipBytes(10);
            Assert.Equal(0x99, packet.ReadByte());
        }

        [Fact]
        public void SkipBytes_Zero_DoesNotAdvance()
        {
            IPacket packet = new ByteArrayPacket(new byte[] { 0x42 });
            packet.SkipBytes(0);
            Assert.Equal(0x42, packet.ReadByte());
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
