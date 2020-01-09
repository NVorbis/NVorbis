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
            if (!TryInit())
            {
                throw new ArgumentException("Could not initialize decoder!", nameof(packetProvider));
            }
        }

        internal StreamDecoder(IPacketProvider packetProvider, IFactory factory)
        {
            _packetProvider = packetProvider ?? throw new ArgumentNullException(nameof(packetProvider));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));

            _packetProvider.ParameterChangeCallback = SetParameterChangePacket;
        }

        internal bool TryInit()
        {
            _currentPosition = 0L;

            return ProcessParameterChange();
        }

        private void SetParameterChangePacket(IPacket packet)
        {
            _parameterChangePacket = packet ?? throw new ArgumentNullException(nameof(packet));
        }

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

        private void ResetDecoder()
        {
            _prevPacketBuf = null;
            _prevPacketStart = 0;
            _prevPacketEnd = 0;
            _prevPacketLen = 0;
            _nextPacketBuf = null;
            _isParameterChange = false;
        }

        public void ClearParameterChange()
        {
            _isParameterChange = false;
        }

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
            while (idx < tgt && !isParameterChange && !_eosFound)
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

                // then we grab the next packet, but only if we actually need more samples
                if (idx < tgt)
                {
                    // decode the next packet now so we can start overlapping with it
                    var curPacket = DecodeNextPacket(out var startIndex, out var validLen, out var totalLen, out isParameterChange);
                    if (curPacket == null)
                    {
                        ResetDecoder();
                        _isParameterChange |= isParameterChange;
                        break;
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

        private float[][] DecodeNextPacket(out int packetStartindex, out int packetValidLength, out int packetTotalLength, out bool isParameterChange)
        {
            packetStartindex = 0;
            packetValidLength = 0;
            packetTotalLength = 0;
            isParameterChange = false;

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

        public void SeekTo(TimeSpan timePosition)
        {
            SeekTo((long)(SampleRate * timePosition.TotalSeconds));
        }

        public void SeekTo(long samplePosition)
        {
            if (samplePosition < 0) throw new ArgumentOutOfRangeException(nameof(samplePosition));

            IPacket packet;
            if (samplePosition > 0)
            {
                packet = _packetProvider.FindPacket(samplePosition, GetPacketLength);
                if (packet == null)
                {
                    throw new ArgumentOutOfRangeException(nameof(samplePosition));
                }
            }
            else
            {
                packet = _packetProvider.GetPacket(4);
            }

            // seek the stream
            _packetProvider.SeekToPacket(packet, 1);

            // now figure out where we are and how many samples we need to discard...
            // note that we use the granule position of the "current" packet, since it will be discarded no matter what

            // get the packet that we'll decode next
            var lastPacket = _packetProvider.PeekNextPacket();

            // now read samples until we are exactly at the granule position requested
            if (samplePosition > 0)
            {
                // we had to seek to somewhere beyond the first packet with audio data, so figure out what that granule position would be
                // packet.GranulePosition will be set from _packetProvider.FindPacket(...)
                _currentPosition = packet.GranulePosition - GetPacketLength(packet, lastPacket);
            }
            else
            {
                // we merely did a seek to the first data packet, so the granule position is by definition 0
                _currentPosition = 0;
            }
            //_preparedLength = 0;
            _eosFound = false;

            // make sure we reset everything
            lastPacket.Done();
            packet.Done();
            ResetDecoder();

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

        private int GetPacketLength(IPacket curPacket, IPacket lastPacket)
        {
            // if we don't have a previous packet, or we're re-syncing, this packet has no audio data to return
            if (lastPacket == null || curPacket.IsResync) return 0;

            // make sure they are audio packets
            if (curPacket.ReadBit() || lastPacket.ReadBit()) return 0;

            // get the current packet's information
            var modeIdx = (int)curPacket.ReadBits(_modeFieldBits);
            if (modeIdx < 0 || modeIdx >= _modes.Length) return 0;  // invalid mode, so by definition there's not any audio data here
            var mode = _modes[modeIdx];

            // ask the mode to calculate the length for us
            return mode.GetPacketSampleCount(curPacket);
        }

        public void Dispose()
        {
            _packetProvider?.Dispose();
            _packetProvider = null;
        }

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
    }
}
