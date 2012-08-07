using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NVorbis.NAudioSupport
{
    public class VorbisWaveReader : NAudio.Wave.WaveStream, IDisposable, NAudio.Wave.ISampleProvider, NAudio.Wave.IWaveProvider
    {
        NVorbis.VorbisReader _reader;
        NAudio.Wave.WaveFormat _waveFormat;

        long _position;

        public VorbisWaveReader(string fileName)
        {
            _reader = new NVorbis.VorbisReader(fileName);

            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }
            
            base.Dispose(disposing);
        }

        public override NAudio.Wave.WaveFormat WaveFormat
        {
            get { return _waveFormat; }
        }

        public override long Length
        {
            get { return (long)(_reader.TotalTime.TotalSeconds * _waveFormat.SampleRate * _waveFormat.Channels * 4); }
        }

        public override long Position
        {
            get
            {
                return _position;
            }
            set
            {
                if (value < 0 || value > Length) throw new ArgumentOutOfRangeException("value");

                _reader.DecodedTime = TimeSpan.FromSeconds((double)value / _reader.SampleRate / _reader.Channels / 4);

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var buf = new float[count / 4];
            int cnt = Read(buf, 0, buf.Length);

            Buffer.BlockCopy(buf, 0, buffer, offset, cnt * 4);

            return cnt * 4;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            count = _reader.ReadSamples(buffer, offset, count);

            _position += count * 4;

            return count;
        }
    }
}
