using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NVorbis
{
    public sealed class StreamDecoder : IStreamDecoder
    {
        static internal Func<IFactory> CreateFactory { get; set; } = () => new Factory();

        private IFactory _factory;
        private IPacketProvider _packetProvider;
        private long _currentPosition;
        private IPacket _parameterChangePacket;
        private byte _channels;
        private int _block0Size;
        private int _block1Size;
        private string[] _comments;
        private IMode[] _modes;
        private bool _eosFound;
        private bool _isParameterChange;
        private bool _hasClipped;
        private int _modeFieldBits;
        private float[][] _nextPacketBuf;
        private float[][] _prevPacketBuf;
        private int _prevPacketStart;
        private int _prevPacketEnd;
        private int _prevPacketLen;

        public StreamDecoder(IPacketProvider packetProvider)
            : this(packetProvider, new Factory())
        {
        }

        internal StreamDecoder(IPacketProvider packetProvider, IFactory factory)
        {
            _packetProvider = packetProvider ?? throw new ArgumentNullException(nameof(packetProvider));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));

            _packetProvider.ParameterChangeCallback = SetParameterChangePacket;

            _currentPosition = 0L;

            if (!ProcessParameterChange())
            {
                _packetProvider.ParameterChangeCallback = null;
                _packetProvider = null;
                throw new ArgumentException("Invalid Vorbis stream!", nameof(packetProvider));
            }
        }

        #region Init

        private bool ProcessParameterChange()
        {
            _parameterChangePacket = null;

            var fullChange = false;
            var fullReset = false;
            var packet = _packetProvider.PeekNextPacket();
            if (ProcessStreamHeader(packet))
            {
                fullChange = true;
                _packetProvider.GetNextPacket().Done();
                packet = _packetProvider.PeekNextPacket();
                if (packet == null) throw new InvalidDataException("Couldn't get next packet!");
            }

            if (LoadComments(packet))
            {
                _packetProvider.GetNextPacket().Done();
                packet = _packetProvider.PeekNextPacket();
                if (packet == null) throw new InvalidDataException("Couldn't get next packet!");
            }

            if (LoadBooks(packet))
            {
                fullReset = true;
                _packetProvider.GetNextPacket().Done();
                packet = _packetProvider.PeekNextPacket();
                if (packet == null) throw new InvalidDataException("Couldn't get next packet!");
            }

            if (fullChange && !fullReset)
            {
                throw new InvalidDataException("Got a new stream header, but no books!");
            }

            ResetDecoder();

            return fullChange && fullReset;
        }

        static private readonly byte[] PacketSignatureStream = { 0x01, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73, 0x00, 0x00, 0x00, 0x00 };
        static private readonly byte[] PacketSignatureComments = { 0x03, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 };
        static private readonly byte[] PacketSignatureBooks = { 0x05, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73 };

        static private bool ValidateHeader(IPacket packet, byte[] expected)
        {
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != packet.ReadBits(8))
                {
                    return false;
                }
            }
            return true;
        }

        static private string ReadString(IPacket packet)
        {
            var len = (int)packet.ReadBits(32);
            var buf = new byte[len];
            var cnt = packet.Read(buf, 0, len);
            if (cnt < len)
            {
                throw new InvalidDataException("Could not read full string!");
            }
            return Encoding.UTF8.GetString(buf);
        }

        private bool ProcessStreamHeader(IPacket packet)
        {
            if (!ValidateHeader(packet, PacketSignatureStream))
            {
                return false;
            }

            _channels = (byte)packet.ReadBits(8);
            SampleRate = (int)packet.ReadBits(32);
            UpperBitrate = (int)packet.ReadBits(32);
            NominalBitrate = (int)packet.ReadBits(32);
            LowerBitrate = (int)packet.ReadBits(32);

            _block0Size = 1 << (int)packet.ReadBits(4);
            _block1Size = 1 << (int)packet.ReadBits(4);

            if (NominalBitrate == 0 && UpperBitrate > 0 && LowerBitrate > 0)
            {
                NominalBitrate = (UpperBitrate + LowerBitrate) / 2;
            }

            return true;
        }

        private bool LoadComments(IPacket packet)
        {
            if (!ValidateHeader(packet, PacketSignatureComments))
            {
                return false;
            }

            Vendor = ReadString(packet);

            _comments = new string[packet.ReadBits(32)];
            for (var i = 0; i < _comments.Length; i++)
            {
                _comments[i] = ReadString(packet);
            }

            return true;
        }

        private bool LoadBooks(IPacket packet)
        {
            if (!ValidateHeader(packet, PacketSignatureBooks))
            {
                return false;
            }

            var mdct = _factory.CreateMdct();

            // read the books
            var books = new ICodebook[packet.ReadBits(8) + 1];
            for (var i = 0; i < books.Length; i++)
            {
                books[i] = _factory.CreateCodebook();
                books[i].Init(packet);
            }

            // Vorbis never used this feature, so we just skip the appropriate number of bits
            var times = (int)packet.ReadBits(6) + 1;
            packet.SkipBits(16 * times);

            // read the floors
            var floors = new IFloor[packet.ReadBits(6) + 1];
            for (var i = 0; i < floors.Length; i++)
            {
                floors[i] = _factory.CreateFloor(packet);
                floors[i].Init(packet, _channels, _block0Size, _block1Size, books);
            }

            // read the residues
            var residues = new IResidue[packet.ReadBits(6) + 1];
            for (var i = 0; i < floors.Length; i++)
            {
                residues[i] = _factory.CreateResidue(packet);
                residues[i].Init(packet, _channels, books);
            }

            // read the mappings
            var mappings = new IMapping[packet.ReadBits(6) + 1];
            for (var i = 0; i < mappings.Length; i++)
            {
                mappings[i] = _factory.CreateMapping(packet);
                mappings[i].Init(packet, _channels, floors, residues, mdct);
            }

            // read the modes
            _modes = new IMode[packet.ReadBits(6) + 1];
            for (var i = 0; i < _modes.Length; i++)
            {
                _modes[i] = _factory.CreateMode();
                _modes[i].Init(packet, _channels, _block0Size, _block1Size, mappings);
            }

            // verify the closing bit
            if (!packet.ReadBit()) throw new InvalidDataException("Book packet did not end on correct bit!");

            // save off the number of bits to read to determine packet mode
            _modeFieldBits = Utils.ilog(_modes.Length - 1);

            return true;
        }

        #endregion

        #region State Change

        private void SetParameterChangePacket(IPacket packet)
        {
            _parameterChangePacket = packet ?? throw new ArgumentNullException(nameof(packet));
        }

        public void ClearParameterChange()
        {
            _isParameterChange = false;
        }

        private void ResetDecoder()
        {
            _prevPacketBuf = null;
            _prevPacketStart = 0;
            _prevPacketEnd = 0;
            _prevPacketLen = 0;
            _nextPacketBuf = null;
            _isParameterChange = false;
            _eosFound = false;
        }

        #endregion

        #region Decoding

        public int ReadSamples(float[] buffer, int offset, int count, out bool isParameterChange)
        {
            isParameterChange = _isParameterChange;

            // if the caller didn't ask for any data, or we're in a parameter change, return no data
            if (count == 0 || isParameterChange)
            {
                return 0;
            }

            // save off value to track when we're done with the request
            var idx = offset;
            var tgt = offset + count;

            // try to fill the buffer
            _hasClipped = false;
            while (idx < tgt)
            {
                // first we read out any valid samples from the previous packet
                var copyLen = Math.Min((tgt - idx) / _channels, _prevPacketEnd - _prevPacketStart);
                if (copyLen > 0)
                {
                    if (ClipSamples)
                    {
                        idx += ClippingCopyBuffer(_prevPacketBuf, ref _prevPacketStart, buffer, ref idx, copyLen, _channels, ref _hasClipped);
                    }
                    else
                    {
                        idx += CopyBuffer(_prevPacketBuf, ref _prevPacketStart, buffer, ref idx, copyLen, _channels);
                    }
                }

                // if we're in a parameter change, no more data should be decoded until the caller acks
                if (isParameterChange)
                {
                    break;
                }

                // if we've found the end of the stream, no more data is available
                if (_eosFound)
                {
                    break;
                }

                // then we grab the next packet, but only if we actually need more samples
                if (idx < tgt)
                {
                    // decode the next packet now so we can start overlapping with it
                    var curPacket = DecodeNextPacket(out var startIndex, out var validLen, out var totalLen, out isParameterChange, out var maxSamplePosition);
                    if (curPacket == null)
                    {
                        ResetDecoder();
                        _isParameterChange |= isParameterChange;
                        break;
                    }

                    // if we get a max sample position, back off our valid length to match
                    if (maxSamplePosition.HasValue)
                    {
                        var actualEnd = _currentPosition + (idx - offset) / _channels + validLen - startIndex;
                        var diff = (int)(maxSamplePosition.Value - actualEnd);
                        if (diff < 0)
                        {
                            validLen += diff;
                        }
                    }

                    // start overlapping (if we don't have an previous packet data, just loop and the previous packet logic will handle things appropriately)
                    if (_prevPacketEnd > 0)
                    {
                        // overlap the first samples in the packet with the previous packet, then loop
                        OverlapBuffers(_prevPacketBuf, curPacket, _prevPacketStart, _prevPacketLen, startIndex, _channels);
                        _prevPacketStart = startIndex;
                    }
                    else if (_prevPacketBuf == null)
                    {
                        // first packet, so it doesn't have any good data before the valid length
                        _prevPacketStart = validLen;
                    }

                    // keep the old buffer so the GC doesn't have to reallocate every packet
                    _nextPacketBuf = _prevPacketBuf;

                    // save off our current packet's data for the next pass
                    _prevPacketEnd = validLen;
                    _prevPacketLen = totalLen;
                    _prevPacketBuf = curPacket;
                }
            }

            count = idx - offset;

            _currentPosition += count / _channels;

            // return count of samples written
            return count;
        }

        private static int ClippingCopyBuffer(float[][] source, ref int sourceIndex, float[] target, ref int targetIndex, int count, int channels, ref bool hasClipped)
        {
            var endIndex = sourceIndex + count;
            for (; sourceIndex < endIndex; sourceIndex++)
            {
                for (var ch = 0; ch < channels; ch++)
                {
                    target[targetIndex++] = Utils.ClipValue(source[ch][sourceIndex], ref hasClipped);
                }
            }
            return count * channels;
        }

        private static int CopyBuffer(float[][] source, ref int sourceIndex, float[] target, ref int targetIndex, int count, int channels)
        {
            var endIndex = sourceIndex + count;
            for (; sourceIndex < endIndex; sourceIndex++)
            {
                for (var ch = 0; ch < channels; ch++)
                {
                    target[targetIndex++] = source[ch][sourceIndex];
                }
            }
            return count * channels;
        }

        private float[][] DecodeNextPacket(out int packetStartindex, out int packetValidLength, out int packetTotalLength, out bool isParameterChange, out long? maxSamplePosition)
        {
            packetStartindex = 0;
            packetValidLength = 0;
            packetTotalLength = 0;
            isParameterChange = false;
            maxSamplePosition = null;

            IPacket packet = null;
            try
            {
                if ((packet = _packetProvider.GetNextPacket()) == null)
                {
                    // no packet? we're at the end of the stream
                    _eosFound = true;
                    return null;
                }

                if (packet == _parameterChangePacket)
                {
                    // parameter change... hmmm...  process it, flag that we're changing, then try again next pass
                    _packetProvider.SeekToPacket(packet, 0);
                    isParameterChange = true;
                    ProcessParameterChange();
                    return null;
                }

                if (packet.IsResync)
                {
                    // crap, we lost sync...  reset the decoder and try again next pass
                    ResetDecoder();
                    return null;
                }

                // if the packet is flagged as the end of the stream, we can safely mark _eosFound
                if (packet.IsEndOfStream)
                {
                    _eosFound = true;
                }

                // make sure the packet starts with a 0 bit as per the spec
                if (packet.ReadBit())
                {
                    return null;
                }

                // if we get here, we should have a good packet; decode it and add it to the buffer
                var mode = _modes[(int)packet.ReadBits(_modeFieldBits)];
                if (_nextPacketBuf == null)
                {
                    _nextPacketBuf = new float[_channels][];
                    for (var i = 0; i < _channels; i++)
                    {
                        _nextPacketBuf[i] = new float[_block1Size];
                    }
                }
                if (mode.Decode(packet, _nextPacketBuf, out packetStartindex, out packetValidLength, out packetTotalLength))
                {
                    // per the spec, do not decode more samples than the last granulePosition
                    if (packet.IsEndOfStream && packet.PageGranulePosition > 0)
                    {
                        maxSamplePosition = packet.PageGranulePosition;
                    }
                    return _nextPacketBuf;
                }
                return null;
            }
            finally
            {
                packet?.Done();
            }
        }

        private static void OverlapBuffers(float[][] previous, float[][] next, int prevStart, int prevLen, int nextStart, int channels)
        {
            for (; prevStart < prevLen; prevStart++, nextStart++)
            {
                for (var c = 0; c < channels; c++)
                {
                    next[c][nextStart] += previous[c][prevStart];
                }
            }
        }

        #endregion

        #region Seeking

        public void SeekTo(TimeSpan timePosition)
        {
            SeekTo((long)(SampleRate * timePosition.TotalSeconds));
        }

        public void SeekTo(long samplePosition)
        {
            if (samplePosition < 0) throw new ArgumentOutOfRangeException(nameof(samplePosition));

            if (samplePosition == 0)
            {
                // short circuit for the looping case...
                _packetProvider.SeekToPacket(_packetProvider.GetPacket(3), 0);
                _currentPosition = 0;
                ResetDecoder();
            }
            else
            {
                // gotta actually look for the correct packet...
                var packet = _packetProvider.FindPacket(samplePosition, GetPacketGranules);
                if (packet == null)
                {
                    throw new ArgumentOutOfRangeException(nameof(samplePosition));
                }

                // update our position to match the actual position at the end of the previous packet
                // this works because we'll never decode anything prior to the current packet's samples
                _currentPosition = packet.GranulePosition - GetPacketGranules(packet, false);

                // if we did a seek to within the first audio packet returning data, _currentPosition might be negative
                if (_currentPosition < 0)
                {
                    // so the fix is to seek to the very first audio packet and allow the logic below to accomplish the rest of the seek
                    packet.Done();
                    packet = _packetProvider.GetPacket(4);

                    _currentPosition = 0;
                }

                // seek the stream to the packet _before_ the found packet
                _packetProvider.SeekToPacket(packet, 1);

                // make sure we reset everything
                packet.Done();
                ResetDecoder();

                // now decode samples until we're in exactly the right spot...
                var cnt = (int)((samplePosition - SamplePosition) * _channels);
                if (cnt > 0)
                {
                    var seekBuffer = new float[cnt];
                    while (cnt > 0)
                    {
                        var temp = ReadSamples(seekBuffer, 0, cnt, out _);
                        if (temp == 0) break;   // we're at the end...
                        cnt -= temp;
                    }
                }
            }
        }

        private int GetPacketGranules(IPacket curPacket, bool isFirst)
        {
            // if it's a resync, there's not any audio data to return
            if (curPacket.IsResync) return 0;

            // if it's not an audio packet, there's no audio data (seems obvious, though...)
            if (curPacket.ReadBit()) return 0;

            // OK, let's ask the appropriate mode how long this packet actually is

            // first we need to know which mode...
            var modeIdx = (int)curPacket.ReadBits(_modeFieldBits);

            // if we got an invalid mode value, we can't decode any audio data anyway...
            if (modeIdx < 0 || modeIdx >= _modes.Length) return 0;

            return _modes[modeIdx].GetPacketSampleCount(curPacket, isFirst);
        }

        #endregion

        public void Dispose()
        {
            _packetProvider?.Dispose();
            _packetProvider = null;
        }

        #region Properties

        public int Channels => _channels;

        public int SampleRate { get; private set; }

        public int UpperBitrate { get; private set; }

        public int NominalBitrate { get; private set; }

        public int LowerBitrate { get; private set; }

        public string Vendor { get; private set; }

        public IReadOnlyList<string> Comments => _comments;

        public TimeSpan TotalTime => TimeSpan.FromSeconds((double)TotalSamples / SampleRate);

        public long TotalSamples => _packetProvider.GetGranuleCount();

        public TimeSpan TimePosition
        {
            get => TimeSpan.FromSeconds((double)_currentPosition / SampleRate);
            set => SeekTo(value);
        }
        public long SamplePosition
        {
            get => _currentPosition;
            set => SeekTo(value);
        }
        public bool ClipSamples { get; set; }

        public bool HasClipped => _hasClipped;

        #endregion
    }
}
