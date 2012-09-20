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
using System.IO;

namespace NVorbis
{
    public class VorbisReader : IDisposable
    {
        IPacketProvider _packetProvider;
        int _streamIdx;

        Stream _sourceStream;
        bool _closeSourceOnDispose;

        List<VorbisStreamDecoder> _decoders;
        List<int> _serials;

        public VorbisReader(string fileName)
            : this(File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read), true)
        {
        }

        public VorbisReader(Stream stream, bool closeStreamOnDispose)
        {
            _decoders = new List<VorbisStreamDecoder>();
            _serials = new List<int>();

            if (stream.CanSeek)
            {
                // TODO: need to check the first byte(s) for Matroska

                _packetProvider = new Ogg.ContainerReader(stream, NewStream);

                // NewStream will populate our decoder list...
            }
            else
            {
                // RTP???
                throw new NotSupportedException("The specified stream is not seekable.");
            }

            _packetProvider.Init();

            _sourceStream = stream;
            _closeSourceOnDispose = closeStreamOnDispose;

            // TODO: check the skeleton to see if we need to find a particular stream serial
            //       to start decoding

            // by this point, the container init should have found at least one vorbis stream...
            if (_decoders.Count == 0) throw new InvalidDataException("No Vorbis data is available in the specified file.");

            _streamIdx = 0;
        }

        void NewStream(int streamSerial)
        {
            var initialPacket = _packetProvider.GetNextPacket(streamSerial);
            var checkByte = (byte)initialPacket.PeekByte();

            // TODO: determine what type of stream this is, load the correct decoder (or ignore
            //       it), then keep going.  If it's a skeleton stream, try to load it.  Only
            //       allow one instance.  Then force another page to be read.

            // for now, we only support the Vorbis decoder; Skeleton will happen later
            if (checkByte == VorbisStreamDecoder.InitialPacketMarker)
            {
                var decoder = new VorbisStreamDecoder(
                    () => {
                        var provider = _packetProvider;
                        if (provider != null)
                        {
                            return provider.GetNextPacket(streamSerial);
                        }
                        return null;
                    },
                    () =>
                    {
                        var provider = _packetProvider;
                        if (provider != null)
                        {
                            return provider.GetTotalPageCount(streamSerial);
                        }
                        return 0;
                    }
                );
                try
                {
                    if (decoder.TryInit(initialPacket))
                    {
                        // the init worked, so we have a valid stream...

                        _decoders.Add(decoder);
                        _serials.Add(streamSerial);
                    }
                    // else: the initial packet wasn't for Vorbis...
                }
                catch (InvalidDataException)
                {
                    // there was an error loading the headers... problem is, we're past the first packet, so we can't go back and try again...
                    // TODO: log the error
                }
            }
            // we're not supporting Skeleton yet...
            //else if (checkByte == Ogg.SkeletonDecoder.InitialPacketMarker)
            //{
            //    // load it
            //    if (_skeleton != null) throw new InvalidDataException("Second skeleton stream found!");
            //    _skeleton = new Ogg.SkeletonDecoder(() => _packetProvider.GetNextPacket(streamSerial));

            //    if (_skeleton.Init(initialPacket))
            //    {
            //        // force the next stream to load
            //        _packetProvider.FindNextStream(streamSerial);
            //    }
            //    else
            //    {
            //        _skeleton = null;
            //    }
            //}
        }

        public void Dispose()
        {
            if (_packetProvider != null)
            {
                _packetProvider.Dispose();
                _packetProvider = null;
            }

            if (_closeSourceOnDispose)
            {
                _sourceStream.Close();
            }

            _sourceStream = null;
        }

        void SeekTo(long granulePos)
        {
            if (!_packetProvider.CanSeek) throw new NotSupportedException();

            if (granulePos < 0) throw new ArgumentOutOfRangeException("granulePos");

            var targetPacketIndex = 3;
            if (granulePos > 0)
            {
                var idx = _packetProvider.FindPacket(
                    _serials[_streamIdx],
                    granulePos,
                    (prevPacket, curPacket, nextPacket) =>
                    {
                        // ask the decoder...
                        return _decoders[_streamIdx].GetPacketLength(curPacket, prevPacket);
                    }
                );
                if (idx == -1) throw new ArgumentOutOfRangeException("granulePos");
                targetPacketIndex = idx - 1;  // move to the previous packet to prime the decoder
            }

            // get the data packet for later
            var dataPacket = _packetProvider.GetPacket(_serials[_streamIdx], targetPacketIndex);

            // actually seek the stream
            _packetProvider.SeekToPacket(_serials[_streamIdx], targetPacketIndex);

            // now read samples until we are exactly at the granule position requested
            _decoders[_streamIdx].CurrentPosition = dataPacket.GranulePosition;
            var cnt = (int)((granulePos - _decoders[_streamIdx].CurrentPosition) * Channels);
            if (cnt > 0)
            {
                var seekBuffer = new float[cnt];
                while (cnt > 0)
                {
                    var temp = _decoders[_streamIdx].ReadSamples(seekBuffer, 0, cnt);
                    if (temp == 0) break;   // we're at the end...
                    cnt -= temp;
                }
            }
        }

        #region Public Interface

        /// <summary>
        /// Gets the number of channels in the current selected Vorbis stream
        /// </summary>
        public int Channels { get { return _decoders[_streamIdx]._channels; } }

        /// <summary>
        /// Gets the sample rate of the current selected Vorbis stream
        /// </summary>
        public int SampleRate { get { return _decoders[_streamIdx]._sampleRate; } }

        /// <summary>
        /// Gets the encoder's upper bitrate of the current selected Vorbis stream
        /// </summary>
        public int UpperBitrate { get { return _decoders[_streamIdx]._upperBitrate; } }

        /// <summary>
        /// Gets the encoder's nominal bitrate of the current selected Vorbis stream
        /// </summary>
        public int NominalBitrate { get { return _decoders[_streamIdx]._nominalBitrate; } }

        /// <summary>
        /// Gets the encoder's lower bitrate of the current selected Vorbis stream
        /// </summary>
        public int LowerBitrate { get { return _decoders[_streamIdx]._lowerBitrate; } }

        /// <summary>
        /// Gets the encoder's vendor string for the current selected Vorbis stream
        /// </summary>
        public string Vendor { get { return _decoders[_streamIdx]._vendor; } }

        /// <summary>
        /// Gets the comments in the current selected Vorbis stream
        /// </summary>
        public string[] Comments { get { return _decoders[_streamIdx]._comments; } }

        /// <summary>
        /// Gets the number of bits read that are related to framing and transport alone
        /// </summary>
        public long ContainerOverheadBits { get { return _packetProvider.ContainerBits; } }

        /// <summary>
        /// Gets stats from each decoder stream available
        /// </summary>
        public IVorbisStreamStatus[] Stats
        {
            get { return _decoders.Select(d => d).Cast<IVorbisStreamStatus>().ToArray(); }
        }

        /// <summary>
        /// Gets the currently-selected stream's index
        /// </summary>
        public int StreamIndex
        {
            get { return _streamIdx; }
        }

        /// <summary>
        /// Reads decoded samples from the current logical stream
        /// </summary>
        /// <param name="buffer">The buffer to write the samples to</param>
        /// <param name="offset">The offset into the buffer to write the samples to</param>
        /// <param name="count">The number of samples to write</param>
        /// <returns>The number of samples written</returns>
        public int ReadSamples(float[] buffer, int offset, int count)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException("count");

            return _decoders[_streamIdx].ReadSamples(buffer, offset, count);
        }

        /// <summary>
        /// Returns the number of logical streams found so far in the physical container
        /// </summary>
        public int StreamCount
        {
            get { return _decoders.Count; }
        }

        /// <summary>
        /// Switches to an alternate logical stream.
        /// </summary>
        /// <param name="index">The logical stream index to switch to</param>
        /// <returns><c>True</c> if the properties of the logical stream differ from those of the one previously being decoded. Otherwise, <c>False</c>.</returns>
        public bool SwitchStreams(int index)
        {
            if (index < 0 || index >= StreamCount) throw new ArgumentOutOfRangeException("index");

            if (_streamIdx == index) return false;

            var curDecoder = _decoders[_streamIdx];
            _streamIdx = index;
            var newDecoder = _decoders[_streamIdx];

            return curDecoder._channels != newDecoder._channels || curDecoder._sampleRate != newDecoder._sampleRate;
        }

        /// <summary>
        /// Gets or Sets the current timestamp of the decoder.  Is the timestamp before the next sample to be decoded
        /// </summary>
        public TimeSpan DecodedTime
        {
            get { return TimeSpan.FromSeconds((double)_decoders[_streamIdx].CurrentPosition / _decoders[_streamIdx]._sampleRate); }
            set
            {
                SeekTo((long)(value.TotalSeconds * SampleRate));
            }
        }

        /// <summary>
        /// Gets the total length of the current logical stream
        /// </summary>
        public TimeSpan TotalTime
        {
            get { return TimeSpan.FromSeconds((double)_packetProvider.GetLastGranulePos(_serials[_streamIdx]) / _decoders[_streamIdx]._sampleRate); }
        }
        
        #endregion
    }
}
