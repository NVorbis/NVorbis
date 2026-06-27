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
