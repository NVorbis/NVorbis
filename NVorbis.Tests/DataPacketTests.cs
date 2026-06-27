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
    }
}
