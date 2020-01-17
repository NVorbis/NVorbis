using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;

namespace TestApp
{
    static class Program
    {
        const string OGG_FILE = @"..\TestFiles\3test.ogg";
        //const string OGG_FILE = @"..\TestFiles\2test.ogg";

        static void Main()
        {
            using (var fs = File.OpenRead(OGG_FILE))
            //using (var fwdStream = new ForwardOnlyStream(fs))
            using (var waveStream = new VorbisWaveStream(fs))
            using (var waveOut = new WaveOutEvent())
            {
                var wait = new System.Threading.ManualResetEventSlim(false);
                waveOut.PlaybackStopped += (s, e) => wait.Set();
                
                waveOut.Init(waveStream);
                waveOut.Play();

                wait.Wait();
            }
        }
    }
}
