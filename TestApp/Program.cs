using NVorbis;
using System.Diagnostics;
using System.IO;

namespace TestApp
{
    static class Program
    {
        const string OGG_FILE = @"..\TestFiles\3test.ogg";
        //const string OGG_FILE = @"..\TestFiles\2test.ogg";

        static void Main()
        {
            var wavFileName = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetFileName(OGG_FILE), "wav"));

            using (var fs = File.OpenRead(OGG_FILE))
            //using (var fwdStream = new ForwardOnlyStream(fs))
            using (var vorbRead = new VorbisReader(fs, false))
            using (var waveWriter = new WaveWriter(wavFileName, vorbRead.SampleRate, vorbRead.Channels))
            {
                var sampleBuf = new float[vorbRead.SampleRate * vorbRead.Channels * 4];
                int cnt;
                while ((cnt = vorbRead.ReadSamples(sampleBuf, 0, sampleBuf.Length)) > 0)
                {
                    waveWriter.WriteSamples(sampleBuf, 0, cnt);
                }
            }
            Process.Start(wavFileName);
        }
    }
}
