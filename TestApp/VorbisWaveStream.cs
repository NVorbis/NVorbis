using NVorbis.Contracts;
using System;

namespace TestApp
{
    class VorbisWaveStream : NAudio.Wave.WaveStream, NAudio.Wave.ISampleProvider
    {
        private IContainerReader _containerReader;
        private IStreamDecoder _streamDecoder;

        NAudio.Wave.WaveFormat _waveFormat;

        public VorbisWaveStream(System.IO.Stream sourceStream)
        {
            _containerReader = new NVorbis.Ogg.ContainerReader(sourceStream, false);
            _containerReader.NewStreamCallback = ProcessNewStream;
            if (!_containerReader.TryInit()) throw new ArgumentException("Could not initialize container!");
        }

        bool ProcessNewStream(IPacketProvider packetProvider)
        {
            IStreamDecoder decoder;
            try
            {
                decoder = new NVorbis.StreamDecoder(packetProvider);
            }
            catch
            {
                return false;
            }

            var channels = _waveFormat?.Channels ?? 0;
            var sampleRate = _waveFormat?.SampleRate ?? 0;

            // save off the old decoder for later
            var lastStreamDecoder = _streamDecoder;

            // change to the new decoder
            _streamDecoder = decoder;

            // notify that the stream has changed
            StreamChange?.Invoke(this, EventArgs.Empty);

            // flag that the format changed
            if (channels != _streamDecoder.Channels || sampleRate != _streamDecoder.SampleRate)
            {
                SetWaveFormat(_streamDecoder.SampleRate, _streamDecoder.Channels);
            }

            // clean up the old decoder
            lastStreamDecoder?.Dispose();

            return true;
        }

        private void SetWaveFormat(int sampleRate, int channels)
        {
            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            WaveFormatChange?.Invoke(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _streamDecoder?.Dispose();
                _streamDecoder = null;

                _containerReader?.Dispose();
                _containerReader = null;
            }

            base.Dispose(disposing);
        }

        public event EventHandler WaveFormatChange;
        public event EventHandler StreamChange;

        public override NAudio.Wave.WaveFormat WaveFormat => _waveFormat;

        public override long Length => _containerReader.CanSeek ? _streamDecoder.TotalSamples * _waveFormat.BlockAlign : throw new InvalidOperationException("Cannot seek underlying stream!");

        public override long Position
        {
            get => _streamDecoder.SamplePosition * _waveFormat.BlockAlign;
            set
            {
                if (!_containerReader.CanSeek) throw new InvalidOperationException("Cannot seek underlying stream!");
                if (value < 0 || value > Length) throw new ArgumentOutOfRangeException(nameof(value));

                _streamDecoder.SamplePosition = value / _waveFormat.BlockAlign;
            }
        }

        // This buffer can be static because it can only be used by 1 instance per thread
        [ThreadStatic]
        static float[] _conversionBuffer = null;

        public override int Read(byte[] buffer, int offset, int count)
        {
            // adjust count so it is in floats instead of bytes
            count /= sizeof(float);

            // make sure we don't have an odd count
            count -= count % _streamDecoder.Channels;

            // get the buffer, creating a new one if none exists or the existing one is too small
            var cb = _conversionBuffer ?? (_conversionBuffer = new float[count]);
            if (cb.Length < count)
            {
                cb = (_conversionBuffer = new float[count]);
            }

            // let Read(float[], int, int) do the actual reading; adjust count back to bytes
            int cnt = Read(cb, 0, count) * sizeof(float);

            // move the data back to the request buffer
            Buffer.BlockCopy(cb, 0, buffer, offset, cnt);

            // done!
            return cnt;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_streamDecoder.IsEndOfStream)
            {
                // we don't care about the return value because everything is handled elsewhere...
                _containerReader.FindNextStream();
            }

            var cnt = _streamDecoder.Read(buffer, offset, count, out var isParmChange);
            if (isParmChange && cnt == 0)
            {
                if (_streamDecoder.Channels != _waveFormat.Channels || _streamDecoder.SampleRate != _waveFormat.SampleRate)
                {
                    SetWaveFormat(_streamDecoder.SampleRate, _streamDecoder.Channels);
                    return 0;
                }
            }
            return cnt;
        }

        public IStreamStats Stats => _streamDecoder.Stats;
        public ITagData Tags => _streamDecoder.Tags;

        /// <summary>
        /// Gets the encoder's upper bitrate of the current selected Vorbis stream
        /// </summary>
        public int UpperBitrate => _streamDecoder.UpperBitrate;

        /// <summary>
        /// Gets the encoder's nominal bitrate of the current selected Vorbis stream
        /// </summary>
        public int NominalBitrate => _streamDecoder.NominalBitrate;

        /// <summary>
        /// Gets the encoder's lower bitrate of the current selected Vorbis stream
        /// </summary>
        public int LowerBitrate => _streamDecoder.LowerBitrate;
    }
}
