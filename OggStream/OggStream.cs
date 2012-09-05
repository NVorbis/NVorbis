using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NVorbis;
using OpenTK.Audio.OpenAL;

namespace OggStream
{
    class OggStream : IDisposable
    {
        // 2 buffers of 0.5 second (stereo) ready at all times
        const int BufferCount = 3;
        const int BufferSize = 44100;

        // in times per second, at most
        const float StreamingThreadUpdateRate = 10;

        readonly float[] readSampleBuffer = new float[BufferSize];
        readonly short[] castBuffer = new short[BufferSize];

        readonly int[] alBufferIds;
        readonly int alSourceId;
        readonly int alFilterId;

        static readonly XRamExtension xre;
        static readonly EffectsExtension fxe;

        class ThreadFlow : IDisposable
        {
            public bool Cancelled;
            public bool Finished;
            public readonly ManualResetEventSlim PauseEvent;

            public ThreadFlow()
            {
                PauseEvent = new ManualResetEventSlim(true);
            }

            public void Dispose()
            {
                PauseEvent.Dispose();
            }
        }

        ThreadFlow currentFlow;
        Thread currentThread;

        Stream underlyingStream;
        VorbisReader reader;
        bool ready;

        public bool IsLooped { get; set; }

        static OggStream()
        {
            xre = new XRamExtension();
            fxe = new EffectsExtension();
        }

        public OggStream(string filename) : this(File.OpenRead(filename)) { }
        public OggStream(Stream stream)
        {
            alBufferIds = AL.GenBuffers(BufferCount);
            alSourceId = AL.GenSource();
            Volume = 1;
            Check();

            if (xre.IsInitialized)
            {
                Trace.WriteLine("hardware buffers will be used");
                xre.SetBufferMode(BufferCount, ref alBufferIds[0], XRamExtension.XRamStorage.Hardware);
                Check();
            }

            if (fxe.IsInitialized)
            {
                Trace.WriteLine("effects will be used");
                alFilterId = fxe.GenFilter();
                fxe.Filter(alFilterId, EfxFilteri.FilterType, (int)EfxFilterType.Lowpass);
                fxe.Filter(alFilterId, EfxFilterf.LowpassGain, 1);
                LowPassHFGain = 1;
            }

            underlyingStream = stream;
            Open(precache: true);
        }

        void Open(bool precache = false)
        {
            underlyingStream.Seek(0, SeekOrigin.Begin);
            reader = new VorbisReader(underlyingStream, false);

            if (precache)
            {
                foreach (var buffer in alBufferIds)
                    FillBuffer(buffer);
                AL.SourceQueueBuffers(alSourceId, BufferCount, alBufferIds);
                Check();
            }

            ready = true;

            Trace.WriteLine("streaming is ready");
        }

        public void Play()
        {
            if (AL.GetSourceState(alSourceId) == ALSourceState.Playing)
            {
                Trace.WriteLine("stream is already playing");
                return;
            }

            if (AL.GetSourceState(alSourceId) == ALSourceState.Paused)
            {
                Resume();
                return;
            }

            if (currentFlow != null && currentFlow.Finished)
                Stop();

            if (!ready)
                Open(precache: true);

            Trace.WriteLine("starting playback");
            AL.SourcePlay(alSourceId);
            Check();

            currentThread = new Thread(() => EnsureBuffersFilled(currentFlow = new ThreadFlow()));
            currentThread.Priority = ThreadPriority.Lowest;
            currentThread.Start();
        }

        public void Pause()
        {
            if (AL.GetSourceState(alSourceId) != ALSourceState.Playing)
            {
                Trace.WriteLine("stream is not playing");
                return;
            }

            currentFlow.PauseEvent.Reset();

            Trace.WriteLine("pausing playback");
            AL.SourcePause(alSourceId);
            Check();
        }

        public void Resume()
        {
            if (AL.GetSourceState(alSourceId) != ALSourceState.Paused)
            {
                Trace.WriteLine("stream is not paused");
                return;
            }

            currentFlow.PauseEvent.Set();

            Trace.WriteLine("resuming playback");
            AL.SourcePlay(alSourceId);
            Check();
        }

        public void Stop()
        {
            var state = AL.GetSourceState(alSourceId);
            if (state == ALSourceState.Playing || state == ALSourceState.Paused)
            {
                Trace.WriteLine("stopping playback");
                StopPlayback();
            }

            if (currentFlow != null)
                StopStreaming();

            if (state != ALSourceState.Initial)
                Empty();
            Close();
        }

        public float LowPassHFGain
        {
            set
            {
                if (fxe.IsInitialized)
                {
                    fxe.Filter(alFilterId, EfxFilterf.LowpassGainHF, value);
                    fxe.BindFilterToSource(alSourceId, alFilterId);
                    Check();
                }
            }
        }

        float volume;
        public float Volume
        {
            get { return volume; }
            set { AL.Source(alSourceId, ALSourcef.Gain, volume = value); }
        }

        public void Dispose()
        {
            Trace.WriteLine("disposing stream");

            var state = AL.GetSourceState(alSourceId);
            if (state == ALSourceState.Playing || state == ALSourceState.Paused)
                StopPlayback();

            if (currentFlow != null)
                StopStreaming(join: true);

            if (state != ALSourceState.Initial)
                Empty();

            Close();

            underlyingStream.Dispose();

            AL.DeleteSource(alSourceId);
            AL.DeleteBuffers(alBufferIds);

            if (fxe.IsInitialized)
                fxe.DeleteFilter(alFilterId);

            Check();
        }

        void StopPlayback()
        {
            AL.SourceStop(alSourceId);
            Check();
        }

        void StopStreaming(bool join = false)
        {
            currentFlow.Cancelled = true;
            currentFlow.PauseEvent.Set();
            currentFlow = null;

            if (join)
                currentThread.Join();

            currentThread = null;
        }

        void Empty()
        {
            int queued;

            AL.GetSource(alSourceId, ALGetSourcei.BuffersQueued, out queued);
            if (queued > 0)
            {
                AL.SourceUnqueueBuffers(alSourceId, queued);
                Check();
            }
        }

        void Close()
        {
            if (reader != null)
            {
                reader.Dispose();
                reader = null;
            }
            ready = false;
        }

        bool FillBuffer(int bufferId)
        {
            var readSamples = reader.ReadSamples(readSampleBuffer, 0, BufferSize);
            CastBuffer(readSampleBuffer, castBuffer, readSamples);
            AL.BufferData(bufferId, reader.Channels == 1 ? ALFormat.Mono16 : ALFormat.Stereo16, castBuffer,
                          readSamples * sizeof (short), reader.SampleRate);
            Check();

            if (readSamples != BufferSize)
                Trace.WriteLine("eos detected; only " + readSamples + " samples found");
            TraceMemoryUsage();

            return readSamples != BufferSize;
        }

        [Conditional("TRACE")]
        static void TraceMemoryUsage()
        {
            var usedHeap = (double)GC.GetTotalMemory(true);

            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            while (usedHeap >= 1024 && order + 1 < sizes.Length)
            {
                order++;
                usedHeap = usedHeap / 1024;
            }

            Trace.WriteLine(String.Format("memory used : {0:0.###} {1}", usedHeap, sizes[order]));
        }

        static void CastBuffer(float[] inBuffer, short[] outBuffer, int length)
        {
            for (int i = 0; i < length; i++)
            {
                var temp = (int) (32767f * inBuffer[i]);
                if (temp > short.MaxValue) temp = short.MaxValue;
                else if (temp < short.MinValue) temp = short.MinValue;
                outBuffer[i] = (short)temp;
            }
        }

        static void Check()
        {
            ALError error;
            if ((error = AL.GetError()) != ALError.NoError)
                throw new InvalidOperationException(AL.GetErrorString(error));
        }

        void EnsureBuffersFilled(ThreadFlow flow)
        {
            bool finished = false;

            while (!finished)
            {
                flow.PauseEvent.Wait();
                if (flow.Cancelled)
                {
                    Trace.WriteLine("streaming cancelled");
                    flow.Dispose();
                    return;
                }

                Thread.Sleep((int) (1000 / StreamingThreadUpdateRate));

                flow.PauseEvent.Wait();
                if (flow.Cancelled)
                {
                    Trace.WriteLine("streaming cancelled");
                    flow.Dispose();
                    return;
                }

                try
                {
                    int processed;
                    AL.GetSource(alSourceId, ALGetSourcei.BuffersProcessed, out processed);
                    Check();

                    if (processed == 0)
                        continue;

                    var tempBuffers = AL.SourceUnqueueBuffers(alSourceId, processed);

                    for (int i = 0; i < processed; i++)
                    {
                        finished |= FillBuffer(tempBuffers[i]);

                        if (finished && IsLooped)
                        {
                            finished = false;
                            Close();
                            Open();
                        }

                        Trace.WriteLine("buffer " + tempBuffers[i] + " refilled");
                    }

                    AL.SourceQueueBuffers(alSourceId, processed, tempBuffers);
                    Check();

                    var state = AL.GetSourceState(alSourceId);
                    if (state == ALSourceState.Stopped)
                    {
                        Trace.WriteLine("buffer underrun detected! restarting playback");
                        AL.SourcePlay(alSourceId);
                        Check();
                    }
                }
                catch (Exception ex)
                {
                    if (!flow.Cancelled)
                        throw ex;
                    Trace.WriteLine("cancellation caused an error : " + ex.Message);
                }
            }

            flow.Finished = true;
            Trace.WriteLine("streaming complete");
        }
    }
}
