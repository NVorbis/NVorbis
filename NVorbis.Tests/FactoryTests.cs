using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class FactoryTests
    {
        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;
            public ByteArrayPacket(byte[] data) => _data = data;
            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;
        }

        // 16-bit type field, LSB-first, matching Vorbis packet bit order.
        private static ByteArrayPacket TypePacket(int type) =>
            new(new byte[] { (byte)(type & 0xFF), (byte)((type >> 8) & 0xFF) });

        private readonly Factory _factory = new();

        [Fact]
        public void CreateHuffman_ReturnsHuffman()
        {
            Assert.IsType<Huffman>(_factory.CreateHuffman());
        }

        [Fact]
        public void CreateMdct_ReturnsMdct()
        {
            Assert.IsType<Mdct>(_factory.CreateMdct());
        }

        [Fact]
        public void CreateCodebook_ReturnsCodebook()
        {
            Assert.IsType<Codebook>(_factory.CreateCodebook());
        }

        [Fact]
        public void CreateMode_ReturnsMode()
        {
            Assert.IsType<Mode>(_factory.CreateMode());
        }

        [Fact]
        public void CreateFloor_Type0_ReturnsFloor0()
        {
            Assert.IsType<Floor0>(_factory.CreateFloor(TypePacket(0)));
        }

        [Fact]
        public void CreateFloor_Type1_ReturnsFloor1()
        {
            Assert.IsType<Floor1>(_factory.CreateFloor(TypePacket(1)));
        }

        [Fact]
        public void CreateFloor_UnknownType_ThrowsInvalidDataException()
        {
            var ex = Assert.Throws<InvalidDataException>(() => _factory.CreateFloor(TypePacket(2)));
            Assert.Contains("floor", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CreateMapping_Type0_ReturnsMapping()
        {
            Assert.IsType<Mapping>(_factory.CreateMapping(TypePacket(0)));
        }

        [Fact]
        public void CreateMapping_NonZeroType_ThrowsInvalidDataException()
        {
            var ex = Assert.Throws<InvalidDataException>(() => _factory.CreateMapping(TypePacket(1)));
            Assert.Contains("mapping", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CreateResidue_Type0_ReturnsResidue0()
        {
            Assert.IsType<Residue0>(_factory.CreateResidue(TypePacket(0)));
        }

        [Fact]
        public void CreateResidue_Type1_ReturnsResidue1()
        {
            Assert.IsType<Residue1>(_factory.CreateResidue(TypePacket(1)));
        }

        [Fact]
        public void CreateResidue_Type2_ReturnsResidue2()
        {
            Assert.IsType<Residue2>(_factory.CreateResidue(TypePacket(2)));
        }

        [Fact]
        public void CreateResidue_UnknownType_ThrowsInvalidDataException()
        {
            var ex = Assert.Throws<InvalidDataException>(() => _factory.CreateResidue(TypePacket(3)));
            Assert.Contains("residue", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
