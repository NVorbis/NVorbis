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
        private float[][] _prevPacketBuf;
        private int _prevPacketStart;
        private int _prevPacketEnd;
        private int _prevPacketLen;
        private float[][] _nextPacketBuf;

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

            // track total count written to buffer
            var idx = offset;
            var tgt = offset + count;

            // read data from the output buffer until no more prepared data, done, or next packet is param change or resync
            while (idx < tgt && !isParameterChange && !_eosFound)
            {
                // try to read in the previous packet's available samples
                if (_prevPacketEnd > 0 && _prevPacketStart < _prevPacketEnd)
                {
                    for (; _prevPacketStart < _prevPacketEnd && idx < tgt; _prevPacketStart++)
                    {
                        for (var c = 0; c < _channels; c++)
                        {
                            buffer[idx++] = _prevPacketBuf[c][_prevPacketStart];
                        }
                    }
                }

                // if more needed, grab the next packet and start overlapping
                if (idx < tgt)
                {
                    // if we enter here, it's because we don't have any more ready samples in the previous packet

                    var curPacket = DecodeNextPacket(out var startIndex, out var validLen, out var totalLen, out isParameterChange);
                    if (curPacket == null)
                    {
                        _prevPacketBuf = null;
                        _prevPacketStart = 0;
                        _prevPacketEnd = 0;
                        _prevPacketLen = 0;
                        _isParameterChange |= isParameterChange;
                        break;
                    }

                    var i = startIndex;
                    if (_prevPacketEnd > 0)
                    {
                        // read out the overlapped samples from both packets
                        for (; _prevPacketStart < _prevPacketLen && i < validLen && idx < tgt; _prevPacketStart++, i++)
                        {
                            for (var c = 0; c < _channels; c++)
                            {
                                buffer[idx++] = _prevPacketBuf[c][_prevPacketStart] + curPacket[c][i];
                            }
                        }

                        // corner case: if we don't read enough samples to finish out the previous packet, we'll just update curPacket's data prior to moving it to previous
                        if (_prevPacketStart < _prevPacketLen)
                        {
                            for (var j = i; _prevPacketStart < _prevPacketLen; _prevPacketStart++, j++)
                            {
                                for (var c = 0; c < _channels; c++)
                                {
                                    curPacket[c][j] += _prevPacketBuf[c][_prevPacketStart];
                                }
                            }
                        }
                    }
                    else if (_prevPacketBuf == null)
                    {
                        // first in the series, so it doesn't have any good data before the valid length
                        i = validLen;
                    }

                    // keep the old buffer so the GC doesn't have to reallocate every packet
                    _nextPacketBuf = _prevPacketBuf;

                    // save off our current packet's data for the next pass
                    _prevPacketStart = i;
                    _prevPacketEnd = validLen;
                    _prevPacketLen = totalLen;
                    _prevPacketBuf = curPacket;
                }
            }

            count = idx - offset;

            _currentPosition += count / _channels;

            // if clipping enabled, clip samples
            if (ClipSamples)
            {
                _hasClipped = false;
                for (var i = 0; i < count; i++, offset++)
                {
                    buffer[offset] = Utils.ClipValue(buffer[offset], ref _hasClipped);
                }
            }

            // return count of sample written
            return count;
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
                    var _nextPacketBuf = new float[_channels][];
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
            if (curPacket.ReadBit()) return 0;
            if (lastPacket.ReadBit()) return 0;

            // get the current packet's information
            var modeIdx = (int)curPacket.ReadBits(_modeFieldBits);
            if (modeIdx < 0 || modeIdx >= _modes.Length) return 0;
            var mode = _modes[modeIdx];

            // get the last packet's information
            modeIdx = (int)lastPacket.ReadBits(_modeFieldBits);
            if (modeIdx < 0 || modeIdx >= _modes.Length) return 0;
            var prevMode = _modes[modeIdx];

            // now calculate the totals...
            return mode.BlockSize / 4 + prevMode.BlockSize / 4;
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
