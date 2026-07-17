using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    // Regression tests for issue #39: "GranulePos mismatch: Page 3, expected 17088, calculated
    // 16601". issue40test.ogg has a genuine ~340-sample hole in its granule timeline (a real gap,
    // not the issue #28 libvorbis mis-count) between samples 1265020 and 1265360, most likely from
    // a spliced/edited source. FindPacket used to treat any unexplained forward granule
    // discrepancy on a page after the first as corruption and throw. It now treats it as a hole:
    // the target page's own granule positions are internally consistent, so a seek that lands in
    // the gap resolves to the first sample that actually exists, rather than throwing.
    public class Issue39RegressionTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        private const string Issue39File = "issue40test.ogg";

        // The exact hole measured in this fixture: (1265020, 1265360].
        private const long HoleTarget = 1265100;
        private const long HoleEnd = 1265360;

        [Fact]
        public void Issue39_SeekIntoHole_SnapsForwardWithoutThrowing()
        {
            using var reader = new VorbisReader(TestFile(Issue39File));

            reader.SeekTo(HoleTarget, SeekOrigin.Begin);

            Assert.Equal(HoleEnd, reader.FramePosition);

            var buf = new float[reader.Channels * 4096];
            Assert.True(reader.ReadSamples(buf, 0, buf.Length) > 0);
        }

        [Fact]
        public void Issue39_RandomSeekFuzz_DoesNotThrow()
        {
            var rand = new Random(39 ^ Issue39File.GetHashCode());
            using var reader = new VorbisReader(TestFile(Issue39File));
            long total = reader.TotalFrames;
            var buf = new float[reader.Channels * 4096];

            for (int i = 0; i < 2000; i++)
            {
                long target = (long)(rand.NextDouble() * total);
                reader.SeekTo(target, SeekOrigin.Begin);
                Assert.True(reader.FramePosition >= target, $"pos {reader.FramePosition} < target {target}");
                reader.ReadSamples(buf, 0, buf.Length);
            }
        }
    }
}
