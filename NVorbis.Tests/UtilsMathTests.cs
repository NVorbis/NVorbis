using Xunit;

namespace NVorbis.Tests
{
    public class UtilsMathTests
    {
        // ── ilog: number of significant bits ─────────────────────────────────

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 2)]
        [InlineData(7, 3)]
        [InlineData(8, 4)]
        [InlineData(255, 8)]
        [InlineData(256, 9)]
        public void Ilog_ReturnsBitCount(int value, int expected)
        {
            Assert.Equal(expected, Utils.ilog(value));
        }

        [Fact]
        public void Ilog_NegativeOrZero_StopsAtZero()
        {
            // sign bit set: loop guard is x > 0, so negatives return 0
            Assert.Equal(0, Utils.ilog(-1));
        }

        // ── BitReverse ───────────────────────────────────────────────────────

        [Fact]
        public void BitReverse_Full32_SingleBit()
        {
            Assert.Equal(0x80000000u, Utils.BitReverse(1u));
        }

        [Theory]
        [InlineData(1u, 1, 1u)]    // 0b1 reversed in 1 bit = 0b1
        [InlineData(1u, 4, 8u)]    // 0b0001 reversed in 4 bits = 0b1000
        [InlineData(0b1011u, 4, 0b1101u)]
        [InlineData(0u, 8, 0u)]
        public void BitReverse_LimitedBits(uint value, int bits, uint expected)
        {
            Assert.Equal(expected, Utils.BitReverse(value, bits));
        }

        // ── ClipValue ────────────────────────────────────────────────────────

        const float Threshold = 0.99999994f;

        [Fact]
        public void ClipValue_WithinRange_ReturnsValueUnclipped()
        {
            var clipped = false;
            Assert.Equal(0.5f, Utils.ClipValue(0.5f, ref clipped));
            Assert.False(clipped);
        }

        [Fact]
        public void ClipValue_AboveThreshold_ClampsAndFlags()
        {
            var clipped = false;
            Assert.Equal(Threshold, Utils.ClipValue(1.5f, ref clipped));
            Assert.True(clipped);
        }

        [Fact]
        public void ClipValue_BelowNegativeThreshold_ClampsAndFlags()
        {
            var clipped = false;
            Assert.Equal(-Threshold, Utils.ClipValue(-1.5f, ref clipped));
            Assert.True(clipped);
        }

        [Fact]
        public void ClipValue_ExactlyAtThreshold_DoesNotClip()
        {
            // comparison is strictly greater-than, so the threshold itself passes through
            var clipped = false;
            Assert.Equal(Threshold, Utils.ClipValue(Threshold, ref clipped));
            Assert.False(clipped);
        }

        [Fact]
        public void ClipValue_DoesNotResetClippedFlag()
        {
            // flag is sticky across calls (drives HasClipped)
            var clipped = true;
            Utils.ClipValue(0.1f, ref clipped);
            Assert.True(clipped);
        }
    }
}
