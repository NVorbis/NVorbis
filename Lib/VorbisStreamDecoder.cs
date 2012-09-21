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
    class VorbisStreamDecoder : IVorbisStreamStatus
    {
        static internal byte InitialPacketMarker { get { return (byte)1; } }

        internal int _upperBitrate;
        internal int _nominalBitrate;
        internal int _lowerBitrate;

        internal string _vendor;
        internal string[] _comments;

        internal int _channels;
        internal int _sampleRate;
        internal int Block0Size;
        internal int Block1Size;

        internal VorbisCodebook[] Books;
        internal VorbisTime[] Times;
        internal VorbisFloor[] Floors;
        internal VorbisResidue[] Residues;
        internal VorbisMapping[] Maps;
        internal VorbisMode[] Modes;

        int _modeFieldBits;

        #region Stat Fields

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

        internal long _samples;

        internal int _packetCount;

        internal System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        #endregion

        Func<DataPacket> _getNextPacket;
        Func<int> _getTotalPages;

        List<int> _pagesSeen;
        int _lastPageSeen;

        bool _eosFound;

        internal VorbisStreamDecoder(Func<DataPacket> getNextPacket, Func<int> getTotalPages)
        {
            _getNextPacket = getNextPacket;
            _getTotalPages = getTotalPages;

            _pagesSeen = new List<int>();
            _lastPageSeen = -1;
        }

        internal bool TryInit(DataPacket initialPacket)
        {
            // make sure it's a vorbis stream...
            if (!initialPacket.ReadBytes(7).SequenceEqual(new byte[] { 0x01, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 }))
            {
                _glueBits += initialPacket.Length * 8;
                return false;
            }

            _glueBits += 56;

            // now load the initial header
            ProcessStreamHeader(initialPacket);

            // finally, load the comment and book headers...
            bool comments = false, books = false;
            while (!(comments & books))
            {
                var packet = _getNextPacket();
                if (packet.IsResync) throw new InvalidDataException("Missing header packets!");

                if (!_pagesSeen.Contains(packet.PageSequenceNumber)) _pagesSeen.Add(packet.PageSequenceNumber);

                switch (packet.PeekByte())
                {
                    case 1: throw new InvalidDataException("Found second init header!");
                    case 3: LoadComments(packet); comments = true; break;
                    case 5: LoadBooks(packet); books = true; break;
                }
            }

            InitDecoder();

            return true;
        }

        #region Header Decode

        void ProcessStreamHeader(DataPacket packet)
        {
            _pagesSeen.Add(packet.PageSequenceNumber);

            var startPos = packet.BitsRead;

            if (packet.ReadInt32() != 0) throw new InvalidDataException("Only Vorbis stream version 0 is supported.");

            _channels = packet.ReadByte();
            _sampleRate = packet.ReadInt32();
            _upperBitrate = packet.ReadInt32();
            _nominalBitrate = packet.ReadInt32();
            _lowerBitrate = packet.ReadInt32();

            Block0Size = 1 << (int)packet.ReadBits(4);
            Block1Size = 1 << (int)packet.ReadBits(4);

            if (_nominalBitrate == 0)
            {
                if (_upperBitrate > 0 && _lowerBitrate > 0)
                {
                    _nominalBitrate = (_upperBitrate + _lowerBitrate) / 2;
                }
            }

            _metaBits += packet.BitsRead - startPos + 8;

            _wasteHdrBits += 8 * packet.Length - packet.BitsRead;
        }

        void LoadComments(DataPacket packet)
        {
            packet.SkipBits(8);
            if (!packet.ReadBytes(6).SequenceEqual(new byte[] { 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 })) throw new InvalidDataException("Corrupted comment header!");

            _glueBits += 56;

            _vendor = Encoding.UTF8.GetString(packet.ReadBytes(packet.ReadInt32()));

            _comments = new string[packet.ReadInt32()];
            for (int i = 0; i < _comments.Length; i++)
            {
                _comments[i] = Encoding.UTF8.GetString(packet.ReadBytes(packet.ReadInt32()));
            }

            _metaBits += packet.BitsRead - 56;
            _wasteHdrBits += 8 * packet.Length - packet.BitsRead;
        }

        void LoadBooks(DataPacket packet)
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

        #region Data Decode

        float[] _prevBuffer;
        RingBuffer<float> _outputBuffer;
        Queue<int> _bitsPerPacketHistory;
        Queue<int> _sampleCountHistory;
        int _preparedLength;
        bool _clipped = false;

        Stack<DataPacket> _resyncQueue;

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
            _currentPosition = 0L;

            _resyncQueue = new Stack<DataPacket>();

            _bitsPerPacketHistory = new Queue<int>();
            _sampleCountHistory = new Queue<int>();
        }

        void ResetDecoder()
        {
            // this is called when the decoder encounters a "hiccup" in the data stream...
            // it is also called when a seek happens

            // save off the existing "good" data
            SaveBuffer();
            _outputBuffer.Clear();
            _preparedLength = 0;
        }

        void SaveBuffer()
        {
            var buf = ACache.Get<float>(_preparedLength * _channels, false);
            ReadSamples(buf, 0, buf.Length);
            _prevBuffer = buf;
        }

        class PacketDecodeInfo
        {
            public VorbisMode Mode;
            public bool PrevFlag;
            public bool NextFlag;
            public VorbisFloor.PacketData[] FloorData;
            public float[][] Residue;
        }

        PacketDecodeInfo UnpackPacket(DataPacket packet)
        {
            // make sure we're on an audio packet
            if (packet.ReadBit())
            {
                // we really can't do anything... count the bits as waste
                return null;
            }

            var pdi = new PacketDecodeInfo();

            // get mode and prev/next flags
            var modeBits = _modeFieldBits;
            try
            {
                pdi.Mode = Modes[(int)packet.ReadBits(_modeFieldBits)];
                if (pdi.Mode.BlockFlag)
                {
                    pdi.PrevFlag = packet.ReadBit();
                    pdi.NextFlag = packet.ReadBit();
                    modeBits += 2;
                }
            }
            catch (EndOfStreamException)
            {
                return null;
            }

            try
            {
                var startBits = packet.BitsRead;

                // read the noise floor data (but don't decode yet)
                pdi.FloorData = ACache.Get<VorbisFloor.PacketData>(_channels);
                var noExecuteChannel = ACache.Get<bool>(_channels);
                for (int i = 0; i < _channels; i++)
                {
                    pdi.FloorData[i] = pdi.Mode.Mapping.ChannelSubmap[i].Floor.UnpackPacket(packet, pdi.Mode.BlockSize);
                    noExecuteChannel[i] = !pdi.FloorData[i].ExecuteChannel;
                }

                // make sure we handle no-energy channels correctly given the couplings...
                foreach (var step in pdi.Mode.Mapping.CouplingSteps)
                {
                    if (pdi.FloorData[step.Angle].ExecuteChannel || pdi.FloorData[step.Magnitude].ExecuteChannel)
                    {
                        pdi.FloorData[step.Angle].ForceEnergy = true;
                        pdi.FloorData[step.Magnitude].ForceEnergy = true;
                    }
                }

                var floorBits = packet.BitsRead - startBits;
                startBits = packet.BitsRead;

                pdi.Residue = ACache.Get<float>(_channels, pdi.Mode.BlockSize);
                foreach (var subMap in pdi.Mode.Mapping.Submaps)
                {
                    for (int j = 0; j < _channels; j++)
                    {
                        if (pdi.Mode.Mapping.ChannelSubmap[j] != subMap)
                        {
                            pdi.FloorData[j].ForceNoEnergy = true;
                        }
                    }

                    var rTemp = subMap.Residue.Decode(packet, noExecuteChannel, _channels, pdi.Mode.BlockSize);
                    for (int c = 0; c < _channels; c++)
                    {
                        var r = pdi.Residue[c];
                        var rt = rTemp[c];
                        for (int i = 0; i < pdi.Mode.BlockSize; i++)
                        {
                            r[i] += rt[i];
                        }
                    }
                    ACache.Return(ref rTemp);
                }
                ACache.Return(ref noExecuteChannel);

                _glueBits += 1;
                _modeBits += modeBits;
                _floorBits += floorBits;
                _resBits += packet.BitsRead - startBits;
                _wasteBits += 8 * packet.Length - packet.BitsRead;

                _packetCount += 1;
            }
            catch (EndOfStreamException)
            {
                ResetDecoder();
                pdi = null;
            }
            catch (InvalidDataException)
            {
                pdi = null;
            }

            return pdi;
        }

        int DecodePacket(PacketDecodeInfo pdi)
        {
            var sizeW = pdi.Mode.BlockSize;

            // inverse coupling
            var steps = pdi.Mode.Mapping.CouplingSteps;
            for (int i = steps.Length - 1; i >= 0; i--)
            {
                var magnitude = pdi.Residue[steps[i].Magnitude];
                var angle = pdi.Residue[steps[i].Angle];

                // we only have to do the first half; MDCT ignores the last half
                for (int j = 0; j < sizeW / 2; j++)
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

            // apply floor / dot product / MDCT (only run if we have sound energy in that channel)
            var pcm = ACache.Get<float[]>(_channels);
            for (int c = 0; c < _channels; c++)
            {
                var floorData = pdi.FloorData[c];
                var res = pdi.Residue[c];
                if (floorData.ExecuteChannel)
                {
                    pdi.Mode.Mapping.ChannelSubmap[c].Floor.Apply(floorData, res);
                    pcm[c] = Mdct.Reverse(res);
                }
                else
                {
                    // Mdct.Reverse does an in-place transform, then returns the input buffer... mimic that
                    pcm[c] = res;
                }
            }

            // window
            var window = pdi.Mode.GetWindow(pdi.PrevFlag, pdi.NextFlag);
            // this is applied as part of the lapping operation

            // now lap the data into the buffer...
            
            // var sizeW = pdi.Mode.BlockSize
            var right = sizeW;
            var center = right >> 1;
            var left = 0;
            var begin = -center;
            var end = center;

            if (pdi.Mode.BlockFlag)
            {
                // if the flag is true, it's a long block
                // if the flag is false, it's a short block
                if (!pdi.PrevFlag)
                {
                    // previous block was short
                    left = Block1Size / 4 - Block0Size / 4;  // where to start in pcm[][]
                    center = left + Block0Size / 2;     // adjust the center so we're correctly clearing the buffer...
                    begin = Block0Size / -2 - left;     // where to start in _outputBuffer[,]
                }

                if (!pdi.NextFlag)
                {
                    // next block is short
                    right -= sizeW / 4 - Block0Size / 4;
                    end = sizeW / 4 + Block0Size / 4;
                }
            }
            // short blocks don't need any adjustments

            var lastLength = _outputBuffer.Length / _channels;
            for (var c = 0; c < _channels; c++)
            {
                var pcmChan = pcm[c];
                int i = left, idx = lastLength + begin;
                for (; i < center; i++)
                {
                    // add the new windowed value to the appropriate buffer index.  clamp to range -1 to 1 and set _clipped appropriately
                    _outputBuffer[c, idx + i] = Utils.ClipValue(_outputBuffer[c, idx + i] + pcmChan[i] * window[i], ref _clipped);
                }
                for (; i < right; i++)
                {
                    _outputBuffer[c, idx + i] = pcmChan[i] * window[i];
                }
            }

            var newPrepLen = _outputBuffer.Length / _channels - end;
            var samplesDecoded = newPrepLen - _preparedLength;
            _preparedLength = newPrepLen;

            return samplesDecoded;
        }

        void UpdatePosition(int samplesDecoded, DataPacket packet)
        {
            _samples += samplesDecoded;

            if (packet.IsResync)
            {
                // during a resync, we have to go through and watch for the next "marker"
                _currentPosition = -packet.PageGranulePosition;
                // _currentPosition will now be end of the page...  wait for the value to change, then go back and repopulate the granule positions accordingly...
                _resyncQueue.Push(packet);
            }
            else
            {
                if (samplesDecoded > 0)
                {
                    _currentPosition += samplesDecoded;
                    packet.GranulePosition = _currentPosition;

                    if (_currentPosition < 0)
                    {
                        if (packet.PageGranulePosition > -_currentPosition)
                        {
                            // we now have a valid granuleposition...  populate the queued packets' GranulePositions
                            var gp = _currentPosition - samplesDecoded;
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
                            packet.GranulePosition = -samplesDecoded;
                            _resyncQueue.Push(packet);
                        }
                    }
                    else if (packet.IsEndOfStream && _currentPosition > packet.PageGranulePosition)
                    {
                        var diff = (int)(_currentPosition - packet.PageGranulePosition);
                        if (diff >= 0)
                        {
                            _preparedLength -= diff;
                            _currentPosition -= diff;
                        }
                        else
                        {
                            // uh-oh.  We're supposed to have more samples to this point...
                            _preparedLength = 0;
                        }
                        packet.GranulePosition = packet.PageGranulePosition;
                        _eosFound = true;
                    }
                }
            }
        }

        void DecodeNextPacket()
        {
            _sw.Start();
            ACache.BeginScope();

            try
            {
                // get the next packet
                var packet = _getNextPacket();

                // if the packet is null, our packet reader is gone...
                if (packet == null)
                {
                    _eosFound = true;
                    return;
                }

                // keep our page count in sync
                if (!_pagesSeen.Contains((_lastPageSeen = packet.PageSequenceNumber))) _pagesSeen.Add(_lastPageSeen);

                // check for resync
                if (packet.IsResync)
                {
                    ResetDecoder(); // if we're a resync, our current decoder state is invalid...
                }

                var pdi = UnpackPacket(packet);
                if (pdi == null)
                {
                    _wasteBits += 8 * packet.Length;
                    return;
                }

                // we can now safely decode all the data without having to worry about a corrupt or partial packet

                var samplesDecoded = DecodePacket(pdi);

                // we can do something cool here...  mark down how many samples were decoded in this packet
                if (packet.GranuleCount.HasValue == false)
                {
                    packet.GranuleCount = samplesDecoded;
                }

                // update our position

                UpdatePosition(samplesDecoded, packet);

                // a little statistical housekeeping...
                var sc = Utils.Sum(_sampleCountHistory) + samplesDecoded;

                _bitsPerPacketHistory.Enqueue((int)packet.BitsRead);
                _sampleCountHistory.Enqueue(samplesDecoded);

                while (sc > _sampleRate)
                {
                    _bitsPerPacketHistory.Dequeue();
                    sc -= _sampleCountHistory.Dequeue();
                }
            }
            finally
            {
                ACache.EndScope();
                _sw.Stop();
            }
        }

        internal int GetPacketLength(DataPacket curPacket, DataPacket lastPacket)
        {
            // if we don't have a previous packet, or we're re-syncing, this packet has no audio data to return
            if (lastPacket == null || curPacket.IsResync) return 0;

            // make sure they are audio packets
            if (curPacket.ReadBit()) return 0;
            if (lastPacket.ReadBit()) return 0;

            // get the current packet's information
            var modeIdx = (int)curPacket.ReadBits(_modeFieldBits);
            if (modeIdx < 0 || modeIdx >= Modes.Length) return 0;
            var mode = Modes[modeIdx];

            // get the last packet's information
            modeIdx = (int)lastPacket.ReadBits(_modeFieldBits);
            if (modeIdx < 0 || modeIdx >= Modes.Length) return 0;
            var prevMode = Modes[modeIdx];

            // now calculate the totals...
            return mode.BlockSize / 4 + prevMode.BlockSize / 4;
        }
        
        #endregion

        internal int ReadSamples(float[] buffer, int offset, int count)
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

            while (_preparedLength * _channels < count && !_eosFound)
            {
                try
                {
                    DecodeNextPacket();
                }
                catch (EndOfStreamException)
                {
                    _eosFound = true;
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

            return samplesRead + count;
        }

        internal long CurrentPosition
        {
            get { return _currentPosition - _preparedLength; }
            set
            {
                _currentPosition = value;
                _preparedLength = 0;
                _eosFound = false;

                ResetDecoder();
                ACache.Return(ref _prevBuffer);
            }
        }

        public void ResetStats()
        {
            // only reset the stream info...  don't mess with the container, book, and hdr bits...

            _clipped = false;
            _packetCount = 0;
            _floorBits = 0L;
            _glueBits = 0L;
            _modeBits = 0L;
            _resBits = 0L;
            _wasteBits = 0L;
            _samples = 0L;
            _sw.Reset();
        }

        public int EffectiveBitRate
        {
            get
            {
                if (_samples == 0L) return 0;

                var decodedSeconds = (double)(_currentPosition - _preparedLength) / _sampleRate;

                return (int)(AudioBits / decodedSeconds);
            }
        }

        public int InstantBitRate
        {
            get
            {
                try
                {
                    return (int)((long)_bitsPerPacketHistory.Sum() * _sampleRate / _sampleCountHistory.Sum());
                }
                catch (DivideByZeroException)
                {
                    return -1;
                }
            }
        }

        public TimeSpan PageLatency
        {
            get
            {
                return TimeSpan.FromTicks(_sw.ElapsedTicks / PagesRead);
            }
        }

        public TimeSpan PacketLatency
        {
            get
            {
                return TimeSpan.FromTicks(_sw.ElapsedTicks / _packetCount);
            }
        }

        public TimeSpan SecondLatency
        {
            get
            {
                return TimeSpan.FromTicks((_sw.ElapsedTicks / _samples) * _sampleRate);
            }
        }

        public long OverheadBits
        {
            get
            {
                return _glueBits + _metaBits + _timeHdrBits + _wasteHdrBits + _wasteBits;
            }
        }

        public long AudioBits
        {
            get
            {
                return _bookBits + _floorHdrBits + _resHdrBits + _mapHdrBits + _modeHdrBits + _modeBits + _floorBits + _resBits;
            }
        }

        public int PagesRead
        {
            get { return _pagesSeen.IndexOf(_lastPageSeen) + 1; }
        }

        public int TotalPages
        {
            get { return _getTotalPages(); }
        }

        public bool Clipped
        {
            get { return _clipped; }
        }
    }
}
