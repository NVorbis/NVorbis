using Xunit;

namespace NVorbis.Tests
{
    public class UtilsTests
    {
        // Vorbis float32 layout: bit 31 = mantissa sign, bits 30:21 = biased exponent (bias 788), bits 20:0 = mantissa magnitude.
        // result = signed_mantissa * 2^(biased_exponent - 788)

        private static float Convert(uint bits) => Utils.ConvertFromVorbisFloat32(bits);

        [Fact]
        public void ConvertFromVorbisFloat32_Zero_ReturnsZero()
        {
            Assert.Equal(0f, Convert(0u));
        }

        [Fact]
        public void ConvertFromVorbisFloat32_One_ReturnsOne()
        {
            // mantissa=1, exponent field=788 → exponent=0 → 1 * 2^0 = 1.0
            Assert.Equal(1f, Convert(0x62800001u));
        }

        [Fact]
        public void ConvertFromVorbisFloat32_Two_ReturnsTwo()
        {
            // mantissa=1, exponent field=789 → exponent=1 → 1 * 2^1 = 2.0
            Assert.Equal(2f, Convert(0x62A00001u));
        }

        [Fact]
        public void ConvertFromVorbisFloat32_NegativeOne_ReturnsNegativeOne()
        {
            // sign=1, mantissa_bits=1, exponent field=788 → -1 * 2^0 = -1.0
            Assert.Equal(-1f, Convert(0xE2800001u));
        }

        [Fact]
        public void ConvertFromVorbisFloat32_NegativeTwo_ReturnsNegativeTwo()
        {
            // sign=1, mantissa_bits=2, exponent field=788 → -2 * 2^0 = -2.0
            Assert.Equal(-2f, Convert(0xE2800002u));
        }

        [Fact]
        public void ConvertFromVorbisFloat32_Half_ReturnsHalf()
        {
            // mantissa=1, exponent field=787 → exponent=-1 → 1 * 2^-1 = 0.5
            Assert.Equal(0.5f, Convert(0x62600001u));
        }
    }
}
