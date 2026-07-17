using System;
using System.IO;
using System.Threading;
using Xunit;

namespace NVorbis.Tests
{
    // Regression tests for issue #40 ("Dead loop in StreamDecoder.Read()").
    //
    // Repro pattern: seek to a position inside the final page, read a buffer larger than the
    // remaining samples (which drains the decoder to EOS), then seek back into that same tail
    // region and read again.  The second seek leaves the decode state with
    // _prevPacketEnd < _prevPacketStart (a negative "valid length").  Before the fix, Read()
    // only refilled when _prevPacketStart == _prevPacketEnd and only copied when
    // _prevPacketEnd - _prevPacketStart > 0, so this state satisfied neither branch and the
    // while-loop spun forever.  The fix refills on _prevPacketStart >= _prevPacketEnd so the
    // degenerate state terminates via the EOS path.
    //
    // Each repro runs under a watchdog so a regression manifests as a failed assertion rather
    // than a hung test run.
    public class Issue40RegressionTests
    {
        private const int WatchdogMs = 15000;

        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        // Runs body on a background thread; fails the test if it does not finish in time
        // (a dead loop would otherwise hang the whole run), and rethrows any real exception.
        private static void WithinWatchdog(Action body)
        {
            Exception captured = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { captured = ex; }
            })
            { IsBackground = true };

            t.Start();
            Assert.True(t.Join(WatchdogMs), "Read did not return within the watchdog window -- possible dead loop (issue #40)");
            if (captured != null) throw captured;
        }

        // Files confirmed to reproduce the dead loop with the seek-near-end + over-read pattern.
        [Theory]
        [InlineData("3test.ogg")]
        [InlineData("pop-2.ogg")]
        public void ReSeekNearEnd_OverRead_DoesNotHang(string file)
        {
            WithinWatchdog(() =>
            {
                using var reader = new VorbisReader(TestFile(file));
                long total = reader.TotalFrames;
                long target = Math.Max(0, total - 40); // lands inside the final page
                var buf1 = new float[reader.Channels * 4096]; // far larger than the samples that remain
                var buf2 = new float[reader.Channels * 4096];

                // first read drains the tail to end-of-stream
                reader.SeekTo(target, SeekOrigin.Begin);
                int first = reader.ReadSamples(buf1, 0, buf1.Length);

                // re-seek into the same tail region and read again -- this used to spin forever,
                // and after the loop guard alone it returned 0 (a stale _currentPosition drove the
                // EOS valid-length backoff negative).  Re-seeking to the same position must be
                // idempotent: the second read returns the same samples as the first.
                reader.SeekTo(target, SeekOrigin.Begin);
                int second = reader.ReadSamples(buf2, 0, buf2.Length);

                Assert.True(first > 0);
                Assert.Equal(first, second);
                Assert.Equal(buf1[..first], buf2[..second]);
            });
        }

        // Repeatedly re-seeking to the very last sample and over-reading must keep terminating.
        [Fact]
        public void RepeatedTailReSeek_TerminatesEachTime()
        {
            WithinWatchdog(() =>
            {
                using var reader = new VorbisReader(TestFile("3test.ogg"));
                long target = Math.Max(0, reader.TotalFrames - 1);
                var buf = new float[reader.Channels * 4096];

                int expected = -1;
                for (int i = 0; i < 5; i++)
                {
                    reader.SeekTo(target, SeekOrigin.Begin);
                    int n = reader.ReadSamples(buf, 0, buf.Length);
                    if (expected < 0) expected = n;
                    Assert.Equal(expected, n); // every re-seek to the same position yields the same read
                }
            });
        }

        // The file attached to issue #40 carries an encoder-produced granule-position defect, so a
        // near-end seek may surface an InvalidDataException -- but it must do so synchronously rather
        // than hang, and a full linear decode must still complete.
        [Fact]
        public void Issue40File_DecodeAndSeek_DoesNotHang()
        {
            WithinWatchdog(() =>
            {
                using var reader = new VorbisReader(TestFile("issue40test.ogg"));
                var buf = new float[reader.Channels * 4096];

                long total = 0;
                int count;
                while ((count = reader.ReadSamples(buf, 0, buf.Length)) > 0)
                {
                    total += count;
                }
                Assert.True(total > 0);
                Assert.True(reader.IsEndOfStream);

                long target = Math.Max(0, reader.TotalFrames - 40);
                try
                {
                    reader.SeekTo(target, SeekOrigin.Begin);
                    reader.SeekTo(target, SeekOrigin.Begin);
                    reader.ReadSamples(buf, 0, buf.Length);
                }
                catch (InvalidDataException)
                {
                    // acceptable: the corrupt granule position is reported, not silently looped
                }
                catch (InvalidOperationException)
                {
                    // acceptable: pre-roll packet could not be read at the requested seek target
                }
            });
        }
    }
}
