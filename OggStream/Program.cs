using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Audio;

namespace OggStream
{
    static class Program
    {
        static readonly string[] StreamFiles = new[] { "2test.ogg", "2test.ogg", "2test.ogg", "2test.ogg", "2test.ogg" };

        static void Main()
        {
#if TRACE
            Trace.Listeners.Add(new ConsoleTraceListener());
#endif
            Console.WindowHeight = StreamFiles.Length + 12;

            Console.WriteLine("Pr[e]pare, [P]lay, [S]top, Pa[u]se, [R]esume, [L]oop toggle, [Q]uit");
            Console.WriteLine("Faders (in/out) : Low-pass filter [F]/[G], Volume [V]/[B]");
            Console.WriteLine("[Up], [Down] : Change current sample");
            Console.WriteLine("[Shift] + Action : Do for all " + StreamFiles.Length + " streams");

            Console.SetCursorPosition(0, 8);
            Console.WriteLine(" #  FX Buffering");

            using (new AudioContext())
            using (new OggStreamer())
            {
                bool quit = false;

                var streams = new OggStream[StreamFiles.Length];

                for (int i = 0; i < StreamFiles.Length; i++)
                {
                    streams[i] = new OggStream(StreamFiles[i]) { logX = 6, logY = 10 + i, LogHandler = Log };
                    Console.SetCursorPosition(1, 10 + i);
                    Console.Write(i);
                }
                Console.SetCursorPosition(0, 10);
                Console.Write(">");
                foreach (var s in streams)
                    s.Prepare();

                int sIdx = 0;
                var activeSet = new List<OggStream>();

                while (!quit)
                {
                    var input = Console.ReadKey(true);

                    activeSet.Clear();
                    if ((input.Modifiers & ConsoleModifiers.Shift) == ConsoleModifiers.Shift)
                        activeSet.AddRange(streams);
                    else
                        activeSet.Add(streams[sIdx]);

                    var lower = char.ToLower(input.KeyChar);
                    if (input.Key == ConsoleKey.UpArrow) lower = '-';
                    if (input.Key == ConsoleKey.DownArrow) lower = '+';

                    switch (lower)
                    {
                        case 'e': activeSet.ForEach(x => x.Prepare()); break;
                        case 'p': activeSet.ForEach(x => x.Play()); break;
                        case 'u': activeSet.ForEach(x => x.Pause()); break;
                        case 's': activeSet.ForEach(x => x.Stop()); break;
                        case 'r': activeSet.ForEach(x => x.Resume()); break;

                        case 'l':
                            activeSet.ForEach(s =>
                            {
                                s.IsLooped = !s.IsLooped;
                                Log(s.IsLooped ? "L" : " ", 3, s.logY);
                            });
                            break;

                        case 'v': FadeVolume(activeSet, true, 1); break;
                        case 'b': FadeVolume(activeSet, false, 1); break;

                        case 'f': FadeFilter(activeSet, true, 1); break;
                        case 'g': FadeFilter(activeSet, false, 1); break;

                        case '+':
                            Log(" ", 0, 10 + sIdx);
                            sIdx++;
                            if (sIdx > streams.Length - 1) sIdx = 0;
                            Log(">", 0, 10 + sIdx);
                            break;

                        case '-':
                            Log(" ", 0, 10 + sIdx);
                            sIdx--;
                            if (sIdx < 0) sIdx = streams.Length - 1;
                            Log(">", 0, 10 + sIdx);
                            break;

                        case 'q': 
                            quit = true;
                            foreach (var cts in filterFades.Values) cts.Cancel();
                            foreach (var cts in volumeFades.Values) cts.Cancel();
                            foreach (var s in streams) s.Stop(); // nicer and more effective
                            foreach (var s in streams) s.Dispose();
                            break;
                    }
                }
            }
        }

        static readonly Dictionary<OggStream, CancellationTokenSource> volumeFades = new Dictionary<OggStream, CancellationTokenSource>(); 
        static void FadeVolume(List<OggStream> streams, bool @in, float duration)
        {
            foreach (var stream in streams)
            {
                var from = stream.Volume;
                var to = @in ? 1f : 0;
                var speed = @in ? 1 - @from : @from;

                lock (volumeFades)
                {
                    CancellationTokenSource token;
                    bool found = volumeFades.TryGetValue(stream, out token);
                    if (found)
                    {
                        token.Cancel();
                        volumeFades.Remove(stream);
                    }
                }
                Log(@in ? "V" : "v", 4, stream.logY);

                var cts = new CancellationTokenSource();
                lock (volumeFades) volumeFades.Add(stream, cts);

                var sw = Stopwatch.StartNew();
                OggStream s = stream;
                Task.Factory.StartNew(() =>
                {
                    float step;
                    do
                    {
                        step = (float) Math.Min(sw.Elapsed.TotalSeconds / (duration * speed), 1);
                        s.Volume = (to - @from) * step + @from;
                        Thread.Sleep(1000 / 60);
                    } while (step < 1 && !cts.Token.IsCancellationRequested);
                    sw.Stop();

                    if (!cts.Token.IsCancellationRequested) 
                    {
                        lock (volumeFades) volumeFades.Remove(s);
                        Log(" ", 4, s.logY);
                    }
                }, cts.Token);
            }
        }

        static readonly Dictionary<OggStream, CancellationTokenSource> filterFades = new Dictionary<OggStream, CancellationTokenSource>();
        static void FadeFilter(List<OggStream> streams, bool @in, float duration)
        {
            foreach (var stream in streams)
            {
                var from = stream.LowPassHFGain;
                var to = @in ? 1f : 0;
                var speed = @in ? 1 - @from : @from;

                lock (filterFades)
                {
                    CancellationTokenSource token;
                    bool found = filterFades.TryGetValue(stream, out token);
                    if (found)
                    {
                        token.Cancel();
                        filterFades.Remove(stream);
                    }
                }
                Log(@in ? "F" : "f", 5, stream.logY);

                var cts = new CancellationTokenSource();
                lock (filterFades) filterFades.Add(stream, cts);

                var sw = Stopwatch.StartNew();
                OggStream s = stream;
                Task.Factory.StartNew(() =>
                {
                    float step;
                    do
                    {
                        step = (float)Math.Min(sw.Elapsed.TotalSeconds / (duration * speed), 1);
                        s.LowPassHFGain = (to - @from) * step + @from;
                        Thread.Sleep(1000 / 60);
                    } while (step < 1 && !cts.Token.IsCancellationRequested);
                    sw.Stop();

                    if (!cts.Token.IsCancellationRequested)
                    {
                        lock (filterFades) filterFades.Remove(s);
                        Log(" ", 5, s.logY);
                    }
                }, cts.Token);
            }
        }

        static readonly object logMutex = new object();
        static void Log(string text, int x, int y)
        {
            lock (logMutex)
            {
                Console.SetCursorPosition(Math.Min(Console.BufferWidth - 1, x), y);
                Console.Write(text);
                Console.SetCursorPosition(0, Console.WindowHeight - 1);
            }
        }
    }
}
