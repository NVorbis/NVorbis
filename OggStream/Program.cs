using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using OpenTK.Audio;

namespace OggStream
{
    static class Program
    {
        static void Main()
        {
#if DEBUG
            Trace.Listeners.Add(new ConsoleTraceListener());
#endif
            Console.WriteLine("p : play");
            Console.WriteLine("s : stop");
            Console.WriteLine("u : pause");
            Console.WriteLine("r : resume");
            Console.WriteLine("l : toggle looping");
            Console.WriteLine("f : low-pass filter mode");
            Console.WriteLine("v : volume mode");
            Console.WriteLine("/, 1,..., 9, 0 : change current mode's value");
            Console.WriteLine("q : quit\n");

            // Trap subsequent text input
            Console.SetOut(TextWriter.Null);

            using (new AudioContext())
            using (var stream = new OggStream(@"2test.ogg"))
            {
                bool quit = false;
                bool lowPass = false;

                while (!quit)
                {
                    var input = Console.ReadKey();

                    switch (input.KeyChar)
                    {
                        case 'p': stream.Play(); break;
                        case 'u': stream.Pause(); break;
                        case 's': stream.Stop(); break;
                        case 'r': stream.Resume(); break;
                        case 'l': 
                            stream.IsLooped = !stream.IsLooped; 
                            Trace.Write("stream looping set to " + stream.IsLooped);
                            break;
                        case 'v':
                            if (lowPass) Trace.WriteLine("switched to volume mode");
                            lowPass = false; 
                            break;
                        case 'f': 
                            if (!lowPass) Trace.WriteLine("switched to low-pass filter mode");
                            lowPass = true; 
                            break;
                        case 'q': quit = true; break;
                    }

                    if (lowPass)
                        switch (input.KeyChar)
                        {
                            case '/': stream.LowPassHFGain = 0; break;
                            case '1': stream.LowPassHFGain = 0.1f; break;
                            case '2': stream.LowPassHFGain = 0.2f; break;
                            case '3': stream.LowPassHFGain = 0.3f; break;
                            case '4': stream.LowPassHFGain = 0.4f; break;
                            case '5': stream.LowPassHFGain = 0.5f; break;
                            case '6': stream.LowPassHFGain = 0.6f; break;
                            case '7': stream.LowPassHFGain = 0.7f; break;
                            case '8': stream.LowPassHFGain = 0.8f; break;
                            case '9': stream.LowPassHFGain = 0.9f; break;
                            case '0': stream.LowPassHFGain = 1f; break;
                        }
                    else
                        switch (input.KeyChar)
                        {
                            case '/': stream.Volume = 0; break;
                            case '1': stream.Volume = 0.1f; break;
                            case '2': stream.Volume = 0.2f; break;
                            case '3': stream.Volume = 0.3f; break;
                            case '4': stream.Volume = 0.4f; break;
                            case '5': stream.Volume = 0.5f; break;
                            case '6': stream.Volume = 0.6f; break;
                            case '7': stream.Volume = 0.7f; break;
                            case '8': stream.Volume = 0.8f; break;
                            case '9': stream.Volume = 0.9f; break;
                            case '0': stream.Volume = 1f; break;
                        }
                }
            }
        }
    }
}
