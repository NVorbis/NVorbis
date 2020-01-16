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
        private readonly ISampleProvider _sampleProvider;

        private int _targetSampleCount;
        private int _sampleCount;
        private int _bufferReadIndex;

        private bool _keepBuffering;
        private Task _bufferingTask;

        private int _averageReadReq;
        private int _averageBuffer;
        private int _minReadAhead;

        // wait handle to notify the fill thread to generate more data
        private ManualResetEventSlim _fillWait = new ManualResetEventSlim(true);

        // wait handle to notify the read thread to read more data
        private ManualResetEventSlim _readWait = new ManualResetEventSlim(false);


        public WaveFormat WaveFormat => _sampleProvider.WaveFormat;

        public TimeSpan TargetBufferDuration => TimeSpan.FromSeconds((double)_targetSampleCount / _sampleProvider.WaveFormat.Channels / _sampleProvider.WaveFormat.SampleRate);
        public int TargetBufferSize => _targetSampleCount;
        public TimeSpan BufferDuration => TimeSpan.FromSeconds((double)_sampleCount / _sampleProvider.WaveFormat.Channels / _sampleProvider.WaveFormat.SampleRate);
        public int BufferSize => _sampleCount;

        public int ReadTimeout { get; set; }

        public TimeSpan MinimumDuration
        {
            get => TimeSpan.FromSeconds((double)_minReadAhead / _sampleProvider.WaveFormat.Channels / _sampleProvider.WaveFormat.SampleRate);
            set => _minReadAhead = (int)(value.TotalSeconds * _sampleProvider.WaveFormat.Channels * _sampleProvider.WaveFormat.SampleRate);
        }


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
            _averageReadReq = _averageReadReq * 29 / 30 + count / 30;
            _averageBuffer = _averageBuffer * 29 / 30 + _sampleCount / 30;
            var tgt = _averageReadReq * 6 / 5;
            if (_averageBuffer < tgt)
            {
                var adj = (tgt - _averageBuffer) / 10;
                if (adj == 0)
                {
                    adj = _sampleProvider.WaveFormat.Channels;
                }
                else
                {
                    adj -= adj % _sampleProvider.WaveFormat.Channels;
                }
                _targetSampleCount += adj;
            }
            else if (_averageBuffer > _targetSampleCount * 8 / 10)
            {
                var adj = (_averageBuffer - tgt) / 100;
                if (adj == 0)
                {
                    adj = _sampleProvider.WaveFormat.Channels;
                }
                else
                {
                    adj -= adj % _sampleProvider.WaveFormat.Channels;
                }
                if ((_targetSampleCount -= adj) < _minReadAhead)
                {
                    _targetSampleCount = _minReadAhead;
                }
            }

            var cnt = 0;
            while (cnt < count)
            {
                float[] buf;
                while (!_buffers.TryPeek(out buf))
                {
                    _readWait.Reset();
                    _fillWait.Set();
                    if (!_readWait.Wait(ReadTimeout)) throw new TimeoutException();

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
                    if ((_bufferReadIndex += avail) == buf.Length)
                    {
                        while (!_buffers.TryDequeue(out _))
                        {
                            // we know there's a buffer there, so just circle until it clears
                        }
                        _fillWait.Set();
                        _bufferReadIndex = 0;
                    }
                }
            }
            Interlocked.Add(ref _sampleCount, -cnt);

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
            // our fixed buffer is 20ms; this lets us respond very rapidly to read requests
            var buf = new float[_sampleProvider.WaveFormat.SampleRate / 50 * _sampleProvider.WaveFormat.Channels];
            // but our min dynamic buffer size is 10ms
            var minBufSize = _sampleProvider.WaveFormat.SampleRate / 100 * _sampleProvider.WaveFormat.Channels;
            while (_keepBuffering)
            {
                var maxBufSize = Math.Min(buf.Length, _targetSampleCount / 8);
                maxBufSize -= maxBufSize % _sampleProvider.WaveFormat.Channels;
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

                    _readWait.Set();

                    var sampleCount = Interlocked.Add(ref _sampleCount, cnt);
                    if (sampleCount < _targetSampleCount)
                    {
                        continue;
                    }
                }
                else
                {
                    // tell the readers that they can read. -ish.
                    _readWait.Set();
                }

                // wait for the buffer to clear a little
                _fillWait.Reset();
                while (!_fillWait.Wait(100) && _keepBuffering) ;
            }
        }
    }
}
