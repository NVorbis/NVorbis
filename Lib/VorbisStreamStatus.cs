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

namespace NVorbis
{
    public class VorbisStreamStatus
    {
        OggContainerReader _container;
        VorbisReader _reader;

        internal VorbisStreamStatus(OggContainerReader container, VorbisReader reader)
        {
            _container = container;
            _reader = reader;

            // _container._containerBits
            // _reader._bookBits
            // _reader._floorBits
            // _reader._floorHdrBits
            // _reader._glueBits
            // _reader._mapHdrBits
            // _reader._metaBits
            // _reader._modeBits
            // _reader._modeHdrBits
            // _reader._resBits
            // _reader._resHdrBits
            // _reader._timeHdrBits
            // _reader._wasteBits
            // _reader._wasteHdrBits
            
            // _reader._samples
            // _reader._sw
        }

        /// <summary>
        /// Resets the counters for latency & bitrate calculations, as well as overall bit counts
        /// </summary>
        public void Reset()
        {
            // only reset the stream info...  don't mess with the container, book, and hdr bits...

            _reader._packetCount = 0;
            _reader._floorBits = 0L;
            _reader._glueBits = 0L;
            _reader._modeBits = 0L;
            _reader._resBits = 0L;
            _reader._wasteBits = 0L;
            _reader._samples = 0L;
            _reader._sw.Reset();
        }

        /// <summary>
        /// Returns the calculated bit rate of audio stream data for the everything decoded so far
        /// </summary>
        public int EffectiveBitRate
        {
            get
            {
                if (_reader._samples == 0L)
                {
                    return _reader.NominalBitrate;
                }

                return (int)(AudioBits / _reader.DecodedTime.TotalSeconds);
            }
        }

        /// <summary>
        /// Returns the calculated bit rate for the last ~1 second of audio
        /// </summary>
        public int InstantBitRate
        {
            get
            {
                try
                {
                    return (int)(_reader.LastSecondBits * _reader._sampleRate / _reader.LastSecondSamples);
                }
                catch (DivideByZeroException)
                {
                    return -1;
                }
            }
        }

        //public TimeSpan PageLatency
        //{
        //    get { }
        //}

        /// <summary>
        /// Returns the calculated latency per packet
        /// </summary>
        public TimeSpan PacketLatency
        {
            get
            {
                return TimeSpan.FromTicks(_reader._sw.ElapsedTicks / _reader._packetCount);
            }
        }

        /// <summary>
        /// Returns the calculated latency per second of output
        /// </summary>
        public TimeSpan SecondLatency
        {
            get
            {
                return TimeSpan.FromTicks((_reader._sw.ElapsedTicks / _reader._samples) * _reader._sampleRate);
            }
        }

        /// <summary>
        /// Returns the number of bits read that do not contribute to the output audio
        /// </summary>
        public long OverheadBits
        {
            get { return _container._containerBits + _reader._glueBits + _reader._metaBits + _reader._timeHdrBits + _reader._wasteHdrBits + _reader._wasteBits; }
        }

        /// <summary>
        /// Returns the number of bits read that contribute to the output audio
        /// </summary>
        public long AudioBits
        {
            get { return _reader._bookBits + _reader._floorHdrBits + _reader._resHdrBits + _reader._mapHdrBits + _reader._modeHdrBits + _reader._modeBits + _reader._floorBits + _reader._resBits; }
        }

        /// <summary>
        /// Returns the number of pages read so far
        /// </summary>
        public int PagesRead
        {
            get { return _container.GetReadPageCount(); }
        }

        /// <summary>
        /// Returns the total number of pages in the stream
        /// </summary>
        /// <remarks>Side-Effect: Updates PagesRead</remarks>
        public int GetTotalPages()
        {
            return _container.GetTotalPageCount();
        }
    }
}
