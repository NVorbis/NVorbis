using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using NVorbis.Ogg;
using System;
using System.Collections.Generic;
using Xunit;

namespace NVorbis.Tests
{
    public class PacketTests
    {
        private sealed class StubPacketReader : IPacketReader
        {
            private readonly Dictionary<int, byte[]> _pages;
            internal StubPacketReader(Dictionary<int, byte[]> pages) => _pages = pages;
            public Memory<byte> GetPacketData(int key) => _pages[key];
            public void InvalidatePacketCache(IPacket packet) { }
        }

        private static ulong ReadByte(IPacket p) => p.ReadBits(8);

        // ── single-page ──────────────────────────────────────────────────────

        [Fact]
        public void SinglePage_BitsRemaining_MatchesDataLength()
        {
            var data = new byte[] { 1, 2, 3, 4 };
            var reader = new StubPacketReader(new Dictionary<int, byte[]> { [0] = data });
            IPacket packet = new Packet(0, reader, data);

            Assert.Equal(data.Length * 8, packet.BitsRemaining);
        }

        [Fact]
        public void SinglePage_ReadBits_ReturnsCorrectSequence()
        {
            var data = new byte[] { 0xAB, 0xCD, 0xEF };
            var reader = new StubPacketReader(new Dictionary<int, byte[]> { [0] = data });
            IPacket packet = new Packet(0, reader, data);

            Assert.Equal(0xABu, ReadByte(packet));
            Assert.Equal(0xCDu, ReadByte(packet));
            Assert.Equal(0xEFu, ReadByte(packet));
            Assert.Equal(0, packet.BitsRemaining);
        }

        [Fact]
        public void SinglePage_ReadBitsAtEnd_ReturnsZeroCount()
        {
            var data = new byte[] { 0xFF };
            var reader = new StubPacketReader(new Dictionary<int, byte[]> { [0] = data });
            var packet = new Packet(0, reader, data);

            ReadByte(packet); // exhaust
            var val = packet.TryPeekBits(8, out var bitsRead);
            Assert.Equal(0, bitsRead);
        }

        [Fact]
        public void SinglePage_Reset_RestartsReadPosition()
        {
            var data = new byte[] { 0x42, 0x99 };
            var reader = new StubPacketReader(new Dictionary<int, byte[]> { [0] = data });
            IPacket packet = new Packet(0, reader, data);

            ReadByte(packet); // consume first byte
            packet.Reset();

            Assert.Equal(0x42u, ReadByte(packet));
        }

        // ── multi-page ───────────────────────────────────────────────────────

        [Fact]
        public void MultiPage_ReadBits_SpansPageBoundary()
        {
            const int key0 = (0 << 8) | 0;
            const int key1 = 1 << 8;
            var part0 = new byte[] { 0xAA, 0xBB };
            var part1 = new byte[] { 0xCC, 0xDD };
            var reader = new StubPacketReader(new Dictionary<int, byte[]>
            {
                [key0] = part0,
                [key1] = part1,
            });
            IPacket packet = new Packet(key0, new[] { key1 }, reader, part0);

            Assert.Equal(0xAAu, ReadByte(packet));
            Assert.Equal(0xBBu, ReadByte(packet));
            Assert.Equal(0xCCu, ReadByte(packet));
            Assert.Equal(0xDDu, ReadByte(packet));
            Assert.Equal(0, packet.BitsRemaining);
        }

        [Fact]
        public void MultiPage_ThreePages_ReadsBeyondSecondPageBoundary()
        {
            const int key0 = (0 << 8) | 0;
            const int key1 = 1 << 8;
            const int key2 = 2 << 8;
            var part0 = new byte[] { 0x01 };
            var part1 = new byte[] { 0x02 };
            var part2 = new byte[] { 0x03 };
            var reader = new StubPacketReader(new Dictionary<int, byte[]>
            {
                [key0] = part0,
                [key1] = part1,
                [key2] = part2,
            });
            IPacket packet = new Packet(key0, new[] { key1, key2 }, reader, part0);

            Assert.Equal(0x01u, ReadByte(packet));
            Assert.Equal(0x02u, ReadByte(packet));
            Assert.Equal(0x03u, ReadByte(packet));
            Assert.Equal(0, packet.BitsRemaining);
        }

        [Fact]
        public void MultiPage_Reset_RestartsReadPosition()
        {
            const int key0 = (0 << 8) | 0;
            const int key1 = 1 << 8;
            var part0 = new byte[] { 0x11, 0x22 };
            var part1 = new byte[] { 0x33, 0x44 };
            var reader = new StubPacketReader(new Dictionary<int, byte[]>
            {
                [key0] = part0,
                [key1] = part1,
            });
            IPacket packet = new Packet(key0, new[] { key1 }, reader, part0);

            ReadByte(packet);
            ReadByte(packet);
            ReadByte(packet);
            ReadByte(packet);
            packet.Reset();

            Assert.Equal(0x11u, ReadByte(packet));
        }
    }
}
