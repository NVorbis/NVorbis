using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NVorbis.Tests
{
    public class MdctTests
    {
        // Smallest legal Vorbis block size; keeps test execution fast.
        private const int N = 64;

        private static float[] MakeInput(int n, float seed = 1f)
        {
            var buf = new float[n];
            for (int i = 0; i < n; i++)
                buf[i] = (float)Math.Sin(seed * (i + 1));
            return buf;
        }

        private static float[] RunReverse(int n, float seed = 1f)
        {
            var mdct = new Mdct();
            var buf = MakeInput(n, seed);
            mdct.Reverse(buf, n);
            return buf;
        }

        // Item 7: repeated calls must produce identical results (buffer reuse must not bleed state)
        [Fact]
        public void Reverse_RepeatedCalls_ProduceSameResult()
        {
            var mdct = new Mdct();

            var buf1 = MakeInput(N);
            var buf2 = MakeInput(N);

            mdct.Reverse(buf1, N);
            mdct.Reverse(buf2, N);

            Assert.Equal(buf1, buf2);
        }

        // Item 7: alternating between two block sizes must not corrupt results
        [Fact]
        public void Reverse_AlternatingBlockSizes_ProducesConsistentResults()
        {
            var mdct = new Mdct();
            const int N2 = 128;

            var small1 = MakeInput(N);
            var large1 = MakeInput(N2);
            mdct.Reverse(small1, N);
            mdct.Reverse(large1, N2);

            var small2 = MakeInput(N);
            var large2 = MakeInput(N2);
            mdct.Reverse(small2, N);
            mdct.Reverse(large2, N2);

            Assert.Equal(small1, small2);
            Assert.Equal(large1, large2);
        }

        // Item 8 + item 7: concurrent calls on the same Mdct instance must not corrupt results
        [Fact]
        public void Reverse_ConcurrentCalls_ProduceCorrectIndependentResults()
        {
            var mdct = new Mdct();

            // Compute reference output single-threaded.
            var reference = RunReverse(N);

            const int threadCount = 8;
            var results = new float[threadCount][];
            var errors = new Exception[threadCount];

            var barrier = new Barrier(threadCount);
            var threads = Enumerable.Range(0, threadCount).Select(i => new Thread(() =>
            {
                try
                {
                    var buf = MakeInput(N);
                    barrier.SignalAndWait(); // all threads start Reverse simultaneously
                    mdct.Reverse(buf, N);
                    results[i] = buf;
                }
                catch (Exception ex)
                {
                    errors[i] = ex;
                }
            })).ToArray();

            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            for (int i = 0; i < threadCount; i++)
            {
                Assert.Null(errors[i]);
                Assert.Equal(reference, results[i]);
            }
        }

        // Reference implementation: direct O(n²) inverse MDCT per Vorbis I spec §1.3.2
        // (same formula as stb_vorbis's inverse_mdct_slow debug reference).
        private static double[] NaiveImdct(float[] spectrum, int n)
        {
            var y = new double[n];
            for (int j = 0; j < n; j++)
            {
                double sum = 0;
                for (int k = 0; k < n / 2; k++)
                    sum += spectrum[k] * Math.Cos(Math.PI / (2.0 * n) * (2 * j + 1 + n / 2.0) * (2 * k + 1));
                y[j] = sum;
            }
            return y;
        }

        private static ulong Fnv1aHash(float[] data)
        {
            ulong h = 14695981039346656037UL;
            foreach (var f in data)
            {
                var bits = (uint)BitConverter.SingleToInt32Bits(f);
                for (int s = 0; s < 32; s += 8)
                {
                    h ^= (bits >> s) & 0xFF;
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        // Output must be the actual inverse MDCT defined by the Vorbis spec, not merely
        // self-consistent. Catches the (upstream stb_vorbis) small-block bug where the
        // fixed step-3 iteration 0/1 calls overlap the combined final three FFT stages
        // for n < 256, producing garbage for legal block sizes 64 and 128.
        [Theory]
        [InlineData(64, 1f)]
        [InlineData(64, 2.5f)]
        [InlineData(128, 1f)]
        [InlineData(128, 2.5f)]
        [InlineData(256, 1f)]
        [InlineData(512, 1f)]
        public void Reverse_MatchesNaiveSpecImdct(int n, float seed)
        {
            var buf = MakeInput(n, seed);
            var spectrum = new float[n / 2];
            Array.Copy(buf, spectrum, n / 2);

            new Mdct().Reverse(buf, n);
            var reference = NaiveImdct(spectrum, n);

            double maxRef = 0;
            for (int j = 0; j < n; j++)
                maxRef = Math.Max(maxRef, Math.Abs(reference[j]));

            for (int j = 0; j < n; j++)
                Assert.True(Math.Abs(buf[j] - reference[j]) <= 5e-5 * maxRef,
                    $"n={n} seed={seed} j={j}: got {buf[j]}, expected {reference[j]}");
        }

        // Bit-exact golden outputs for every legal Vorbis block size. Pins the exact
        // arithmetic so refactors (e.g. bounds-check-elimination) can prove they did
        // not change a single operation. Regenerate only for a deliberate change in
        // numeric behavior, and only after Reverse_MatchesNaiveSpecImdct passes.
        [Theory]
        [InlineData(64, 1f, 0x653757E457244495UL)]
        [InlineData(64, 2.5f, 0x2BDC91750253834DUL)]
        [InlineData(128, 1f, 0xC50C35CC3D91234DUL)]
        [InlineData(128, 2.5f, 0x3FEB85480BC9C349UL)]
        [InlineData(256, 1f, 0x0D66048BF0197E7DUL)]
        [InlineData(256, 2.5f, 0xA7C2DEB9AA81D799UL)]
        [InlineData(512, 1f, 0x610D7FA5D7541C5DUL)]
        [InlineData(512, 2.5f, 0x6EDE0495506E2E41UL)]
        [InlineData(1024, 1f, 0x53437BAC881CD6ADUL)]
        [InlineData(1024, 2.5f, 0x3FF46FFB2936781DUL)]
        [InlineData(2048, 1f, 0x5DE665695975AD29UL)]
        [InlineData(2048, 2.5f, 0xCDE9C2AFBBDC31ADUL)]
        [InlineData(4096, 1f, 0x7DE27D9924C8BD45UL)]
        [InlineData(4096, 2.5f, 0xB14DAA6B9C503369UL)]
        [InlineData(8192, 1f, 0x52373326A0A06E91UL)]
        [InlineData(8192, 2.5f, 0x98CE711F9CBABD09UL)]
        public void Reverse_GoldenOutput_BitExact(int n, float seed, ulong expectedHash)
        {
            var result = RunReverse(n, seed);
            Assert.Equal(expectedHash, Fnv1aHash(result));
        }

        // Item 8: Reverse called concurrently with a new sampleCount must not corrupt cache
        [Fact]
        public void Reverse_ConcurrentNewSampleCounts_CachePopulatesSafely()
        {
            // Two threads simultaneously hit Reverse with block sizes not yet in the cache.
            const int Na = 64;
            const int Nb = 128;

            var refA = RunReverse(Na);
            var refB = RunReverse(Nb);

            const int iterations = 20;
            for (int iter = 0; iter < iterations; iter++)
            {
                var mdct = new Mdct(); // fresh cache each iteration
                float[] resultA = null, resultB = null;
                Exception exA = null, exB = null;

                var barrier = new Barrier(2);
                var tA = new Thread(() =>
                {
                    try { var b = MakeInput(Na); barrier.SignalAndWait(); mdct.Reverse(b, Na); resultA = b; }
                    catch (Exception ex) { exA = ex; }
                });
                var tB = new Thread(() =>
                {
                    try { var b = MakeInput(Nb); barrier.SignalAndWait(); mdct.Reverse(b, Nb); resultB = b; }
                    catch (Exception ex) { exB = ex; }
                });

                tA.Start(); tB.Start();
                tA.Join(); tB.Join();

                Assert.Null(exA);
                Assert.Null(exB);
                Assert.Equal(refA, resultA);
                Assert.Equal(refB, resultB);
            }
        }
    }
}
