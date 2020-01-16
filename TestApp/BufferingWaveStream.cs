using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TestApp
{
    class BufferingWaveStream : WaveStream, IDisposable
    {
        private readonly ConcurrentQueue<byte[]> _buffers = new ConcurrentQueue<byte[]>();
        private readonly WaveStream _baseStream;

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


        public override WaveFormat WaveFormat => _baseStream.WaveFormat;

        public TimeSpan TargetBufferDuration => TimeSpan.FromSeconds((double)_targetByteCount / _baseStream.WaveFormat.BlockAlign / _baseStream.WaveFormat.SampleRate);
        public int TargetBufferSize => _targetByteCount;
        public TimeSpan BufferDuration => TimeSpan.FromSeconds((double)_byteCount / _baseStream.WaveFormat.BlockAlign / _baseStream.WaveFormat.SampleRate);
        public int BufferSize => _byteCount;

        public override bool CanTimeout => true;

        public override int ReadTimeout { get; set; }

        public override long Length => _baseStream.Length;

        public override long Position
        {
            get => _baseStream.Position;
            set
            {
                ClearBuffers(() =>
                {
                    try
                    {
                        _baseStream.Position = value;
                    }
                    catch (Exception)
                    {
                        _fillWait.Set();
                        throw;
                    }
                });
            }
        }


        public BufferingWaveStream(WaveStream baseStream)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _buffers = new ConcurrentQueue<byte[]>();

            _targetByteCount = (int)Math.Round(baseStream.WaveFormat.SampleRate * baseStream.WaveFormat.BlockAlign * .2);

            _keepBuffering = true;
            _bufferingTask = Task.Run(BufferLoad);

            ReadTimeout = Timeout.Infinite;
        }

        public void Clear()
        {
            ClearBuffers(() => { });
        }

        private void ClearBuffers(Action callback)
        {
            _fillWait.Reset();
            _readWait.Reset();

            callback();

            while (_buffers.Count > 0)
            {
                _buffers.TryDequeue(out _);
            }
            _byteCount = 0;
            _bufferReadIndex = 0;

            // start refilling the buffer
            _fillWait.Set();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _averageReadReq = _averageReadReq * 29 / 30 + count / 30;
            _averageBuffer = _averageBuffer * 29 / 30 + _byteCount / 30;
            var tgt = _averageReadReq * 5 / 4;
            if (_averageBuffer < tgt)
            {
                var adj = (tgt - _averageBuffer) / 5;
                adj -= adj % _baseStream.WaveFormat.BlockAlign;
                _targetByteCount += adj;
            }
            else if (_averageBuffer > _targetByteCount * 8 / 10)
            {
                var adj = (_averageBuffer - tgt) / 10;
                adj -= adj % _baseStream.WaveFormat.BlockAlign;
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _keepBuffering = false;
                _bufferingTask?.Wait();
                _bufferingTask?.Dispose();
                _bufferingTask = null;

                ClearBuffers(() => { });
            }

            base.Dispose(disposing);
        }

        private void BufferLoad()
        {
            var sampleCount = Math.Max(_targetByteCount / _baseStream.WaveFormat.BlockAlign / 4, 441);
            var buf = new byte[sampleCount * _baseStream.WaveFormat.BlockAlign];
            while (_keepBuffering)
            {
                var cnt = Math.Min(buf.Length, _targetByteCount - _byteCount);
                if (cnt > 0)
                {
                    cnt = _baseStream.Read(buf, 0, cnt);
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
