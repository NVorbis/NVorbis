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
            var readCount = _reader.ReadSamples(buffer, offset, count);

            //if (readCount < count)
            //{
            //    // we're almost certainly at the end of the stream...  maybe try to read the next stream?
            //    if (ConcatenateStreams)
            //    {
            //        if (_reader.SwitchStreams(_reader.StreamIndex))
            //        {
            //            readCount += Read(buffer, offset + readCount, count - readCount);

            //            _position = (long)(_reader.DecodedTime.TotalSeconds * _waveFormat.SampleRate * _waveFormat.Channels * 4);
            //        }
            //    }
            //}
            //else
            {
                _position += readCount * 4;
            }

            return readCount;
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
                _position = (long)(_reader.DecodedTime.TotalSeconds * _waveFormat.SampleRate * _waveFormat.Channels * 4);
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
