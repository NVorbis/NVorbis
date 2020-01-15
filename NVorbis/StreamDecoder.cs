using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NVorbis
{
    /// <summary>
    /// Implements a stream decoder for Vorbis data.
    /// </summary>
    public sealed class StreamDecoder : IStreamDecoder
    {
        static internal Func<IFactory> CreateFactory { get; set; } = () => new Factory();

        private IPacketProvider _packetProvider;
        private IFactory _factory;
        private StreamStats _stats;

        private byte _channels;
        private int _sampleRate;
        private int _block0Size;
        private int _block1Size;
        private IMode[] _modes;
        private int _modeFieldBits;

        private string _vendor;
        private string[] _comments;
        private ITagData _tags;

        private long _currentPosition;
        private bool _hasClipped;
        private bool _hasPosition;
        private bool _eosFound;

        private float[][] _nextPacketBuf;
        private float[][] _prevPacketBuf;
        private int _prevPacketStart;
        private int _prevPacketEnd;
        private int _prevPacketStop;

        private bool _isParameterChange;
        private bool _hasReadSampleRate;
        private bool _hasReadChannels;

        /// <summary>
        /// Creates a new instance of <see cref="StreamDecoder"/>.
        /// </summary>
        /// <param name="packetProvider">A <see cref="IPacketProvider"/> instance for the decoder to read from.</param>
        public StreamDecoder(IPacketProvider packetProvider)
            : this(packetProvider, new Factory())
        {
        }

        internal StreamDecoder(IPacketProvider packetProvider, IFactory factory)
        {
            _packetProvider = packetProvider ?? throw new ArgumentNullException(nameof(packetProvider));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));

            _stats = new StreamStats();

            _currentPosition = 0L;
            ClipSamples = true;

            if (!ProcessParameterChange(_packetProvider.PeekNextPacket(), true))
            {
                _packetProvider = null;
                throw new ArgumentException("Invalid Vorbis stream!", nameof(packetProvider));
            }
        }

        #region Init

        private bool ProcessParameterChange(IPacket packet, bool advanceToNextPacket)
        {
            var fullChange = false;
            var fullReset = false;
            if (ProcessStreamHeader(packet))
            {
                fullChange = true;
                if (advanceToNextPacket)
                {
                    _packetProvider.GetNextPacket().Done();
                }
                else
                {
                    packet.Done();
                    advanceToNextPacket = true;
                }
                packet = _packetProvider.PeekNextPacket();
                if (packet == null) throw new InvalidDataException("Couldn't get next packet!");
            }
            else
            {
                packet.Reset();
            }

            if (LoadComments(packet))
            {
                if (advanceToNextPacket)
                {
                    _packetProvider.GetNextPacket().Done();
                }
                else
                {
                    packet.Done();
                    advanceToNextPacket = true;
                }
                packet = _packetProvider.PeekNextPacket();
                if (packet == null) throw new InvalidDataException("Couldn't get next packet!");
            }
            else
            {
                packet.Reset();
            }

            if (LoadBooks(packet))
            {
                fullReset = true;
                if (advanceToNextPacket)
                {
                    _packetProvider.GetNextPacket().Done();
                }
                else
                {
                    packet.Done();
                }
                packet = _packetProvider.PeekNextPacket();
                if (packet == null) throw new InvalidDataException("Couldn't get next packet!");
            }
            else
            {
                packet.Reset();
            }

            if (fullChange && !fullReset)
            {
                throw new InvalidDataException("Got a new stream header, but no books!");
            }

            if (fullReset)
            {
                _currentPosition = 0;
                ResetDecoder();
                return true;
            }
            return false;
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
            _sampleRate = (int)packet.ReadBits(32);
            UpperBitrate = (int)packet.ReadBits(32);
            NominalBitrate = (int)packet.ReadBits(32);
            LowerBitrate = (int)packet.ReadBits(32);

            _block0Size = 1 << (int)packet.ReadBits(4);
            _block1Size = 1 << (int)packet.ReadBits(4);

            if (NominalBitrate == 0 && UpperBitrate > 0 && LowerBitrate > 0)
            {
                NominalBitrate = (UpperBitrate + LowerBitrate) / 2;
            }

            _stats.SetSampleRate(_sampleRate);
            _stats.AddPacket(-1, 0, packet.BitsRead + packet.BitsRemaining, packet.ContainerOverheadBits);

            return true;
        }

        private bool LoadComments(IPacket packet)
        {
            if (!ValidateHeader(packet, PacketSignatureComments))
            {
                return false;
            }

            _vendor = ReadString(packet);

            _comments = new string[packet.ReadBits(32)];
            for (var i = 0; i < _comments.Length; i++)
            {
                _comments[i] = ReadString(packet);
            }

            _stats.AddPacket(-1, 0, packet.BitsRead + packet.BitsRemaining, packet.ContainerOverheadBits);

            return true;
        }

        private bool LoadBooks(IPacket packet)
        {
            if (!ValidateHeader(packet, PacketSignatureBooks))
            {
                return false;
            }

            var mdct = _factory.CreateMdct();
            var huffman = _factory.CreateHuffman();

            // read the books
            var books = new ICodebook[packet.ReadBits(8) + 1];
            for (var i = 0; i < books.Length; i++)
            {
                books[i] = _factory.CreateCodebook();
                books[i].Init(packet, huffman);
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

            _stats.AddPacket(-1, 0, packet.BitsRead + packet.BitsRemaining, packet.ContainerOverheadBits);

            return true;
        }

        #endregion

        #region State Change

        private void ResetDecoder()
        {
            _prevPacketBuf = null;
            _prevPacketStart = 0;
            _prevPacketEnd = 0;
            _prevPacketStop = 0;
            _nextPacketBuf = null;
            _isParameterChange = false;
            _hasReadChannels = false;
            _hasReadSampleRate = false;
            _eosFound = false;
            _hasClipped = false;
            _hasPosition = false;
        }

        private void AckParameterChange(bool sampleRate = false, bool channels = false)
        {
            _hasReadSampleRate |= sampleRate;
            _hasReadChannels |= channels;

            if (_hasReadSampleRate && _hasReadChannels)
            {
                // both have been read, so clear the parameter change flag
                _isParameterChange = false;
            }
        }

        #endregion

        #region Decoding

        /// <summary>
        /// Reads samples into the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to read the samples into.</param>
        /// <param name="offset">The index to start reading samples into the buffer.</param>
        /// <param name="count">The number of samples that should be read into the buffer.  Must be a multiple of <see cref="Channels"/>.</param>
        /// <param name="isParameterChange"><see langword="true"/> if subsequent data will have a different <see cref="Channels"/> or <see cref="SampleRate"/>.</param>
        /// <returns>The number of samples read into the buffer.  If <paramref name="isParameterChange"/> is <see langword="true"/>, this will be <c>0</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the buffer is too small or <paramref name="offset"/> is less than zero.</exception>
        /// <remarks>The data populated into <paramref name="buffer"/> is interleaved by channel in normal PCM fashion: Left, Right, Left, Right, Left, Right</remarks>
        public int ReadSamples(float[] buffer, int offset, int count, out bool isParameterChange)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count % _channels != 0) throw new ArgumentOutOfRangeException(nameof(count), "Must be a multiple of Channels!");
            if (_packetProvider == null) throw new ObjectDisposedException(nameof(StreamDecoder));

            // by default, we're not in a parameter change
            isParameterChange = false;

            // if the caller didn't ask for any data, bail early
            if (count == 0)
            {
                return 0;
            }

            // save off value to track when we're done with the request
            var idx = offset;
            var tgt = offset + count;

            // try to fill the buffer; drain the last buffer if EOS, resync, bad packet, or parameter change
            while (idx < tgt)
            {
                // if we don't have any more valid data in the current packet, read in the next packet
                if (_prevPacketStart == _prevPacketEnd)
                {
                    // if we're in a parameter change or we're at the end of the file, we've read everything and need to reset
                    if (_isParameterChange)
                    {
                        // notify our caller of the parameter change state
                        isParameterChange = _isParameterChange;

                        // fully reset ourself
                        ResetDecoder();
                        _hasPosition = true;
                        break;
                    }
                    if (_eosFound)
                    {
                        _nextPacketBuf = null;
                        _prevPacketBuf = null;

                        // no more samples, so just return
                        break;
                    }

                    if (!ReadNextPacket((idx - offset) / _channels, out isParameterChange, out var samplePosition))
                    {
                        // save off the parameter change state...
                        _isParameterChange |= isParameterChange;

                        // ... but don't actually flag it to our caller just yet
                        isParameterChange = false;

                        // drain the current packet (the windowing will fade it out)
                        _prevPacketEnd = _prevPacketStop;
                    }

                    // if we need to pick up a position, and the packet had one, apply the position now
                    if (samplePosition.HasValue && !_hasPosition)
                    {
                        _hasPosition = true;
                        _currentPosition = samplePosition.Value - (_prevPacketEnd - _prevPacketStart) - (idx - offset) / _channels;
                    }
                }

                // we read out the valid samples from the previous packet
                var copyLen = Math.Min((tgt - idx) / _channels, _prevPacketEnd - _prevPacketStart);
                if (copyLen > 0)
                {
                    if (ClipSamples)
                    {
                        idx += ClippingCopyBuffer(buffer, idx, copyLen);
                    }
                    else
                    {
                        idx += CopyBuffer(buffer, idx, copyLen);
                    }
                }
            }

            // update the count of floats written
            count = idx - offset;

            // update the position
            _currentPosition += count / _channels;

            // return count of floats written
            return count;
        }

        private int ClippingCopyBuffer(float[] target, int targetIndex, int count)
        {
            var idx = targetIndex;
            for (; count > 0; _prevPacketStart++, count--)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    target[idx++] = Utils.ClipValue(_prevPacketBuf[ch][_prevPacketStart], ref _hasClipped);
                }
            }
            return idx - targetIndex;
        }

        private int CopyBuffer(float[] target, int targetIndex, int count)
        {
            var idx = targetIndex;
            for (; count > 0; _prevPacketStart++, count--)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    target[idx++] = _prevPacketBuf[ch][_prevPacketStart];
                }
            }
            return idx - targetIndex;
        }

        private bool ReadNextPacket(int bufferedSamples, out bool isParameterChange, out long? samplePosition)
        {
            // decode the next packet now so we can start overlapping with it
            var curPacket = DecodeNextPacket(out var startIndex, out var validLen, out var totalLen, out isParameterChange, out var isEndOfStream, out samplePosition, out var bitsRead, out var bitsRemaining, out var containerOverheadBits);
            _eosFound |= isEndOfStream;
            if (curPacket == null)
            {
                _stats.AddPacket(0, bitsRead, bitsRemaining, containerOverheadBits);
                return false;
            }

            // if we get a max sample position, back off our valid length to match
            if (samplePosition.HasValue && isEndOfStream)
            {
                var actualEnd = _currentPosition + bufferedSamples + validLen - startIndex;
                var diff = (int)(samplePosition.Value - actualEnd);
                if (diff < 0)
                {
                    validLen += diff;
                }
            }

            // start overlapping (if we don't have an previous packet data, just loop and the previous packet logic will handle things appropriately)
            if (_prevPacketEnd > 0)
            {
                // overlap the first samples in the packet with the previous packet, then loop
                OverlapBuffers(_prevPacketBuf, curPacket, _prevPacketStart, _prevPacketStop, startIndex, _channels);
                _prevPacketStart = startIndex;
            }
            else if (_prevPacketBuf == null)
            {
                // first packet, so it doesn't have any good data before the valid length
                _prevPacketStart = validLen;
            }

            // update stats
            _stats.AddPacket(validLen - _prevPacketStart, bitsRead, bitsRemaining, containerOverheadBits);

            // keep the old buffer so the GC doesn't have to reallocate every packet
            _nextPacketBuf = _prevPacketBuf;

            // save off our current packet's data for the next pass
            _prevPacketEnd = validLen;
            _prevPacketStop = totalLen;
            _prevPacketBuf = curPacket;

            isParameterChange = false;
            return true;
        }

        private float[][] DecodeNextPacket(out int packetStartindex, out int packetValidLength, out int packetTotalLength, out bool isParameterChange, out bool isEndOfStream, out long? samplePosition, out int bitsRead, out int bitsRemaining, out int containerOverheadBits)
        {
            IPacket packet = null;
            try
            {
                if ((packet = _packetProvider.GetNextPacket()) == null)
                {
                    // no packet? we're at the end of the stream
                    isParameterChange = false;
                    isEndOfStream = true;
                }
                else if (packet.IsParameterChange)
                {
                    // parameter change... hmmm...  process it, flag that we're changing, then try again next pass
                    isParameterChange = true;
                    isEndOfStream = false;
                    ProcessParameterChange(packet, false);
                }
                else
                {
                    isParameterChange = false;

                    // if the packet is flagged as the end of the stream, we can safely mark _eosFound
                    isEndOfStream = packet.IsEndOfStream;

                    // resync... that means we've probably lost some data; pick up a new position
                    if (packet.IsResync)
                    {
                        _hasPosition = false;
                    }

                    // grab the container overhead now, since the read won't affect it
                    containerOverheadBits = packet.ContainerOverheadBits;

                    // make sure the packet starts with a 0 bit as per the spec
                    if (packet.ReadBit())
                    {
                        bitsRemaining = packet.BitsRemaining + 1;
                    }
                    else
                    {
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
                            samplePosition = packet.GranulePosition;
                            bitsRead = packet.BitsRead;
                            bitsRemaining = packet.BitsRemaining;
                            return _nextPacketBuf;
                        }
                        bitsRemaining = packet.BitsRead + packet.BitsRemaining;
                    }
                }
                packetStartindex = 0;
                packetValidLength = 0;
                packetTotalLength = 0;
                samplePosition = null;
                bitsRead = 0;
                bitsRemaining = 0;
                containerOverheadBits = 0;
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

        /// <summary>
        /// Seeks the stream to the specified time.
        /// </summary>
        /// <param name="timePosition">The time to seek to.</param>
        public void SeekTo(TimeSpan timePosition)
        {
            SeekTo((long)(SampleRate * timePosition.TotalSeconds));
        }

        /// <summary>
        /// Seeks the stream to the specified sample.
        /// </summary>
        /// <param name="samplePosition">The sample position to seek to.</param>
        public void SeekTo(long samplePosition)
        {
            if (_packetProvider == null) throw new ObjectDisposedException(nameof(StreamDecoder));
            if (!_packetProvider.CanSeek) throw new InvalidOperationException("Seek is not supported by the IPacketProvider instance.");

            if (samplePosition < 0) throw new ArgumentOutOfRangeException(nameof(samplePosition));

            int rollForward;
            if (samplePosition == 0)
            {
                // short circuit for the looping case...
                _packetProvider.SeekTo(0, 0, GetPacketGranules);
                rollForward = 0;
            }
            else
            {
                // seek the stream to the correct position
                var pos = _packetProvider.SeekTo(samplePosition, 1, GetPacketGranules);
                rollForward = (int)(samplePosition - pos);
            }

            // clear out old data
            ResetDecoder();
            _hasPosition = true;

            // read the pre-roll packet
            if (!ReadNextPacket(0, out _, out _))
            {
                // we'll use this to force ReadSamples to fail to read
                _eosFound = true;
                throw new InvalidOperationException("Could not read pre-roll packet!  Try seeking again prior to reading more samples.");
            }

            // read the actual packet
            if (!ReadNextPacket(0, out _, out _))
            {
                ResetDecoder();
                // we'll use this to force ReadSamples to fail to read
                _eosFound = true;
                throw new InvalidOperationException("Could not read pre-roll packet!  Try seeking again prior to reading more samples.");
            }

            // adjust our indexes to match what we want
            _prevPacketStart += rollForward;
            _currentPosition = samplePosition;
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

        /// <summary>
        /// Cleans up this instance.
        /// </summary>
        public void Dispose()
        {
            _packetProvider = null;
        }

        #region Properties

        /// <summary>
        /// Gets the number of channels in the stream.
        /// </summary>
        public int Channels
        {
            get
            {
                AckParameterChange(channels: true);
                return _channels;
            }
        }

        /// <summary>
        /// Gets the sample rate of the stream.
        /// </summary>
        public int SampleRate
        {
            get
            {
                AckParameterChange(sampleRate: true);
                return _sampleRate;
            }
        }

        /// <summary>
        /// Gets the upper bitrate limit for the stream, if specified.
        /// </summary>
        public int UpperBitrate { get; private set; }

        /// <summary>
        /// Gets the nominal bitrate of the stream, if specified.  May be calculated from <see cref="LowerBitrate"/> and <see cref="UpperBitrate"/>.
        /// </summary>
        public int NominalBitrate { get; private set; }

        /// <summary>
        /// Gets the lower bitrate limit for the stream, if specified.
        /// </summary>
        public int LowerBitrate { get; private set; }

        /// <summary>
        /// Gets the tag data from the stream's header.
        /// </summary>
        public ITagData Tags => _tags ?? (_tags = new TagData(_vendor, _comments));

        /// <summary>
        /// Gets the total duration of the decoded stream.
        /// </summary>
        public TimeSpan TotalTime => TimeSpan.FromSeconds((double)TotalSamples / _sampleRate);

        /// <summary>
        /// Gets the total number of samples in the decoded stream.
        /// </summary>
        public long TotalSamples => _packetProvider?.GetGranuleCount() ?? throw new ObjectDisposedException(nameof(StreamDecoder));

        /// <summary>
        /// Gets or sets the current time position of the stream.
        /// </summary>
        public TimeSpan TimePosition
        {
            get => TimeSpan.FromSeconds((double)_currentPosition / _sampleRate);
            set => SeekTo(value);
        }

        /// <summary>
        /// Gets or sets the current sample position of the stream.
        /// </summary>
        public long SamplePosition
        {
            get => _currentPosition;
            set => SeekTo(value);
        }

        /// <summary>
        /// Gets or sets whether to clip samples returned by <see cref="ReadSamples(float[], int, int, out bool)"/>.
        /// </summary>
        public bool ClipSamples { get; set; }

        /// <summary>
        /// Gets whether <see cref="ReadSamples(float[], int, int, out bool)"/> has returned any clipped samples.
        /// </summary>
        public bool HasClipped => _hasClipped;

        /// <summary>
        /// Gets the <see cref="IStreamStats"/> instance for this stream.
        /// </summary>
        public IStreamStats Stats => _stats;

        #endregion
    }
}
