using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    // Regression tests for seeking INTO the first audio page when that page is large.
    //
    // The Vorbis I spec only requires audio to start on a fresh page; it says nothing about
    // how much audio that page may hold.  Muxers that page by DURATION rather than by size
    // (ffmpeg: -page_duration, default 1 s) put many packets on the first audio page.
    //
    // largefirstpage.ogg: 20 KB, mono 8 kHz PINK NOISE (seeded; a pure tone's periodicity would let a
    // time-shifted seek look correct), libvorbis q-1, ffmpeg -page_duration 3000000:
    //   page 2 (first data page)  granule 23808   94 packets   <- ~3 s of audio, >10x block_size
    //   page 3                    granule 47872   94 packets
    //   page 4 (EOS)              granule 48000    1 packet
    //
    // PacketProvider.SeekTo short-circuits targets on the first data page because there is
    // no preceding audio page to validate granule positions against.  An earlier version of
    // that shortcut snapped EVERY such target to the stream start, assuming the page held
    // only ~block_size samples; StreamDecoder then clamped the oversized roll-forward and
    // delivered audio from the wrong position while reporting the requested SamplePosition.
    // The shortcut must still locate the packet containing the target within the page.
    public class LargeFirstPageSeekTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        private const string FixtureName = "largefirstpage.ogg";
        private const long FirstDataPageEndGranule = 23808;
        private const int ProbeLength = 256;

        [Fact]
        public void Fixture_FirstDataPage_IsLarge()
        {
            // Guards the fixture, not the library: the point of the file is a seek target far
            // beyond block_size that still lands on the first data page.
            var pages = OggPageTable.Read(TestFile(FixtureName));
            Assert.True(pages.FirstDataPageEndGranule >= FirstDataPageEndGranule, $"first data page ends at {pages.FirstDataPageEndGranule}");
            Assert.True(pages.DataPageCount >= 2, "need a page after the first data page as a control");
        }

        [Theory]
        [InlineData(5)]
        [InlineData(640)]
        [InlineData(768)]
        [InlineData(1024)]
        [InlineData(1025)]
        [InlineData(2048)]
        [InlineData(10000)]
        [InlineData(FirstDataPageEndGranule - 300)]   // last packets of the first data page
        [InlineData(FirstDataPageEndGranule - 1)]
        [InlineData(FirstDataPageEndGranule)]         // first sample of the second data page
        [InlineData(FirstDataPageEndGranule + 1000)]  // the normal FindPacket path, as a control
        public void SeekIntoFirstDataPage_FreshOpen_DeliversSamplesFromTheRequestedPosition(long target)
        {
            var linear = SeekFidelity.LinearDecode(TestFile(FixtureName), out var channels);

            using var reader = new VorbisReader(TestFile(FixtureName));
            reader.ClipSamples = false;
            SeekFidelity.SeekAndAssertProbeMatches(reader, linear, channels, target, ProbeLength);
        }

        [Fact]
        public void SeekBackIntoFirstDataPage_AfterReadingFurtherOn_DeliversSamplesFromTheRequestedPosition()
        {
            var linear = SeekFidelity.LinearDecode(TestFile(FixtureName), out var channels);

            using var reader = new VorbisReader(TestFile(FixtureName));
            reader.ClipSamples = false;
            SeekFidelity.WithTimeout(() => { reader.SeekTo(FirstDataPageEndGranule + 5000, SeekOrigin.Begin); return 0; });
            var scratch = new float[4096 * channels];
            SeekFidelity.WithTimeout(() => reader.ReadSamples(scratch, 0, scratch.Length));

            SeekFidelity.SeekAndAssertProbeMatches(reader, linear, channels, 1024, ProbeLength);
        }
    }
}