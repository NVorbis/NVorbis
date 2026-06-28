using NVorbis;
using NVorbis.Contracts;
using System;
using System.Reflection;
using Xunit;

namespace NVorbis.Tests
{
    // Exercises the square-polar inverse-coupling math embedded in Mapping.DecodePacket.
    // The mapping is wired up by setting its private fields directly (bypassing the bit-packed
    // Init), and floor/residue/mdct are stubbed so the buffer after DecodePacket holds exactly
    // the post-coupling values.
    public class MappingCouplingTests
    {
        private class StubFloorData : IFloorData
        {
            private readonly bool _execute;
            public StubFloorData(bool execute) => _execute = execute;
            public bool ExecuteChannel => _execute;
            public bool ForceEnergy { get; set; }
            public bool ForceNoEnergy { get; set; }
        }

        private class StubFloor : IFloor
        {
            private readonly bool _execute;
            public StubFloor(bool execute) => _execute = execute;
            public void Init(IPacket packet, int channels, int block0Size, int block1Size, ICodebook[] codebooks) { }
            public IFloorData Unpack(IPacket packet, int blockSize, int channel) => new StubFloorData(_execute);
            public void Apply(IFloorData floorData, int blockSize, float[] residue) { }
        }

        // writes preset magnitude data into buffer[0] and angle data into buffer[1]
        private class StubResidue : IResidue
        {
            private readonly float[] _mag, _ang;
            public StubResidue(float[] mag, float[] ang) { _mag = mag; _ang = ang; }
            public void Init(IPacket packet, int channels, ICodebook[] codebooks) { }
            public void Decode(IPacket packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer)
            {
                Array.Copy(_mag, buffer[0], _mag.Length);
                Array.Copy(_ang, buffer[1], _ang.Length);
            }
        }

        private class StubMdct : IMdct
        {
            public void Reverse(float[] samples, int sampleCount) { }
        }

        private static void Set(Mapping m, string field, object value) =>
            typeof(Mapping).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(m, value);

        // 2-channel mapping, single coupling step (magnitude=ch0, angle=ch1), one submap
        private static Mapping BuildMapping(float[] mag, float[] ang, bool execute)
        {
            var m = new Mapping();
            var floor = new StubFloor(execute);
            var residue = new StubResidue(mag, ang);

            Set(m, "_mdct", new StubMdct());
            Set(m, "_couplingMagnitude", new[] { 0 });
            Set(m, "_couplingAngle", new[] { 1 });
            Set(m, "_channelFloor", new IFloor[] { floor, floor });
            Set(m, "_channelResidue", new IResidue[] { residue, residue });
            Set(m, "_submapFloor", new IFloor[] { floor });
            Set(m, "_submapResidue", new IResidue[] { residue });
            return m;
        }

        private static (float[] mag, float[] ang) RunCoupling(float[] mag, float[] ang, bool execute = true)
        {
            const int blockSize = 8; // half = 4 coupled samples
            var m = BuildMapping(PadTo(mag, blockSize), PadTo(ang, blockSize), execute);
            var buffer = new[] { new float[blockSize], new float[blockSize] };
            m.DecodePacket(null, blockSize, 2, buffer);
            return (Take(buffer[0], mag.Length), Take(buffer[1], ang.Length));
        }

        private static float[] PadTo(float[] src, int len) { var a = new float[len]; Array.Copy(src, a, src.Length); return a; }
        private static float[] Take(float[] src, int len) { var a = new float[len]; Array.Copy(src, a, len); return a; }

        [Fact]
        public void InverseCoupling_AllFourQuadrants()
        {
            // sample 0: M>0, A>0  → M'=M,       A'=M-A
            // sample 1: M>0, A<=0 → A'=M,       M'=M+A
            // sample 2: M<=0,A>0  → M'=M,       A'=M+A
            // sample 3: M<=0,A<=0 → A'=M,       M'=M-A
            var mag = new float[] { 5, 5, -5, -5 };
            var ang = new float[] { 3, -3, 3, -3 };

            var (m, a) = RunCoupling(mag, ang);

            Assert.Equal(new float[] { 5, 2, -5, -2 }, m);
            Assert.Equal(new float[] { 2, 5, -2, -5 }, a);
        }

        [Fact]
        public void InverseCoupling_PositiveMagnitudePositiveAngle()
        {
            var (m, a) = RunCoupling(new float[] { 8 }, new float[] { 3 });
            Assert.Equal(8f, m[0]);   // M' = M
            Assert.Equal(5f, a[0]);   // A' = M - A
        }

        [Fact]
        public void InverseCoupling_PositiveMagnitudeNegativeAngle()
        {
            var (m, a) = RunCoupling(new float[] { 8 }, new float[] { -3 });
            Assert.Equal(5f, m[0]);   // M' = M + A
            Assert.Equal(8f, a[0]);   // A' = M
        }

        [Fact]
        public void InverseCoupling_SkippedWhenChannelsNotExecuted()
        {
            // both floor channels report no energy → coupling loop is skipped, raw values remain
            var mag = new float[] { 5, 5 };
            var ang = new float[] { 3, -3 };
            var (m, a) = RunCoupling(mag, ang, execute: false);
            Assert.Equal(mag, m);
            Assert.Equal(ang, a);
        }
    }
}
