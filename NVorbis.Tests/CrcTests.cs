using NVorbis.Ogg;
using Xunit;

namespace NVorbis.Tests
{
    public class CrcTests
    {
        // Independent bit-by-bit reference for the Ogg CRC (poly 0x04c11db7, MSB-first,
        // init 0, no input/output reflection, no final xor). Proves the table-driven
        // Crc class against a simple implementation.
        private static uint RefCrc(byte[] data)
        {
            uint crc = 0;
            foreach (var b in data)
            {
                crc ^= (uint)b << 24;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04c11db7u : crc << 1;
                }
            }
            return crc;
        }

        [Fact]
        public void Test_EmptyInput_IsZero()
        {
            var crc = new Crc();
            Assert.True(crc.Test(0u));
        }

        [Theory]
        [InlineData(new byte[] { 0x00 })]
        [InlineData(new byte[] { 0xFF })]
        [InlineData(new byte[] { 0x4F, 0x67, 0x67, 0x53 })] // "OggS"
        [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 })]
        public void Test_MatchesReferenceImplementation(byte[] data)
        {
            var crc = new Crc();
            foreach (var b in data)
            {
                crc.Update(b);
            }
            Assert.True(crc.Test(RefCrc(data)));
        }

        [Fact]
        public void Test_WrongCrc_Fails()
        {
            var crc = new Crc();
            crc.Update(0x42);
            Assert.False(crc.Test(0u));
        }

        [Fact]
        public void Reset_ClearsAccumulator()
        {
            var crc = new Crc();
            crc.Update(0xAB);
            crc.Update(0xCD);
            crc.Reset();
            Assert.True(crc.Test(0u));
        }

        [Fact]
        public void Update_IsOrderSensitive()
        {
            var a = new Crc();
            a.Update(0x01);
            a.Update(0x02);

            var b = new Crc();
            b.Update(0x02);
            b.Update(0x01);

            // different byte order must yield a different CRC
            Assert.False(b.Test(RefCrc(new byte[] { 0x01, 0x02 })));
            Assert.True(a.Test(RefCrc(new byte[] { 0x01, 0x02 })));
        }
    }
}
