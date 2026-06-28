using System;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    public class Floor0Tests
    {
        static readonly Type _floor0Type =
            typeof(VorbisReader).Assembly.GetType("NVorbis.Floor0")!;

        static readonly MethodInfo _toBark =
            _floor0Type.GetMethod("toBARK", BindingFlags.NonPublic | BindingFlags.Static)!;

        static readonly MethodInfo _synthBark =
            _floor0Type.GetMethod("SynthesizeBarkCurve", BindingFlags.NonPublic | BindingFlags.Instance)!;

        static double ToBark(double lsp) =>
            (double)_toBark.Invoke(null, new object[] { lsp })!;

        static object MakeFloor0(int rate, int barkMapSize)
        {
            var instance = Activator.CreateInstance(_floor0Type, nonPublic: true)!;
            _floor0Type.GetField("_rate", BindingFlags.NonPublic | BindingFlags.Instance)!
                       .SetValue(instance, rate);
            _floor0Type.GetField("_bark_map_size", BindingFlags.NonPublic | BindingFlags.Instance)!
                       .SetValue(instance, barkMapSize);
            return instance;
        }

        static int[] SynthesizeBarkCurve(object instance, int n) =>
            (int[])_synthBark.Invoke(instance, new object[] { n })!;

        // ── toBARK precision ────────────────────────────────────────────────

        [Fact]
        public void ToBark_ReturnType_IsDouble()
        {
            Assert.Equal(typeof(double), _toBark.ReturnType);
        }

        [Fact]
        public void ToBark_Result_HasSubFloatPrecision()
        {
            // If toBARK still returned float the double value would equal its own
            // float cast. A true double return carries bits below float precision.
            double val = ToBark(22050.0);
            Assert.NotEqual((double)(float)val, val);
        }

        [Fact]
        public void ToBark_HalfNyquist_IsPositive()
        {
            Assert.True(ToBark(11025.0) > 0.0);
        }

        [Fact]
        public void ToBark_Zero_IsZero()
        {
            Assert.Equal(0.0, ToBark(0.0));
        }

        [Fact]
        public void ToBark_IsMonotonicallyIncreasing()
        {
            Assert.True(ToBark(11025.0) < ToBark(22050.0));
        }

        // ── SynthesizeBarkCurve ─────────────────────────────────────────────

        [Fact]
        public void SynthesizeBarkCurve_TerminatorIsMinusOne()
        {
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: 256);
            int n = 512;
            int[] map = SynthesizeBarkCurve(floor0, n);
            Assert.Equal(-1, map[n]);
        }

        [Fact]
        public void SynthesizeBarkCurve_BinsAreNonDecreasing()
        {
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: 256);
            int[] map = SynthesizeBarkCurve(floor0, 512);
            for (int i = 1; i < 511; i++)
            {
                Assert.True(map[i] >= map[i - 1],
                    $"map[{i}]={map[i]} < map[{i - 1}]={map[i - 1]}");
            }
        }

        [Fact]
        public void SynthesizeBarkCurve_AllBinsInRange()
        {
            int barkMapSize = 256;
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: barkMapSize);
            int[] map = SynthesizeBarkCurve(floor0, 512);
            for (int i = 0; i < 511; i++)
            {
                Assert.InRange(map[i], 0, barkMapSize - 1);
            }
        }

        [Fact]
        public void SynthesizeBarkCurve_FirstBinIsZero()
        {
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: 256);
            int[] map = SynthesizeBarkCurve(floor0, 512);
            Assert.Equal(0, map[0]);
        }

        [Fact]
        public void SynthesizeBarkCurve_LastDataBinIsMaxBark()
        {
            int barkMapSize = 256;
            var floor0 = MakeFloor0(rate: 44100, barkMapSize: barkMapSize);
            int n = 512;
            int[] map = SynthesizeBarkCurve(floor0, n);
            Assert.Equal(barkMapSize - 1, map[n - 2]);
        }
    }
}
