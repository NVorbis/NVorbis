using NVorbis.Contracts;
using System;
using System.IO;
using System.Text;

namespace NVorbis
{
    /// <summary>
    /// Implements a stream decoder for Vorbis data.
    /// </summary>
    public sealed class StreamDecoder : IStreamDecoder
    {
        private Contracts.IPacketProvider _packetProvider;
        private readonly IFactory _factory;
        private readonly StreamStats _stats;

        private byte _channels;
        private int _sampleRate;
        private int _block0Size;
        private int _block1Size;
        private IMode[] _modes;
        private int _modeFieldBits;

        private string _vendor;
        private string[] _comments;
        private readonly Lazy<ITagData> _tags;

        private long _currentPosition;
        private readonly long _granuleOffset;
        private bool _hasClipped;
        private bool _hasPosition;
        private bool _eosFound;

        private float[][] _nextPacketBuf;
        private float[][] _prevPacketBuf;
        private int _prevPacketStart;
        private int _prevPacketEnd;
        private int _prevPacketStop;

        /// <summary>
        /// Creates a new instance of <see cref="StreamDecoder"/>.
        /// </summary>
        /// <param name="packetProvider">A <see cref="Contracts.IPacketProvider"/> instance for the decoder to read from.</param>
        public StreamDecoder(Contracts.IPacketProvider packetProvider)
            : this(packetProvider, new Factory())
        {
        }

        internal StreamDecoder(Contracts.IPacketProvider packetProvider, IFactory factory)
        {
            _packetProvider = packetProvider ?? throw new ArgumentNullException(nameof(packetProvider));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));

            _stats = new StreamStats();

            _currentPosition = 0L;
            ClipSamples = true;

            var packet = _packetProvider.PeekNextPacket();
            if (!ProcessHeaderPackets(packet))
            {
                _packetProvider = null;
                packet.Reset();

                throw GetInvalidStreamException(packet);
            }

            if (_packetProvider.CanSeek)
            {
                try
                {
                    // Find where the stream's granule timeline starts.  Normally that's 0, but a
                    // stream cut or captured mid-timeline starts later (issue #35); all positions
                    // we report are normalized so the first decodable sample is position 0.
                    // The provider is already positioned at the first audio packet, so this seek
                    // doesn't change any state we care about.
                    _granuleOffset = _packetProvider.SeekTo(0, 0, GetPacketGranules);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // no audio pages; nothing to normalize
                }
            }

            _tags = new Lazy<ITagData>(() => new TagData(_vendor, _comments));
        }

        private static Exception GetInvalidStreamException(IPacket packet)
        {
            try
            {
                // let's give our caller some helpful hints about what they've encountered...
                var header = packet.ReadBits(64);
                if (header == 0x646165487375704ful)
                {
                    return new ArgumentException("Found OPUS bitstream.");
                }
                else if ((header & 0xFF) == 0x7F)
                {
                    return new ArgumentException("Found FLAC bitstream.");
                }
                else if (header == 0x2020207865657053ul)
                {
                    return new ArgumentException("Found Speex bitstream.");
                }
                else if (header == 0x0064616568736966ul)
                {
                    // ugh...  we need to add support for this in the container reader
                    return new ArgumentException("Found Skeleton metadata bitstream.");
                }
                else if ((header & 0xFFFFFFFFFFFF00ul) == 0x61726f65687400ul)
                {
                    return new ArgumentException("Found Theora bitsream.");
                }
                return new ArgumentException("Could not find Vorbis data to decode.");
            }
            finally
            {
                packet.Reset();
            }
        }

        #region Init

        private bool ProcessHeaderPackets(IPacket packet)
        {
            if (!ProcessHeaderPacket(packet, LoadStreamHeader, _ => _packetProvider.GetNextPacket().Done()))
            {
                return false;
            }

            if (!ProcessHeaderPacket(_packetProvider.GetNextPacket(), LoadComments, pkt => pkt.Done()))
            {
                return false;
            }

            if (!ProcessHeaderPacket(_packetProvider.GetNextPacket(), LoadBooks, pkt => pkt.Done()))
            {
                return false;
            }

            ResetDecoder(); // also clears _currentPosition
            return true;
        }

        private static bool ProcessHeaderPacket(IPacket packet, Func<IPacket, bool> processAction, Action<IPacket> doneAction)
        {
            if (packet != null)
            {
                try
                {
                    return processAction(packet);
                }
                finally
                {
                    doneAction(packet);
                }
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

            if(len == 0)
            {
                return string.Empty;
            }
            
            var buf = new byte[len];
            var cnt = packet.Read(buf, 0, len);
            if (cnt < len)
            {
                throw new InvalidDataException("Could not read full string!");
            }
            return Encoding.UTF8.GetString(buf);
        }

        private bool LoadStreamHeader(IPacket packet)
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

            // Vorbis I spec §4.2.2: block sizes must be between 64 and 8192 with
            // blocksize[0] <= blocksize[1]; the stream is undecodable otherwise.
            // Mdct also relies on this range.
            if (_block0Size < 64 || _block1Size < _block0Size || _block1Size > 8192)
            {
                return false;
            }

            if (NominalBitrate == 0 && UpperBitrate > 0 && LowerBitrate > 0)
            {
                NominalBitrate = (UpperBitrate + LowerBitrate) / 2;
            }

            _stats.SetSampleRate(_sampleRate);
            _stats.AddPacket(-1, packet.BitsRead, packet.BitsRemaining, packet.ContainerOverheadBits);

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

            _stats.AddPacket(-1, packet.BitsRead, packet.BitsRemaining, packet.ContainerOverheadBits);

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
            for (var i = 0; i < times; i++) packet.SkipBits(16);

            // read the floors
            var floors = new IFloor[packet.ReadBits(6) + 1];
            for (var i = 0; i < floors.Length; i++)
            {
                floors[i] = _factory.CreateFloor(packet);
                floors[i].Init(packet, _channels, _block0Size, _block1Size, books);
            }

            // read the residues
            var residues = new IResidue[packet.ReadBits(6) + 1];
            for (var i = 0; i < residues.Length; i++)
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

            _stats.AddPacket(-1, packet.BitsRead, packet.BitsRemaining, packet.ContainerOverheadBits);

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
            _eosFound = false;
            _hasClipped = false;
            _hasPosition = false;
            // Clear the stale output position.  SeekTo() calls ResetDecoder() before reading its
            // pre-roll/seek packets and only assigns _currentPosition afterward.  ReadNextPacket's
            // end-of-stream valid-length backoff (see below) uses _currentPosition; if it still held
            // the position left over from a previous decode, the backoff would compute a bogus
            // negative valid length on a near-end re-seek -- returning zero samples (and, before the
            // Read() guard, spinning forever).  Every caller sets _currentPosition right before or
            // right after this reset, so clearing it here is safe.
            _currentPosition = 0;
        }

        #endregion

        #region Decoding

        /// <summary>
        /// Reads samples into the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to read the samples into.</param>
        /// <returns>The number of samples read into the buffer.</returns>
        /// <remarks>The data populated into <paramref name="buffer"/> is interleaved by channel in normal PCM fashion: Left, Right, Left, Right, Left, Right</remarks>
        public int Read(Span<float> buffer)
        {
            if (_packetProvider == null) throw new ObjectDisposedException(nameof(StreamDecoder));

            // if the caller didn't ask for any data, bail early
            if (buffer.IsEmpty)
            {
                return 0;
            }

            int count = 0;
            while (buffer.Length >= _channels)
            {
                // If we don't have any more valid data in the current packet, read in the next packet.
                // Use ">=" rather than "==": a seek near the end of the stream can leave the decode
                // state with _prevPacketEnd < _prevPacketStart (a negative valid length).  With "=="
                // that state would never re-enter this block to refill, yet copyLen below would be
                // non-positive, so neither branch makes progress and Read() spins forever (issue #40).
                // Treating "no valid samples available" (start >= end) the same as "exhausted" routes
                // the degenerate state into the EOS/refill handling and the loop terminates.
                if (_prevPacketStart >= _prevPacketEnd)
                {
                    if (_eosFound)
                    {
                        _nextPacketBuf = null;
                        _prevPacketBuf = null;

                        // no more samples, so just return
                        break;
                    }

                    if (!ReadNextPacket(count / _channels, out var samplePosition))
                    {
                        // Drain the current packet (the windowing will fade it out). Left
                        // unclamped, this can emit past the stream's stated end when EOS wasn't
                        // flagged during decode (HasAllPages was still false, so the normal EOS
                        // valid-length backoff in ReadNextPacket never ran) -- making the total
                        // emitted length depend on whether TotalSamples was queried first. Clamp
                        // the drain to what's actually left. GetGranuleCount() (via TotalSamples)
                        // is cheap here: reaching this branch means the provider already hit EOF,
                        // so HasAllPages is already true and no extra I/O is triggered.
                        long drainSpan = _prevPacketStop - _prevPacketStart;
                        if (_packetProvider.CanSeek)
                        {
                            var allowed = TotalSamples - (_currentPosition + count / _channels);
                            drainSpan = Math.Min(drainSpan, Math.Max(0, allowed));
                        }
                        _prevPacketEnd = _prevPacketStart + (int)drainSpan;
                    }

                    // If we need to pick up a position, and the packet had one, apply it now.
                    // _granuleOffset translates the stream's timeline (which doesn't necessarily
                    // start at zero -- issue #35) to the 0-based positions we report.
                    if (samplePosition.HasValue && !_hasPosition)
                    {
                        _hasPosition = true;
                        _currentPosition = samplePosition.Value - _granuleOffset - (_prevPacketEnd - _prevPacketStart) - count / _channels;
                    }
                }

                // we read out the valid samples from the previous packet
                var copyLen = Math.Min(buffer.Length / _channels, _prevPacketEnd - _prevPacketStart) * _channels;
                if (copyLen > 0)
                {
                    int written;
                    if (ClipSamples)
                    {
                        written = ClippingCopyBuffer(buffer.Slice(0, copyLen));
                    }
                    else
                    {
                        written = CopyBuffer(buffer.Slice(0, copyLen));
                    }
                    count += written;
                    buffer = buffer.Slice(written);
                }
            }

            // update the position
            _currentPosition += count / _channels;

            // return count of floats written
            return count;
        }

        /// <summary>
        /// Reads samples into the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to read the samples into.</param>
        /// <param name="offset">The index to start reading samples into the buffer.</param>
        /// <param name="count">The number of samples that should be read into the buffer.  Must be a multiple of <see cref="Channels"/>.</param>
        /// <returns>The number of samples read into the buffer.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the buffer is too small or <paramref name="offset"/> is less than zero.</exception>
        /// <remarks>The data populated into <paramref name="buffer"/> is interleaved by channel in normal PCM fashion: Left, Right, Left, Right, Left, Right</remarks>
        [Obsolete("Use Read(Span<float>) instead.")]
        public int Read(Span<float> buffer, int offset, int count)
        {
            if (offset < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count % _channels != 0) throw new ArgumentOutOfRangeException(nameof(count), "Must be a multiple of Channels!");
            return Read(buffer.Slice(offset, count));
        }

        private int ClippingCopyBuffer(Span<float> target)
        {
            var idx = 0;
            while (idx < target.Length)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    target[idx++] = Utils.ClipValue(_prevPacketBuf[ch][_prevPacketStart], ref _hasClipped);
                }
                ++_prevPacketStart;
            }
            return idx;
        }

        private int CopyBuffer(Span<float> target)
        {
            var idx = 0;
            while (idx < target.Length)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    target[idx++] = _prevPacketBuf[ch][_prevPacketStart];
                }
                ++_prevPacketStart;
            }
            return idx;
        }

        private bool ReadNextPacket(int bufferedSamples, out long? samplePosition)
        {
            // decode the next packet now so we can start overlapping with it
            var curPacket = DecodeNextPacket(out var startIndex, out var validLen, out var totalLen, out var isEndOfStream, out samplePosition, out var bitsRead, out var bitsRemaining, out var containerOverheadBits);
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
                var diff = (int)(samplePosition.Value - _granuleOffset - actualEnd);
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
            return true;
        }

        private float[][] DecodeNextPacket(out int packetStartindex, out int packetValidLength, out int packetTotalLength, out bool isEndOfStream, out long? samplePosition, out int bitsRead, out int bitsRemaining, out int containerOverheadBits)
        {
            // initialize the outputs up front so the bad/short/non-audio packet paths can report real
            // bit counts to the stats without being clobbered by a trailing reset block
            packetStartindex = 0;
            packetValidLength = 0;
            packetTotalLength = 0;
            isEndOfStream = false;
            samplePosition = null;
            bitsRead = 0;
            bitsRemaining = 0;
            containerOverheadBits = 0;

            IPacket packet = null;
            try
            {
                if ((packet = _packetProvider.GetNextPacket()) == null)
                {
                    // no packet? we're at the end of the stream
                    isEndOfStream = true;
                }
                else
                {
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
        /// Seeks the stream by the specified duration.
        /// </summary>
        /// <param name="timePosition">The relative time to seek to.</param>
        /// <param name="seekOrigin">The reference point used to obtain the new position.</param>
        public void SeekTo(TimeSpan timePosition, SeekOrigin seekOrigin = SeekOrigin.Begin)
        {
            SeekTo((long)(SampleRate * timePosition.TotalSeconds), seekOrigin);
        }

        /// <summary>
        /// Seeks the stream by the specified sample count.
        /// </summary>
        /// <param name="samplePosition">The relative sample position to seek to.</param>
        /// <param name="seekOrigin">The reference point used to obtain the new position.</param>
        public void SeekTo(long samplePosition, SeekOrigin seekOrigin = SeekOrigin.Begin)
        {
            if (_packetProvider == null) throw new ObjectDisposedException(nameof(StreamDecoder));
            if (!_packetProvider.CanSeek) throw new InvalidOperationException("Seek is not supported by the Contracts.IPacketProvider instance.");

            switch (seekOrigin)
            {
                case SeekOrigin.Begin:
                    // no-op
                    break;
                case SeekOrigin.Current:
                    samplePosition = SamplePosition + samplePosition;
                    break;
                case SeekOrigin.End:
                    samplePosition = TotalSamples - samplePosition;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(seekOrigin));
            }

            if (samplePosition < 0) throw new ArgumentOutOfRangeException(nameof(samplePosition));

            // clear out old data
            ResetDecoder();
            _hasPosition = true;

            long pos;
            int rollForward;
            try
            {
                // Seek to the packet whose granule range contains samplePosition, pre-rolling
                // one packet back so the MDCT overlap is valid.  PacketProvider.SeekTo clamps
                // to the first audio packet when samplePosition falls on the first data page
                // (covers position 0 and the first ~block_size samples generically).
                // _granuleOffset translates our 0-based position onto the stream's timeline.
                pos = _packetProvider.SeekTo(samplePosition + _granuleOffset, 1, GetPacketGranules);
                rollForward = (int)(samplePosition + _granuleOffset - pos);
            }
            catch (ArgumentOutOfRangeException)
            {
                // the requested position is past the end of the stream
                _currentPosition = _packetProvider.GetGranuleCount() - _granuleOffset;
                _prevPacketStart = _prevPacketEnd = 0;
                _eosFound = true;
                return;
            }

            if (rollForward < 0)
            {
                // The target falls in a granule hole -- a gap in the stream's timeline, e.g. a
                // spliced stream (issue #39) -- so the provider gave us the first packet after
                // the hole.  Snap forward to the first sample that actually exists.
                samplePosition = pos - _granuleOffset;
                rollForward = 0;
            }

            // read the pre-roll packet
            if (!ReadNextPacket(0, out _))
            {
                // we'll use this to force ReadSamples to fail to read
                _eosFound = true;
                if (_packetProvider.GetGranuleCount() - _granuleOffset != samplePosition)
                {
                    throw new InvalidOperationException("Could not read pre-roll packet!  Try seeking again prior to reading more samples.");
                }
                _prevPacketStart = _prevPacketStop;
                _currentPosition = samplePosition;
                return;
            }

            // read the actual packet
            if (!ReadNextPacket(0, out _))
            {
                ResetDecoder();
                // we'll use this to force ReadSamples to fail to read
                _eosFound = true;
                throw new InvalidOperationException("Could not read seek packet!  Try seeking again prior to reading more samples.");
            }

            // adjust our indexes to match what we want
            while (_prevPacketStart + rollForward > _prevPacketEnd && !_eosFound)
            {
                var size = _prevPacketEnd - _prevPacketStart;
                rollForward -= size;
                if (!ReadNextPacket(0, out _))
                {
                    _prevPacketStart = _prevPacketEnd;
                    rollForward = 0;
                    break;
                }
            }

            _prevPacketStart += rollForward;
            _currentPosition = samplePosition;
        }

        private int GetPacketGranules(IPacket curPacket)
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

            return _modes[modeIdx].GetPacketSampleCount(curPacket);
        }

        #endregion

        /// <summary>
        /// Cleans up this instance.
        /// </summary>
        public void Dispose()
        {
            (_packetProvider as IDisposable)?.Dispose();
            _packetProvider = null;
        }

        #region Properties

        /// <summary>
        /// Gets the number of channels in the stream.
        /// </summary>
        public int Channels => _channels;

        /// <summary>
        /// Gets the sample rate of the stream.
        /// </summary>
        public int SampleRate => _sampleRate;

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
        public ITagData Tags => _tags.Value;

        /// <summary>
        /// Gets the total duration of the decoded stream.
        /// </summary>
        public TimeSpan TotalTime => TimeSpan.FromSeconds((double)TotalSamples / _sampleRate);

        /// <summary>
        /// Gets the total number of samples in the decoded stream.
        /// </summary>
        public long TotalSamples => (_packetProvider ?? throw new ObjectDisposedException(nameof(StreamDecoder))).GetGranuleCount() - _granuleOffset;

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
        /// Gets or sets whether to clip samples returned by <see cref="Read(Span&lt;float&gt;, int, int)"/>.
        /// </summary>
        public bool ClipSamples { get; set; }

        /// <summary>
        /// Gets whether <see cref="Read(Span&lt;float&gt;, int, int)"/> has returned any clipped samples.
        /// </summary>
        public bool HasClipped => _hasClipped;

        /// <summary>
        /// Gets whether the decoder has reached the end of the stream.
        /// </summary>
        public bool IsEndOfStream => _eosFound && _prevPacketBuf == null;

        /// <summary>
        /// Gets the <see cref="IStreamStats"/> instance for this stream.
        /// </summary>
        public IStreamStats Stats => _stats;

        #endregion
    }
}
