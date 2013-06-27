/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NVorbis.NAudioSupport
{
    // TODO: figure out how to handle concatenated streams!

    public class VorbisWaveReader : NAudio.Wave.WaveStream, IDisposable, NAudio.Wave.ISampleProvider, NAudio.Wave.IWaveProvider
    {
        NVorbis.VorbisReader _reader;
        NAudio.Wave.WaveFormat _waveFormat;

        public VorbisWaveReader(string fileName)
        {
            _reader = new NVorbis.VorbisReader(fileName);

            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
        }

        public VorbisWaveReader(System.IO.Stream sourceStream)
        {
            _reader = new NVorbis.VorbisReader(sourceStream, false);

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
            get { return (long)(_reader.TotalTime.TotalSeconds * _waveFormat.SampleRate * _waveFormat.Channels * sizeof(float)); }
        }

        public override long Position
        {
            get
            {
                return (long)(_reader.DecodedTime.TotalSeconds * _reader.SampleRate * _reader.Channels * sizeof(float));
            }
            set
            {
                if (value < 0 || value > Length) throw new ArgumentOutOfRangeException("value");

                _reader.DecodedTime = TimeSpan.FromSeconds((double)value / _reader.SampleRate / _reader.Channels / sizeof(float));
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
            count -= count % _reader.Channels;

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
            return _reader.ReadSamples(buffer, offset, count);

            // This is for "ConcatenateStreams" support (which we're not doing currently)
            //var readCount = _reader.ReadSamples(buffer, offset, count);

            //if (readCount < count)
            //{
            //    // we're almost certainly at the end of the stream...  maybe try to read the next stream?
            //    if (ConcatenateStreams)
            //    {
            //        if (_reader.SwitchStreams(_reader.StreamIndex))
            //        {
            //            readCount += Read(buffer, offset + readCount, count - readCount);
            //        }
            //    }
            //}

            //return readCount;
        }

        public int StreamCount
        {
            get { return _reader.StreamCount; }
        }

        //public bool ConcatenateStreams { get; set; }

        public int CurrentStream
        {
            get { return _reader.StreamIndex; }
            set
            {
                if (!_reader.SwitchStreams(value))
                {
                    throw new System.IO.InvalidDataException("The selected stream is not a valid Vorbis stream!");
                }
            }
        }

        /// <summary>
        /// Gets the encoder's upper bitrate of the current selected Vorbis stream
        /// </summary>
        public int UpperBitrate { get { return _reader.UpperBitrate; } }

        /// <summary>
        /// Gets the encoder's nominal bitrate of the current selected Vorbis stream
        /// </summary>
        public int NominalBitrate { get { return _reader.NominalBitrate; } }

        /// <summary>
        /// Gets the encoder's lower bitrate of the current selected Vorbis stream
        /// </summary>
        public int LowerBitrate { get { return _reader.LowerBitrate; } }

        /// <summary>
        /// Gets the encoder's vendor string for the current selected Vorbis stream
        /// </summary>
        public string Vendor { get { return _reader.Vendor; } }

        /// <summary>
        /// Gets the comments in the current selected Vorbis stream
        /// </summary>
        public string[] Comments { get { return _reader.Comments; } }

        /// <summary>
        /// Gets the number of bits read that are related to framing and transport alone
        /// </summary>
        public long ContainerOverheadBits { get { return _reader.ContainerOverheadBits; } }

        /// <summary>
        /// Gets stats from each decoder stream available
        /// </summary>
        public IVorbisStreamStatus[] Stats { get { return _reader.Stats; } }
    }
}
