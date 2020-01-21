using NVorbis;
using NVorbis.Contracts;
using System;

namespace TestApp
{
    class VorbisWaveStream : NAudio.Wave.WaveStream, NAudio.Wave.ISampleProvider
    {
        internal static Func<string, IVorbisReader> CreateFileReader { get; set; } = fn => new VorbisReader(fn);
        internal static Func<System.IO.Stream, IVorbisReader> CreateStreamReader { get; set; } = ss => new VorbisReader(ss, false);

        private IVorbisReader _reader;

        NAudio.Wave.WaveFormat _waveFormat;

        public event EventHandler ParameterChange;

        public VorbisWaveStream(string fileName)
        {
            _reader = CreateFileReader(fileName);

            UpdateWaveFormat();
        }
        
        public VorbisWaveStream(System.IO.Stream sourceStream)
        {
            _reader = CreateStreamReader(sourceStream);

            UpdateWaveFormat();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader?.Dispose();
                _reader = null;
            }

            base.Dispose(disposing);
        }

        public override NAudio.Wave.WaveFormat WaveFormat => _waveFormat;

        private void UpdateWaveFormat()
        {
            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
            ParameterChange?.Invoke(this, EventArgs.Empty);
        }

        public override long Length => _reader.TotalSamples * _waveFormat.BlockAlign;

        public override long Position
        {
            get => _reader.SamplePosition * _waveFormat.BlockAlign;
            set => _reader.SamplePosition = value / _waveFormat.BlockAlign;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // adjust count so it is in samples instead of bytes
            count /= sizeof(float) * _reader.Channels;

            // get an aligned offset into the buffer
            var sampleOffset = offset / (sizeof(float) * _reader.Channels);
            if (sampleOffset < offset * (sizeof(float) * _reader.Channels))
            {
                // move to the next viable position and remove a sample from our count
                ++sampleOffset;
                if (--count <= 0)
                {
                    // of course, if we then have no samples, we can't read anything... just return
                    return 0;
                }
            }

            // actually do the read
            var cnt = sizeof(float) * Read(new NAudio.Wave.WaveBuffer(buffer), sampleOffset, count);

            // move the data to the requested offset
            if (cnt > 0)
            {
                sampleOffset *= (sizeof(float) * _reader.Channels);
                if (sampleOffset > offset)
                {
                    Buffer.BlockCopy(buffer, sampleOffset, buffer, offset, cnt);
                }
            }

            // done!
            return cnt;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (IsParameterChange) throw new InvalidOperationException("Parameter change pending!  Call ClearParameterChange() before reading more data.");

            var cnt = _reader.ReadSamples(buffer, offset, count);
            if (cnt == 0)
            {
                if (_reader.IsEndOfStream && AutoAdvanceToNextStream)
                {
                    if (_reader.StreamIndex < _reader.Streams.Count - 1)
                    {
                        if (_reader.SwitchStreams(_reader.StreamIndex + 1))
                        {
                            IsParameterChange = true;
                            UpdateWaveFormat();
                            return 0;
                        }
                        else
                        {
                            return Read(buffer, offset, count);
                        }
                    }
                }
            }
            return cnt;
        }

        public bool AutoAdvanceToNextStream { get; set; }

        public bool IsParameterChange { get; private set; }

        public void ClearParameterChange() => IsParameterChange = false;

        public bool IsEndOfStream => _reader.IsEndOfStream;

        public int StreamCount => _reader.Streams.Count;

        public int StreamIndex
        {
            get => _reader.StreamIndex;
            set
            {
                if (value < 0 || value >= _reader.Streams.Count) throw new ArgumentOutOfRangeException(nameof(value));
                if (_reader.SwitchStreams(value))
                {
                    UpdateWaveFormat();
                }
            }
        }

        public bool FindNewStream() => _reader.FindNextStream();

        public IStreamStats Stats => _reader.Stats;
        public ITagData Tags => _reader.Tags;

        /// <summary>
        /// Gets the encoder's upper bitrate of the current selected Vorbis stream
        /// </summary>
        public int UpperBitrate => _reader.UpperBitrate;

        /// <summary>
        /// Gets the encoder's nominal bitrate of the current selected Vorbis stream
        /// </summary>
        public int NominalBitrate => _reader.NominalBitrate;

        /// <summary>
        /// Gets the encoder's lower bitrate of the current selected Vorbis stream
        /// </summary>
        public int LowerBitrate => _reader.LowerBitrate;
    }
}
