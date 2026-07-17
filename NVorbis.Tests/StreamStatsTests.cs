using System;
using System.IO;
using System.Threading;
using Xunit;

namespace NVorbis.Tests
{
    public class StreamStatsTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        [Fact]
        public void StreamStats_AfterReading_PacketCountPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.PacketCount > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_AudioBitsPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.AudioBits > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_EffectiveBitRatePositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.EffectiveBitRate > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_InstantBitRatePositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.InstantBitRate > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_ContainerBitsPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.ContainerBits > 0);
        }

        [Fact]
        public void StreamStats_AfterReading_OverheadBitsPositive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.StreamStats.OverheadBits > 0);
        }

        [Fact]
        public void StreamStats_ResetStats_ResetsPacketCount()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            reader.StreamStats.ResetStats();
            Assert.Equal(0, reader.StreamStats.PacketCount);
        }

        [Fact]
        public void StreamStats_ResetStats_ResetsAudioBits()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            reader.StreamStats.ResetStats();
            Assert.Equal(0L, reader.StreamStats.AudioBits);
        }

        [Fact]
        public void ContainerOverheadBits_AfterReading_Positive()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            Assert.True(reader.ContainerOverheadBits > 0);
        }

        [Fact]
        public void StreamStats_MoreReads_PacketCountIncreases()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            int after1 = reader.StreamStats.PacketCount;
            reader.ReadSamples(buf, 0, buf.Length);
            int after2 = reader.StreamStats.PacketCount;
            Assert.True(after2 > after1);
        }

        // ── InstantBitRate rolling-window and packed-slot correctness ─────────

        [Fact]
        public void InstantBitRate_InitialState_IsZero()
        {
            var stats = new StreamStats();
            Assert.Equal(0, stats.InstantBitRate);
        }

        [Fact]
        public void InstantBitRate_AfterReset_IsZero()
        {
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            var buf = new float[reader.SampleRate * reader.Channels];
            reader.ReadSamples(buf, 0, buf.Length);
            reader.StreamStats.ResetStats();
            Assert.Equal(0, reader.StreamStats.InstantBitRate);
        }

        [Fact]
        public void InstantBitRate_RollingWindow_SlotIsOverwrittenByThirdPacket()
        {
            // Packets: slot0←p1, slot1←p2, slot0←p3 (p1 evicted).
            // After p3 the window contains only p2+p3 bits/samples.
            var stats = new StreamStats();
            stats.SetSampleRate(44100);

            stats.AddPacket(samples: 1000, bits: 8000, waste: 0, container: 0);  // p1 → slot 0
            stats.AddPacket(samples:  500, bits: 2000, waste: 0, container: 0);  // p2 → slot 1
            int rateAfterTwo = stats.InstantBitRate;

            stats.AddPacket(samples:  500, bits: 2000, waste: 0, container: 0);  // p3 → slot 0 (overwrites p1)
            int rateAfterThree = stats.InstantBitRate;

            // Both windows must be positive.
            Assert.True(rateAfterTwo > 0);
            Assert.True(rateAfterThree > 0);
            // p1+p2 window: 10000 bits / 1500 samples.
            // p2+p3 window: 4000 bits / 1000 samples — p1's high-bit-rate entry was evicted.
            // Rate drops when the expensive packet leaves the window.
            Assert.True(rateAfterThree < rateAfterTwo);
        }

        [Fact]
        public void InstantBitRate_PackedSlot_ConcurrentRead_NeverNegative()
        {
            // Stress test: a background thread reads InstantBitRate while the decode
            // thread drives AddPacket.  The packed-long design means each slot is
            // always a consistent (bits, samples) pair, so InstantBitRate must
            // never return a negative value regardless of scheduling.
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            int badReads = 0;
            var cts = new CancellationTokenSource();

            var readerThread = new Thread(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (reader.StreamStats.InstantBitRate < 0)
                        Interlocked.Increment(ref badReads);
                }
            });
            readerThread.IsBackground = true;
            readerThread.Start();

            var buf = new float[reader.SampleRate * reader.Channels];
            while (reader.ReadSamples(buf, 0, buf.Length) > 0) { }

            cts.Cancel();
            readerThread.Join(TimeSpan.FromSeconds(2));

            Assert.Equal(0, badReads);
        }

        // ── Residue decode correctness after ArrayPool change ─────────────────

        [Fact]
        public void Residue_ArrayPool_FullDecode_SampleCountMatchesTotalFrames()
        {
            // Draining the file exercises every Residue0.WriteVectors call.
            // If the ArrayPool rent/return is incorrect the decoded sample count
            // will not match TotalFrames (corruption manifests as premature EOS
            // or a mismatch in the running total).
            using var reader = new VorbisReader(TestFile("3test.ogg"));
            long expected = reader.TotalFrames;
            var buf = new float[4096 * reader.Channels];
            long total = 0;
            int n;
            while ((n = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                total += n;
            Assert.Equal(expected * reader.Channels, total);
        }
    }
}
