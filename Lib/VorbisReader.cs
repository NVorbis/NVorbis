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
        public int Channels { get { return _channels; } }
        public int SampleRate { get; private set; }
        public int UpperBitrate { get; private set; }
        public int NominalBitrate { get; private set; }
        public int LowerBitrate { get; private set; }

        public string Vendor { get; private set; }
        public string[] Comments { get; private set; }

        internal int _channels;
        internal int Block0Size;
        internal int Block1Size;

        internal VorbisCodebook[] Books;
        internal VorbisTime[] Times;
        internal VorbisFloor[] Floors;
        internal VorbisResidue[] Residues;
        internal VorbisMapping[] Maps;
        internal VorbisMode[] Modes;

        int _modeFieldBits;

        OggContainerReader _reader;
        int _streamIdx, _streamSerial;

        public VorbisStreamStatus Stats { get; private set; }

        internal long _glueBits;
        internal long _metaBits;
        internal long _bookBits;
        internal long _timeHdrBits;
        internal long _floorHdrBits;
        internal long _resHdrBits;
        internal long _mapHdrBits;
        internal long _modeHdrBits;
        internal long _wasteHdrBits;

        internal long _modeBits;
        internal long _floorBits;
        internal long _resBits;
        internal long _wasteBits;

        internal int _packetCount;
        internal System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        public VorbisReader(string fileName)
        {
            _reader = new OggContainerReader(fileName);
            _streamIdx = -1;

            while (!InitNextStream(false))
            {
                if (!_reader.FindNextStream(_streamSerial))
                {
                    throw new InvalidDataException("Could not find Vorbis stream in file!");
                }
            }

            Stats = new VorbisStreamStatus(_reader, this);
        }

        public void Dispose()
        {
            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }
        }

        bool InitNextStream(bool checkProperties)
        {
            int channels = _channels;
            int rate = SampleRate;

            // increment the stream index
            ++_streamIdx;

            // get the next stream's serialNumber
            if (_streamIdx >= _reader.StreamSerials.Length) return false;
            _streamSerial = _reader.StreamSerials[_streamIdx];

            // get the first packet of the stream...
            var packet = _reader.GetNextPacket(_streamSerial);

            // make sure it's a vorbis stream...
            if (!packet.ReadBytes(7).SequenceEqual(new byte[] { 0x01, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 }))
            {
                _glueBits += packet.Length * 8;
                return false;
            }

            _glueBits += 56;

            try
            {
                // now load the initial header
                ProcessStreamHeader(packet);

                // finally, load the comment and book headers...
                bool comments = false, books = false;
                while (!(comments & books))
                {
                    packet = _reader.GetNextPacket(_streamSerial);
                    if (packet.IsResync) throw new InvalidDataException("Missing header packets!");

                    switch (packet.PeekByte())
                    {
                        case 1: throw new InvalidDataException("Found second init header!");
                        case 3: LoadComments(packet); comments = true; break;
                        case 5: LoadBooks(packet); books = true; break;
                    }
                }
            }
            catch (InvalidDataException)
            {
                // todo: log the error reason

                return false;
            }

            if (checkProperties)
            {
                if (_channels != channels || rate != SampleRate)
                {
                    throw new InvalidDataException("Next stream setup incompatible!");
                }
            }

            InitDecoder();

            return true;
        }

        #region Header Decode

        void ProcessStreamHeader(OggPacket packet)
        {
            var startPos = packet.BitsRead;

            if (packet.ReadInt32() != 0) throw new InvalidDataException("Only Vorbis stream version 0 is supported.");

            _channels = packet.ReadByte();
            SampleRate = packet.ReadInt32();
            UpperBitrate = packet.ReadInt32();
            NominalBitrate = packet.ReadInt32();
            LowerBitrate = packet.ReadInt32();

            Block0Size = 1 << (int)packet.ReadBits(4);
            Block1Size = 1 << (int)packet.ReadBits(4);

            if (NominalBitrate == 0)
            {
                if (UpperBitrate > 0 && LowerBitrate > 0)
                {
                    NominalBitrate = (UpperBitrate + LowerBitrate) / 2;
                }
            }

            _metaBits += packet.BitsRead - startPos + 8;

            _wasteHdrBits += 8 * packet.Length - packet.BitsRead;
        }

        void LoadComments(OggPacket packet)
        {
            packet.SkipBits(8);
            if (!packet.ReadBytes(6).SequenceEqual(new byte[] { 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 })) throw new InvalidDataException("Corrupted comment header!");

            _glueBits += 56;

            Vendor = Encoding.UTF8.GetString(packet.ReadBytes(packet.ReadInt32()));

            Comments = new string[packet.ReadInt32()];
            for (int i = 0; i < Comments.Length; i++)
            {
                Comments[i] = Encoding.UTF8.GetString(packet.ReadBytes(packet.ReadInt32()));
            }

            _metaBits += packet.BitsRead - 56;
            _wasteHdrBits += 8 * packet.Length - packet.BitsRead;
        }

        void LoadBooks(OggPacket packet)
        {
            packet.SkipBits(8);
            if (!packet.ReadBytes(6).SequenceEqual(new byte[] { 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 })) throw new InvalidDataException("Corrupted book header!");

            var bits = packet.BitsRead;

            _glueBits += packet.BitsRead;

            // get books
            Books = new VorbisCodebook[packet.ReadByte() + 1];
            for (int i = 0; i < Books.Length; i++)
            {
                Books[i] = VorbisCodebook.Init(this, packet, i);
            }

            _bookBits += packet.BitsRead - bits;
            bits = packet.BitsRead;

            // get times
            Times = new VorbisTime[(int)packet.ReadBits(6) + 1];
            for (int i = 0; i < Times.Length; i++)
            {
                Times[i] = VorbisTime.Init(this, packet);
            }

            _timeHdrBits += packet.BitsRead - bits;
            bits = packet.BitsRead;

            // get floor
            Floors = new VorbisFloor[(int)packet.ReadBits(6) + 1];
            for (int i = 0; i < Floors.Length; i++)
            {
                Floors[i] = VorbisFloor.Init(this, packet);
            }

            _floorHdrBits += packet.BitsRead - bits;
            bits = packet.BitsRead;

            // get residue
            Residues = new VorbisResidue[(int)packet.ReadBits(6) + 1];
            for (int i = 0; i < Residues.Length; i++)
            {
                Residues[i] = VorbisResidue.Init(this, packet);
            }

            _resHdrBits += packet.BitsRead - bits;
            bits = packet.BitsRead;

            // get map
            Maps = new VorbisMapping[(int)packet.ReadBits(6) + 1];
            for (int i = 0; i < Maps.Length; i++)
            {
                Maps[i] = VorbisMapping.Init(this, packet);
            }

            _mapHdrBits += packet.BitsRead - bits;
            bits = packet.BitsRead;

            // get mode settings
            Modes = new VorbisMode[(int)packet.ReadBits(6) + 1];
            for (int i = 0; i < Modes.Length; i++)
            {
                Modes[i] = VorbisMode.Init(this, packet);
            }

            _modeHdrBits += packet.BitsRead - bits;

            // check the framing bit
            if (!packet.ReadBit()) throw new InvalidDataException();

            ++_glueBits;

            _wasteHdrBits += 8 * packet.Length - packet.BitsRead;

            _modeFieldBits = Utils.ilog(Modes.Length - 1);
        }
        
        #endregion

        #region Stream Decode

        float[] _prevBuffer;
        RingBuffer<float> _outputBuffer;
        int _lastBlockSize;
        int _preparedLength;
        int _lastCenter;

        Stack<OggPacket> _resyncQueue;

        long _currentPosition;

        void InitDecoder()
        {
            if (_outputBuffer != null)
            {
                SaveBuffer();
            }

            _outputBuffer = new RingBuffer<float>(Block1Size * 2 * _channels);
            _outputBuffer.Channels = _channels;

            _preparedLength = 0;
            _lastCenter = Block0Size / -2;
            _lastBlockSize = Block0Size;
            _currentPosition = 0L;

            _resyncQueue = new Stack<OggPacket>();
        }

        void ResetDecoder()
        {
            // this is called when the decoder encounters a "hiccup" in the data stream...

            // save off the existing "good" data
            SaveBuffer();
            _outputBuffer.Clear();
            _preparedLength = 0;
            _lastCenter = Block0Size / -2;
            _lastBlockSize = Block0Size;
        }

        void SaveBuffer()
        {
            var buf = ACache.Get<float>(_preparedLength * _channels, false);
            ReadSamples(buf, 0, buf.Length);
            _prevBuffer = buf;
        }

        void DecodeNextPacket()
        {
            _sw.Start();
            ACache.BeginScope();

            try
            {
                // get the next packet
                var packet = _reader.GetNextPacket(_streamSerial);

                // check for resync
                if (packet.IsResync)
                {
                    ResetDecoder(); // if we're a resync, our current decoder state is invalid...
                }

                #region Packet Read

                // make sure we're on an audio packet
                if (packet.ReadBit())
                {
                    // we really can't do anything... count the bits as "glue"
                    _wasteBits += packet.Length * 8;
                    return;
                }
                ++_glueBits;

                VorbisMode mode;
                bool prevFlag = false, nextFlag = false;
                var modeBits = _modeFieldBits;
                try
                {
                    mode = Modes[(int)packet.ReadBits(_modeFieldBits)];
                    if (mode.BlockFlag)
                    {
                        prevFlag = packet.ReadBit();
                        nextFlag = packet.ReadBit();
                        modeBits += 2;
                    }
                }
                catch (EndOfStreamException)
                {
                    // we really can't do anything... count the bits as "glue"
                    --_glueBits;
                    _wasteBits += packet.Length * 8;
                    return;
                }
                _modeBits += modeBits;

                var floors = ACache.Get<float[]>(_channels);
                var residue = ACache.Get<float>(_channels, mode.BlockSize);
                long floorBits, resBits;
                try
                {
                    var startBits = packet.BitsRead;

                    // get the noise floors
                    for (int i = 0; i < floors.Length; i++)
                    {
                        var submap = mode.Mapping.ChannelSubmap[i];
                        floors[i] = submap.Floor.DecodePacket(packet, mode.BlockSize);
                    }

                    floorBits = packet.BitsRead - startBits;
                    startBits = packet.BitsRead;

                    // check that we have energy in all coupled channels
                    foreach (var step in mode.Mapping.CouplingSteps)
                    {
                        if (floors[step.Angle] == null || floors[step.Magnitude] == null)
                        {
                            floors[step.Angle] = null;
                            floors[step.Magnitude] = null;
                        }
                    }

                    // get the residue
                    foreach (var subMap in mode.Mapping.Submaps)
                    {
                        var ch = 0;
                        for (int j = 0; j < _channels; j++)
                        {
                            if (mode.Mapping.ChannelSubmap[j] == subMap)
                            {
                                // the spec says to tweak a "do_not_encode_flag", but we have that in floors already

                                ++ch;
                            }
                        }

                        var rTemp = subMap.Residue.Decode(packet, floors.Select(f => f == null).ToArray(), ch, mode.BlockSize);
                        for (int c = 0; c < _channels; c++)
                        {
                            var r = residue[c];
                            var rt = rTemp[c];
                            for (int i = 0; i < mode.BlockSize; i++)
                            {
                                r[i] += rt[i];
                            }
                        }
                        ACache.Return(ref rTemp);
                    }

                    resBits = packet.BitsRead - startBits;
                }
                catch (EndOfStreamException)
                {
                    --_glueBits;
                    _wasteBits += packet.Length * 8;
                    ResetDecoder();
                    return;
                }
                catch (InvalidDataException)
                {
                    --_glueBits;
                    _wasteBits += packet.Length * 8;
                    return;
                }

                _floorBits += floorBits;
                _resBits += resBits;
                _wasteBits += 8 * packet.Length - packet.BitsRead;

                #endregion

                ++_packetCount;

                // we can now safely decode all the data without having to worry about a corrupt or partial packet

                #region Packet Decode

                // inverse coupling
                var steps = mode.Mapping.CouplingSteps;
                for (int i = steps.Length - 1; i >= 0; i--)
                {
                    var magnitude = residue[steps[i].Magnitude];
                    var angle = residue[steps[i].Angle];

                    for (int j = 0; j < mode.BlockSize; j++)
                    {
                        float newM, newA;

                        if (magnitude[j] > 0)
                        {
                            if (angle[j] > 0)
                            {
                                newM = magnitude[j];
                                newA = magnitude[j] - angle[j];
                            }
                            else
                            {
                                newA = magnitude[j];
                                newM = magnitude[j] + angle[j];
                            }
                        }
                        else
                        {
                            if (angle[j] > 0)
                            {
                                newM = magnitude[j];
                                newA = magnitude[j] + angle[j];
                            }
                            else
                            {
                                newA = magnitude[j];
                                newM = magnitude[j] - angle[j];
                            }
                        }

                        magnitude[j] = newM;
                        angle[j] = newA;
                    }
                }

                // dot product
                for (int c = 0; c < _channels; c++)
                {
                    var res = residue[c];
                    var floor = floors[c];
                    if (floor != null)
                    {
                        for (int i = 0; i < mode.BlockSize; i++)
                        {
                            res[i] *= floor[i];
                        }
                    }
                    else
                    {
                        Array.Clear(res, 0, mode.BlockSize);
                    }
                }

                // inverse MDCT
                var pcm = ACache.Get<float[]>(_channels);
                for (int c = 0; c < _channels; c++)
                {
                    // pcm[c] will equal residue[c]!
                    pcm[c] = Mdct.Reverse(residue[c]);
                }

                // window
                var window = mode.GetWindow(prevFlag, nextFlag);
                // this is applied as part of the lapping operation

                // lap
                var sizelW = _lastBlockSize;
                var sizeW = mode.BlockSize;
                var _centerW = _lastCenter + sizelW / 4 + sizeW / 4;
                var beginW = _centerW - sizeW / 2;
                var endW = beginW + sizeW;
                int beginSl = 0;
                int endSl = Block0Size / 2;

                if (mode.BlockFlag)
                {
                    beginSl = sizeW / 4 - sizelW / 4;
                    endSl = beginSl + sizelW / 2;
                }
                else
                {
                    beginSl = 0;
                    endSl = Block0Size / 2;
                }

                for (int j = 0; j < _channels; j++)
                {
                    var pcmChan = pcm[j];

                    int i = beginSl, _pcm = beginW;
                    for (; i < endSl; i++)
                    {
                        _outputBuffer[j, _pcm + i] = Math.Max(Math.Min(_outputBuffer[j, _pcm + i] + pcmChan[i] * window[i], 1f), -1f);
                    }
                    for (; i < sizeW; i++)
                    {
                        _outputBuffer[j, _pcm + i] = pcmChan[i] * window[i];
                    }
                }

                _preparedLength = _centerW;
                _lastBlockSize = sizeW;

                #endregion

                #region Position Update

                if (packet.IsResync)
                {
                    // during a resync, we have to go through and watch for the next "marker"
                    _currentPosition = -packet.PageGranulePosition;
                    // _currentPosition will now be end of the page...  wait for the value to change, then go back and repopulate the granule positions accordingly...
                    _resyncQueue.Push(packet);
                }
                else
                {
                    if (_lastCenter != Block0Size / -2)
                    {
                        _currentPosition += _centerW - _lastCenter;
                        packet.GranulePosition = _currentPosition;

                        if (_currentPosition < 0)
                        {
                            if (packet.PageGranulePosition > -_currentPosition)
                            {
                                // we now have a valid granuleposition...  populate the queued packets' GranulePositions
                                var gp = _currentPosition - (_centerW - _lastCenter);
                                while (_resyncQueue.Count > 0)
                                {
                                    var pkt = _resyncQueue.Pop();

                                    var temp = pkt.GranulePosition + gp;
                                    pkt.GranulePosition = gp;
                                    gp = temp;
                                }
                            }
                            else
                            {
                                packet.GranulePosition = -(_centerW - _lastCenter);
                                _resyncQueue.Push(packet);
                            }
                        }
                        else if (packet.IsEndOfStream && packet.PageGranulePosition > _currentPosition)
                        {
                            _preparedLength -= (int)(_currentPosition - packet.PageGranulePosition);
                            packet.GranulePosition = packet.PageGranulePosition;
                        }
                    }
                }

                #endregion

                _lastCenter = _centerW;
            }
            finally
            {
                ACache.EndScope();
                _sw.Stop();
            }
        }
        
        #endregion

        #region Public Interface

        public int ReadSamples(float[] buffer, int offset, int count)
        {
            int samplesRead = 0;

            if (_prevBuffer != null)
            {
                // get samples from the previous buffer's data
                var cnt = Math.Min(count, _prevBuffer.Length);
                Buffer.BlockCopy(_prevBuffer, 0, buffer, offset, cnt * sizeof(float));

                // if we have samples left over, rebuild the previous buffer array...
                if (cnt < _prevBuffer.Length)
                {
                    var buf = ACache.Get<float>(_prevBuffer.Length - cnt, false);
                    Buffer.BlockCopy(_prevBuffer, cnt * sizeof(float), buf, 0, (_prevBuffer.Length - cnt) * sizeof(float));
                    ACache.Return(ref _prevBuffer);
                    _prevBuffer = buf;
                }

                // reduce the desired sample count & increase the desired sample offset
                count -= cnt;
                offset += cnt;
                samplesRead = cnt;
            }

            int minSize = count + Block1Size * _channels;
            _outputBuffer.EnsureSize(minSize);

            while (_preparedLength * _channels < count)
            {
                try
                {
                    DecodeNextPacket();
                }
                catch (EndOfStreamException)
                {
                    break;
                }
            }

            if (_preparedLength * _channels < count)
            {
                // we can safely assume we've read the last packet...
                count = _preparedLength * _channels;
            }

            _outputBuffer.CopyTo(buffer, offset, count);
            _preparedLength -= count / _channels;
            _lastCenter -= count / _channels;

            return samplesRead + count;
        }

        // TODO: Uncomment this when adding support for stream switching
        //public int StreamCount
        //{
        //    get { return _reader.StreamSerials.Length; }
        //}

        public TimeSpan DecodedTime
        {
            get { return TimeSpan.FromSeconds((double)(_currentPosition - _preparedLength) / SampleRate); }
            set
            {
                // get the sample number to look for...
                var sampleNum = (int)(value.TotalSeconds * SampleRate);

                _reader.SeekToSample(_streamSerial, sampleNum);

                ResetDecoder();
                ACache.Return(ref _prevBuffer); // we aren't keeping it...
            }
        }

        public TimeSpan TotalTime
        {
            get { return TimeSpan.FromSeconds((double)_reader.GetLastGranulePos(_streamSerial) / SampleRate); }
        }
        
        #endregion
    }
}
