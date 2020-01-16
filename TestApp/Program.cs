//#define SAVESTREAM
#define WRAPSTREAM

using NAudio.Wave;
using NVorbis;
using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.IO;

namespace TestApp
{
    static class Program
    {
        //const string OGG_URI = @"http://play.global.audio:80/nrj_low.ogg";          // Radio NRJ (Bulgaria)
        //const string OGG_URI = @"http://revolutionradio.ru/live.ogg";               // Revolution Radio (Russia) -- down?
        //const string OGG_URI = @"http://play.global.audio:80/veronika.ogg";         // Radio Veronika (Bulgaria)
        const string OGG_URI = @"http://stream2.dancewave.online:8080/dance.ogg";   // Dance Wave! (online-only)

        const string OGG_FILE = "3test.ogg";

        const int FadeStepDuration = 14;
        const float FadeStepMultiplier = .9f;
        const float FadeMinimum = .01f;


        static void Main()
        {
            //Play(OGG_URI);
            Play(OGG_FILE);
        }

        private static void Play(string uriString)
        {
            var uri = new Uri(uriString);
            if (uri.Scheme == Uri.UriSchemeFile)
            {
                using (var stream = File.OpenRead(uriString))
                {
#if WRAPSTREAM
                    using (var fwdOnlyStream = new ForwardOnlyStream(stream))
                    {
                        PlayStream(fwdOnlyStream);
                    }
#else
                    PlayStream(stream);
#endif
                }
            }
            else
            {
                var req = System.Net.WebRequest.Create(uri);
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        Console.WriteLine(resp.Headers["icy-name"]);
                        Console.WriteLine(resp.Headers["icy-description"]);

                        using (var stream = resp.GetResponseStream())
                        {
#if SAVESTREAM
                            using (var wrapStream = new Nerdbank.Streams.MonitoringStream(stream))
                            using (var outStream = File.Create(@"E:\TEMP\raw.ogg"))
                            {
                                wrapStream.DidRead += (s, e) =>
                                {
                                    outStream.Write(e.Array, e.Offset, e.Count);
                                };

                                PlayStream(wrapStream);
                            }
#else
                            PlayStream(stream);
#endif
                        }
                    }
                }
            }
        }

        private static void PlayStream(Stream stream)
        {
            using (var vorbisReader = new VorbisWaveStream(stream))
            using (var bufProvider = new BufferingSampleProvider(vorbisReader) { MinimumDuration = TimeSpan.FromMilliseconds(100) })
            {
                var volProvider = new NAudio.Wave.SampleProviders.VolumeSampleProvider(bufProvider)
                {
                    Volume = 1f
                };

                Console.CursorVisible = false;
                Console.TreatControlCAsInput = true;

                var fmtChgWait = new System.Threading.ManualResetEventSlim(false);
                void HandleFormatChange(object sender, EventArgs args)
                {
                    bufProvider.Clear();

                    Console.CursorLeft = 0;
                    Console.CursorTop = 7;
                    Console.Write($"{vorbisReader.WaveFormat.SampleRate:N0} hz, {vorbisReader.WaveFormat.Channels} channels".PadRight(Console.WindowWidth - 1));
                    Console.CursorTop = 6;

                    fmtChgWait.Set();
                }
                vorbisReader.WaveFormatChange += HandleFormatChange;
                void HandleStreamChange(object sender, EventArgs args)
                {
                    Console.CursorLeft = 0;
                    Console.CursorTop = 3;
                    Console.WriteLine($"{vorbisReader.Tags.Title} -- ({vorbisReader.NominalBitrate / 1000f:N1} kbps)".PadRight(Console.WindowWidth - 1));
                    Console.Write($"{vorbisReader.Tags.Artist}".PadRight(Console.WindowWidth - 1));
                    Console.CursorTop = 6;
                }
                vorbisReader.StreamChange += HandleStreamChange;

                HandleFormatChange(null, EventArgs.Empty);
                HandleStreamChange(null, EventArgs.Empty);

                var keepPlaying = true;
                while (keepPlaying)
                {
                    fmtChgWait.Reset();
                    using (var wout = new WaveOutEvent())
                    {
                        wout.DesiredLatency = 50;
                        wout.Init(volProvider);

                        wout.Play();

                        var stop = false;
                        do
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(true);
                                switch (key.Key)
                                {
                                    case ConsoleKey.C:
                                        if (key.Modifiers == ConsoleModifiers.Control)
                                        {
                                            while (volProvider.Volume > FadeMinimum)
                                            {
                                                volProvider.Volume *= FadeStepMultiplier;
                                                System.Threading.Thread.Sleep(FadeStepDuration);
                                            }
                                            keepPlaying = false;
                                            stop = true;
                                        }
                                        break;
                                    case ConsoleKey.Spacebar:
                                        if (wout.PlaybackState == PlaybackState.Playing)
                                        {
                                            while (volProvider.Volume > FadeMinimum)
                                            {
                                                volProvider.Volume *= FadeStepMultiplier;
                                                System.Threading.Thread.Sleep(FadeStepDuration);
                                            }
                                            wout.Pause();
                                        }
                                        else
                                        {
                                            volProvider.Volume = FadeMinimum;
                                            wout.Play();
                                            while (volProvider.Volume <= FadeStepMultiplier)
                                            {
                                                System.Threading.Thread.Sleep(FadeStepDuration - 2);
                                                volProvider.Volume /= FadeStepMultiplier;
                                            }
                                            // just to safeguard it...
                                            volProvider.Volume = 1f;
                                        }
                                        break;
                                }
                            }

                            string state;
                            switch (wout.PlaybackState)
                            {
                                case PlaybackState.Paused: state = "[PAUSED]"; break;
                                case PlaybackState.Stopped: state = "STOPPED!"; break;
                                default: state = "        "; break;
                            }
                            var curTime = vorbisReader.CurrentTime - bufProvider.BufferDuration;

                            Console.CursorLeft = 0;
                            Console.CursorTop = 6;
                            Console.Write($"{curTime:h\\:mm\\:ss\\.f}  ({vorbisReader.Stats.EffectiveBitRate / 1000f:N1} kbps, {bufProvider.BufferDuration.TotalMilliseconds,5:N1}ms buffered)  {state} ");
                        }
                        while (!fmtChgWait.Wait(100) && !stop);

                        wout.Stop();
                    }
                }

                Console.CursorLeft = 0;
                Console.CursorTop = 8;
            }
        }
    }
}
