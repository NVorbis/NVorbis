using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TestApp
{
    class BufferingWaveProvider : IWaveProvider, IDisposable
    {
        private readonly ConcurrentQueue<byte[]> _buffers = new ConcurrentQueue<byte[]>();
        private readonly IWaveProvider _waveProvider;

        private int _targetByteCount;
        private int _byteCount;
        private int _bufferReadIndex;

        private bool _keepBuffering;
        private Task _bufferingTask;

        private int _averageReadReq;
        private int _averageBuffer;

        // wait handle to notify the fill thread to generate more data
        private ManualResetEventSlim _fillWait = new ManualResetEventSlim(true);

        // wait handle to notify the read thread to read more data
        private ManualResetEventSlim _readWait = new ManualResetEventSlim(false);


        public WaveFormat WaveFormat => _waveProvider.WaveFormat;

        public TimeSpan TargetBufferDuration => TimeSpan.FromSeconds((double)_targetByteCount / _waveProvider.WaveFormat.BlockAlign / _waveProvider.WaveFormat.SampleRate);
        public int TargetBufferSize => _targetByteCount;
        public TimeSpan BufferDuration => TimeSpan.FromSeconds((double)_byteCount / _waveProvider.WaveFormat.BlockAlign / _waveProvider.WaveFormat.SampleRate);
        public int BufferSize => _byteCount;

        public int ReadTimeout { get; set; }

        public BufferingWaveProvider(IWaveProvider waveProvider)
        {
            _waveProvider = waveProvider ?? throw new ArgumentNullException(nameof(waveProvider));

            _targetByteCount = (int)Math.Round(waveProvider.WaveFormat.SampleRate * waveProvider.WaveFormat.BlockAlign * .2);

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
            _byteCount = 0;
            _bufferReadIndex = 0;

            // start refilling the buffer
            _fillWait.Set();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            _averageReadReq = _averageReadReq * 29 / 30 + count / 30;
            _averageBuffer = _averageBuffer * 29 / 30 + _byteCount / 30;
            var tgt = _averageReadReq * 5 / 4;
            if (_averageBuffer < tgt)
            {
                var adj = (tgt - _averageBuffer) / 5;
                adj -= adj % _waveProvider.WaveFormat.BlockAlign;
                _targetByteCount += adj;
            }
            else if (_averageBuffer > _targetByteCount * 8 / 10)
            {
                var adj = (_averageBuffer - tgt) / 10;
                adj -= adj % _waveProvider.WaveFormat.BlockAlign;
                _targetByteCount -= adj;
            }

            var cnt = 0;
            while (cnt < count)
            {
                byte[] buf;
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
                    Buffer.BlockCopy(buf, _bufferReadIndex, buffer, offset, avail);
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
            Interlocked.Add(ref _byteCount, -cnt);

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
            var sampleCount = Math.Max(_targetByteCount / _waveProvider.WaveFormat.BlockAlign / 4, 441);
            var buf = new byte[sampleCount * _waveProvider.WaveFormat.BlockAlign];
            while (_keepBuffering)
            {
                var cnt = Math.Min(buf.Length, _targetByteCount - _byteCount);
                if (cnt > 0)
                {
                    cnt = _waveProvider.Read(buf, 0, cnt);
                }
                if (cnt > 0)
                {
                    var queueBuf = new byte[cnt];
                    Buffer.BlockCopy(buf, 0, queueBuf, 0, cnt);

                    _buffers.Enqueue(queueBuf);

                    _readWait.Set();

                    var byteCount = Interlocked.Add(ref _byteCount, cnt);
                    if (byteCount < _targetByteCount)
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
