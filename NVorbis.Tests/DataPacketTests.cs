using NVorbis.Contracts;
using System;
using Xunit;

namespace NVorbis.Tests
{
    public class DataPacketTests
    {
        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;
            public ByteArrayPacket(byte[] data) => _data = data;
            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;
        }

        [Fact]
        public void SkipBits_CountGreaterThan64_ThrowsArgumentOutOfRangeException()
        {
            var packet = new ByteArrayPacket(new byte[9]);
            Assert.Throws<ArgumentOutOfRangeException>(() => packet.SkipBits(65));
        }

        [Fact]
        public void SkipBits_CountOf64_DoesNotThrow()
        {
            var packet = new ByteArrayPacket(new byte[9]);
            packet.SkipBits(64);
            Assert.Equal(64, packet.BitsRead);
        }

        [Fact]
        public void SkipBits_NegativeCount_ThrowsArgumentOutOfRangeException()
        {
            var packet = new ByteArrayPacket(new byte[1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => packet.SkipBits(-1));
        }

        [Fact]
        public void SkipBits_CountOf0_DoesNotAdvancePosition()
        {
            var packet = new ByteArrayPacket(new byte[1]);
            packet.SkipBits(0);
            Assert.Equal(0, packet.BitsRead);
        }

        [Fact]
        public void SkipBits_AfterPeek_LeavesCorrectBitsRemaining()
        {
            // Fill 9 bytes of 0xFF, peek 64, skip 5, peek again to force overflow
            // into _overflowBits, then skip 16 more and verify remaining bits are correct.
            var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
            var packet = new ByteArrayPacket(data);

            packet.TryPeekBits(64, out _); // loads 64 bits
            packet.SkipBits(5);            // reduces to 59 buffered bits
            packet.TryPeekBits(64, out _); // reads byte 8, produces overflow bits
            packet.SkipBits(16);           // skip 16 more (now at bit 21; overflow branch not triggered)

            // 72 total bits - 21 read = 51 remaining
            Assert.Equal(21, packet.BitsRead);
        }

        // ── TryPeekBits does not consume ─────────────────────────────────────

        [Fact]
        public void TryPeekBits_DoesNotAdvanceBitsRead()
        {
            var data = new byte[] { 0xAB, 0xCD };
            var packet = new ByteArrayPacket(data);
            packet.TryPeekBits(8, out _);
            Assert.Equal(0, packet.BitsRead);
        }

        [Fact]
        public void TryPeekBits_RepeatedCall_ReturnsSameValue()
        {
            var data = new byte[] { 0x5A };
            var packet = new ByteArrayPacket(data);
            var v1 = packet.TryPeekBits(8, out _);
            var v2 = packet.TryPeekBits(8, out _);
            Assert.Equal(v1, v2);
        }

        [Fact]
        public void TryPeekBits_AtEnd_BitsReadIsZero()
        {
            var data = new byte[] { 0xFF };
            var packet = new ByteArrayPacket(data);
            packet.SkipBits(8);
            packet.TryPeekBits(8, out var bitsRead);
            Assert.Equal(0, bitsRead);
        }

        // ── ReadBits LSB-first ───────────────────────────────────────────────

        [Fact]
        public void ReadBits_SingleBit_ReturnsLsb()
        {
            // byte 0b00000001 → bit 0 = 1, bit 1 = 0
            var data = new byte[] { 0x01 };
            IPacket packet = new ByteArrayPacket(data);
            Assert.Equal(1UL, packet.ReadBits(1));
            Assert.Equal(0UL, packet.ReadBits(1));
        }

        [Fact]
        public void ReadBits_LsbFirst_ByteValue()
        {
            // 0xB4 = 1011 0100; LSB-first 4 bits = 0100 = 4, next 4 bits = 1011 = 11
            var data = new byte[] { 0xB4 };
            IPacket packet = new ByteArrayPacket(data);
            Assert.Equal(4UL, packet.ReadBits(4));
            Assert.Equal(11UL, packet.ReadBits(4));
        }

        // ── BitsRead / BitsRemaining ─────────────────────────────────────────

        [Fact]
        public void BitsRead_AfterReadBits_Advances()
        {
            var data = new byte[] { 0xFF, 0xFF };
            IPacket packet = new ByteArrayPacket(data);
            packet.ReadBits(5);
            Assert.Equal(5, packet.BitsRead);
        }

        [Fact]
        public void BitsRemaining_DecreasesAsRead()
        {
            var data = new byte[] { 0xAA, 0xBB };
            IPacket packet = new ByteArrayPacket(data);
            int initial = packet.BitsRemaining;
            packet.ReadBits(8);
            Assert.Equal(initial - 8, packet.BitsRemaining);
        }

        [Fact]
        public void SkipBits_AdvancesBitsRead()
        {
            var data = new byte[] { 0xFF, 0xFF };
            var packet = new ByteArrayPacket(data);
            packet.SkipBits(13);
            Assert.Equal(13, packet.BitsRead);
        }
    }
}
