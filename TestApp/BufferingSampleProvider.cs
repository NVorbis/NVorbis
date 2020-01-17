using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TestApp
{
    class BufferingSampleProvider : ISampleProvider, IDisposable
    {
        private readonly ConcurrentQueue<float[]> _buffers = new ConcurrentQueue<float[]>();
        private readonly ConcurrentQueue<int> _readCounts = new ConcurrentQueue<int>();
        private readonly ISampleProvider _sampleProvider;

        private int _targetSampleCount;
        private int _sampleCount;
        private int _bufferReadIndex;

        private bool _keepBuffering;
        private Task _bufferingTask;

        // wait handle to notify the fill thread to generate more data
        private ManualResetEventSlim _fillWait = new ManualResetEventSlim(true);

        // wait handle to notify the read thread to read more data
        private ManualResetEventSlim _readWait = new ManualResetEventSlim(false);


        public WaveFormat WaveFormat => _sampleProvider.WaveFormat;

        public TimeSpan TargetBufferDuration
        {
            get => TimeSpan.FromSeconds((double)_targetSampleCount / _sampleProvider.WaveFormat.Channels / _sampleProvider.WaveFormat.SampleRate);
            set => _targetSampleCount = (int)(value.TotalSeconds * _sampleProvider.WaveFormat.SampleRate) * _sampleProvider.WaveFormat.Channels;
        }
        public int TargetBufferSize
        {
            get => _targetSampleCount;
            set => _targetSampleCount = value - value % _sampleProvider.WaveFormat.Channels;
        }
        public TimeSpan BufferDuration => TimeSpan.FromSeconds((double)_sampleCount / _sampleProvider.WaveFormat.Channels / _sampleProvider.WaveFormat.SampleRate);
        public int BufferSize => _sampleCount;

        public int ReadTimeout { get; set; }


        public BufferingSampleProvider(ISampleProvider sampleProvider)
        {
            _sampleProvider = sampleProvider ?? throw new ArgumentNullException(nameof(sampleProvider));

            _targetSampleCount = (int)Math.Round(sampleProvider.WaveFormat.SampleRate * sampleProvider.WaveFormat.Channels * .2);

            _keepBuffering = true;
            _bufferingTask = Task.Run(BufferLoad);

            ReadTimeout = Timeout.Infinite;
        }

        public void Clear()
        {
            _fillWait.Reset();
            _readWait.Reset();

            while (_buffers.Count > 0)
            {
                _buffers.TryDequeue(out _);
            }
            _sampleCount = 0;
            _bufferReadIndex = 0;

            // start refilling the buffer
            _fillWait.Set();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // pass up the exception if one happens
            if (_bufferingTask.IsFaulted)
            {
                _bufferingTask.Wait();
            }

            var maxWait = count / _sampleProvider.WaveFormat.Channels * 1000 / _sampleProvider.WaveFormat.SampleRate;
            if (maxWait >= 25)
            {
                maxWait -= 20;
            }

            var cnt = 0;
            while (cnt < count)
            {
                float[] buf;
                while (!_buffers.TryPeek(out buf))
                {
                    // if there's a buffer in the queue, loop until we get it
                    if (_buffers.Count > 0) continue;

                    // underflow...

                    // otherwise, wait for the fill thread to generate a new buffer
                    _readWait.Reset();
                    _fillWait.Set();

                    // wait for data or a timeout
                    if (!_readWait.Wait(1))
                    {
                        var elapsed = (int)sw.ElapsedMilliseconds;
                        if (cnt == 0)
                        {
                            if (elapsed >= ReadTimeout && ReadTimeout > -1)
                            {
                                // no data and we've hit the read timeout limit
                                throw new TimeoutException();
                            }
                            // no data and no read timeout, loop to try again
                            continue;
                        }
                        else if (elapsed < maxWait)
                        {
                            // we have data, but we still have more time to get data, loop to try again
                            continue;
                        }
                        // we have data, but ran out of time... return what we have
                    }
                    // else: the fill thread says to go look

                    if (_buffers.Count == 0)
                    {
                        count = cnt;
                        buf = null;
                        break;
                    }
                }
                if (buf != null)
                {
                    var avail = buf.Length - _bufferReadIndex;
                    if (avail > count - cnt)
                    {
                        avail = count - cnt;
                    }
                    Buffer.BlockCopy(buf, _bufferReadIndex * sizeof(float), buffer, offset * sizeof(float), avail * sizeof(float));
                    offset += avail;
                    cnt += avail;
                    _readCounts.Enqueue(avail);
                    if ((_bufferReadIndex += avail) == buf.Length)
                    {
                        while (!_buffers.TryDequeue(out _))
                        {
                            // we know there's a buffer there, so just circle until it clears
                        }
                        _bufferReadIndex = 0;
                    }
                }
            }

            return cnt;
        }

        public void Dispose()
        {
            _keepBuffering = false;
            _bufferingTask?.Wait();
            _bufferingTask?.Dispose();
            _bufferingTask = null;

            Clear();
        }

        private void BufferLoad()
        {
            var channels = _sampleProvider.WaveFormat.Channels;

            // our fixed buffer is 20ms; this lets us respond very rapidly to read requests
            var buf = new float[_sampleProvider.WaveFormat.SampleRate / 50 * channels];
            // but our min dynamic buffer size is 10ms
            var minBufSize = _sampleProvider.WaveFormat.SampleRate / 100 * channels;

            // indicates whether to wait to set _readWait until after fully loading the buffer
            var doFullRefill = false;

            var sw = new System.Diagnostics.Stopwatch();
            while (_keepBuffering)
            {
                var maxBufSize = Math.Min(buf.Length, _targetSampleCount / 8);
                maxBufSize -= maxBufSize % channels;
                var cnt = Math.Max(Math.Min(maxBufSize, _targetSampleCount - _sampleCount), minBufSize);
                if (cnt > 0)
                {
                    cnt = _sampleProvider.Read(buf, 0, cnt);
                }
                if (cnt > 0)
                {
                    var queueBuf = new float[cnt];
                    Buffer.BlockCopy(buf, 0, queueBuf, 0, cnt * sizeof(float));

                    _buffers.Enqueue(queueBuf);

                    if (!doFullRefill)
                    {
                        // since we're not in a blocking underflow, make sure the read thread doesn't have to wait
                        _readWait.Set();
                    }

                    _sampleCount += cnt;
                    if (_sampleCount < _targetSampleCount)
                    {
                        continue;
                    }
                }
                _readWait.Set();

                // wait for the buffer to clear a little
                // note that if the reader sets the waithandle, we've underflowed and should fully rebuffer before continuing
                _fillWait.Reset();
                while (_keepBuffering && _sampleCount + minBufSize >= _targetSampleCount && !(doFullRefill = _fillWait.Wait(10)))
                {
                    // update the buffered sample count while we're waiting
                    if (_readCounts.TryDequeue(out var readCount))
                    {
                        _sampleCount -= readCount;
                    }
                }

                // forceably update the buffered sample count
                while (_readCounts.Count > 0)
                {
                    if (_readCounts.TryDequeue(out var readCount))
                    {
                        _sampleCount -= readCount;
                    }
                }
            }
        }
    }
}
