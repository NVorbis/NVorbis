using System;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    public class ModeTests
    {
        static readonly Type _modeType = typeof(VorbisReader).Assembly.GetType("NVorbis.Mode")!;

        static readonly MethodInfo _calcWindow =
            _modeType.GetMethod("CalcWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        static readonly MethodInfo _calcOverlap =
            _modeType.GetMethod("CalcOverlap", BindingFlags.NonPublic | BindingFlags.Static)!;

        static float[] CalcWindow(int prev, int block, int next) =>
            (float[])_calcWindow.Invoke(null, new object[] { prev, block, next })!;

        static (int start, int valid, int total) CalcOverlap(int prev, int block, int next)
        {
            var info = _calcOverlap.Invoke(null, new object[] { prev, block, next })!;
            var t = info.GetType();
            int F(string n) => (int)t.GetField(n, BindingFlags.Public | BindingFlags.Instance)!.GetValue(info)!;
            return (F("PacketStartIndex"), F("PacketValidLength"), F("PacketTotalLength"));
        }

        // ── CalcWindow ───────────────────────────────────────────────────────

        [Fact]
        public void CalcWindow_LengthEqualsBlockSize()
        {
            Assert.Equal(256, CalcWindow(256, 256, 256).Length);
        }

        [Fact]
        public void CalcWindow_AllValuesInUnitRange()
        {
            foreach (var v in CalcWindow(256, 256, 256))
            {
                Assert.InRange(v, 0f, 1f);
            }
        }

        [Fact]
        public void CalcWindow_RisingHalf_IsMonotonic()
        {
            var w = CalcWindow(256, 256, 256);
            for (int i = 1; i < 128; i++)
            {
                Assert.True(w[i] >= w[i - 1], $"w[{i}]={w[i]} < w[{i - 1}]={w[i - 1]}");
            }
        }

        [Fact]
        public void CalcWindow_Endpoints_AreNearZeroAndOne()
        {
            var w = CalcWindow(256, 256, 256);
            Assert.True(w[0] < 0.05f, $"start {w[0]}");
            Assert.True(w[127] > 0.99f, $"peak {w[127]}");
        }

        [Fact]
        public void CalcWindow_SatisfiesPrincenBradleyIdentity()
        {
            // For the symmetric case, w[i]^2 + w[i+half]^2 == 1 (perfect reconstruction)
            var w = CalcWindow(256, 256, 256);
            for (int i = 0; i < 128; i++)
            {
                var sum = w[i] * w[i] + w[i + 128] * w[i + 128];
                Assert.Equal(1f, sum, 4); // 4 decimal places
            }
        }

        [Fact]
        public void CalcWindow_LeadingRegion_IsZeroForLongBlock()
        {
            // long block with short neighbors: samples before the ramp are zero
            var w = CalcWindow(256, 2048, 256);
            // leftbegin = 2048/4 - (256/2)/2 = 512 - 64 = 448
            Assert.Equal(0f, w[0]);
            Assert.Equal(0f, w[447]);
            Assert.True(w[448] > 0f);
        }

        // ── CalcOverlap ──────────────────────────────────────────────────────

        [Fact]
        public void CalcOverlap_Symmetric()
        {
            var (start, valid, total) = CalcOverlap(256, 256, 256);
            Assert.Equal(0, start);
            Assert.Equal(256, total);
            Assert.Equal(128, valid);
        }

        [Fact]
        public void CalcOverlap_Asymmetric()
        {
            var (start, valid, total) = CalcOverlap(128, 256, 512);
            // leftHalf=32, rightHalf=128; start = 64-32 = 32
            Assert.Equal(32, start);
            Assert.Equal(320, total);  // 256/4*3 + 128
            Assert.Equal(64, valid);   // 320 - 128*2
        }
    }
}
