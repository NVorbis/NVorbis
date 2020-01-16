using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis
{
    /// <summary>
    /// Implements an easy to use wrapper around <see cref="IContainerReader"/> and <see cref="IStreamDecoder"/>.
    /// </summary>
    public sealed class VorbisReader : IDisposable
    {
        internal static Func<Stream, bool, IContainerReader> CreateContainerReader { get; set; } = (s, cod) => new Ogg.ContainerReader(s, cod);
        internal static Func<IPacketProvider, IStreamDecoder> CreateStreamDecoder { get; set; } = pp => new StreamDecoder(pp, new Factory());

        private List<IStreamDecoder> _decoders;
        private IContainerReader _containerReader;
        private bool _isOwned;
        private int _tailIdx;
        private float[] _tailBuffer;

        /// <summary>
        /// Raised when a new stream has been encountered in the file or container.
        /// </summary>
        public event EventHandler<NewStreamEventArgs> NewStream;

        /// <summary>
        /// Creates a new instance of <see cref="VorbisReader"/> reading from the specified file.
        /// </summary>
        /// <param name="fileName">The file to read from.</param>
        public VorbisReader(string fileName)
            : this(File.OpenRead(fileName), true)
        {
        }

        /// <summary>
        /// Creates a new instance of <see cref="VorbisReader"/> reading from the specified stream, optionally taking ownership of it.
        /// </summary>
        /// <param name="stream">The <see cref="Stream"/> to read from.</param>
        /// <param name="isOwned"><see langword="true"/> to take ownership and clean up the instance when disposed, otherwise <see langword="false"/>.</param>
        public VorbisReader(Stream stream, bool isOwned = true)
        {
            _decoders = new List<IStreamDecoder>();

            var containerReader = CreateContainerReader(stream, isOwned);
            containerReader.NewStreamCallback = ProcessNewStream;

            if (!containerReader.TryInit() || _decoders.Count == 0)
            {
                containerReader.NewStreamCallback = null;
                containerReader.Dispose();

                if (isOwned)
                {
                    stream.Dispose();
                }

                throw new ArgumentException("Could not load the specified container!", nameof(containerReader));
            }
            _isOwned = isOwned;
            _containerReader = containerReader;
        }

        [Obsolete("Use \"new StreamDecoder(IPacketProvider)\" and the container's NewStreamCallback or Streams property instead.", true)]
        public VorbisReader(IContainerReader containerReader) => throw new NotSupportedException();

        [Obsolete("Use \"new StreamDecoder(IPacketProvider)\" instead.", true)]
        public VorbisReader(IPacketProvider packetProvider) => throw new NotSupportedException();

        private bool ProcessNewStream(IPacketProvider packetProvider)
        {
            var decoder = CreateStreamDecoder(packetProvider);
            var ea = new NewStreamEventArgs(decoder);
            NewStream?.Invoke(this, ea);
            if (!ea.IgnoreStream)
            {
                _decoders.Add(decoder);
                decoder.ClipSamples = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Cleans up this instance.
        /// </summary>
        public void Dispose()
        {
            if (_decoders != null)
            {
                foreach (var decoder in _decoders)
                {
                    (decoder as IDisposable)?.Dispose();
                }
                _decoders.Clear();
                _decoders = null;
            }

            if (_containerReader != null)
            {
                _containerReader.NewStreamCallback = null;
                if (_isOwned)
                {
                    _containerReader.Dispose();
                }
                _containerReader = null;
            }
        }

        /// <summary>
        /// Gets the list of <see cref="IStreamDecoder"/> instances associated with the loaded file / container.
        /// </summary>
        public IReadOnlyList<IStreamDecoder> Streams => _decoders;

        #region Convenience Helpers

        // Since most uses of Ogg Vorbis are single-stream audio files, we can make life simpler for users
        //  by exposing the first stream's properties and methods here.

        /// <summary>
        /// Gets the number of channels in the stream.
        /// </summary>
        public int Channels => _decoders[0].Channels;

        /// <summary>
        /// Gets the sample rate of the stream.
        /// </summary>
        public int SampleRate => _decoders[0].SampleRate;

        /// <summary>
        /// Gets the upper bitrate limit for the stream, if specified.
        /// </summary>
        public int UpperBitrate => _decoders[0].UpperBitrate;

        /// <summary>
        /// Gets the nominal bitrate of the stream, if specified.  May be calculated from <see cref="LowerBitrate"/> and <see cref="UpperBitrate"/>.
        /// </summary>
        public int NominalBitrate => _decoders[0].NominalBitrate;

        /// <summary>
        /// Gets the lower bitrate limit for the stream, if specified.
        /// </summary>
        public int LowerBitrate => _decoders[0].LowerBitrate;

        /// <summary>
        /// Gets the tag data from the stream's header.
        /// </summary>
        public ITagData Tags => _decoders[0].Tags;

        [Obsolete("Use .Tags.EncoderVendor instead.", true)]
        public string Vendor => throw new NotSupportedException();

        [Obsolete("Use .Tags.All instead.", true)]
        public string[] Comments => throw new NotSupportedException();

        [Obsolete("Use ReadSamples(float[], int, int, out bool) to get immediate parameter change flag status.")]
        public bool IsParameterChange { get; private set; }

        /// <summary>
        /// Gets the number of bits read that are related to framing and transport alone.
        /// </summary>
        public long ContainerOverheadBits => _containerReader?.ContainerBits ?? 0;

        /// <summary>
        /// Gets the number of bits skipped in the container due to framing, ignored streams, or sync loss.
        /// </summary>
        public long ContainerWasteBits => _containerReader?.WasteBits ?? 0;

        [Obsolete("Will always return 0.")]
        public int StreamIndex => 0;

        [Obsolete("Will always return 1.")]
        public int StreamCount => 1;

        [Obsolete("Use TimePosition instead.", true)]
        public TimeSpan DecodedTime => throw new NotSupportedException();

        [Obsolete("Use SamplePosition instead.", true)]
        public long DecodedPosition => throw new NotSupportedException();

        /// <summary>
        /// Gets the total duration of the decoded stream.
        /// </summary>
        public TimeSpan TotalTime => _decoders[0].TotalTime;

        /// <summary>
        /// Gets the total number of samples in the decoded stream.
        /// </summary>
        public long TotalSamples => _decoders[0].TotalSamples;

        /// <summary>
        /// Gets or sets the current time position of the stream.
        /// </summary>
        public TimeSpan TimePosition
        {
            get => _decoders[0].TimePosition;
            set
            {
                _decoders[0].TimePosition = value;
                _tailBuffer = null;
            }
        }

        /// <summary>
        /// Gets or sets the current sample position of the stream.
        /// </summary>
        public long SamplePosition
        {
            get => _decoders[0].SamplePosition;
            set
            {
                _decoders[0].SamplePosition = value;
                _tailBuffer = null;
            }
        }

        /// <summary>
        /// Gets or sets whether to clip samples returned by <see cref="Read(float[], int, int, out bool)"/>.
        /// </summary>
        public bool ClipSamples
        {
            get => _decoders[0].ClipSamples;
            set => _decoders[0].ClipSamples = value;
        }

        /// <summary>
        /// Gets whether <see cref="Read(float[], int, int, out bool)"/> has returned any clipped samples.
        /// </summary>
        public bool HasClipped => _decoders[0].HasClipped;

        /// <summary>
        /// Gets the <see cref="IStreamStats"/> instance for this stream.
        /// </summary>
        public IStreamStats Stats => _decoders[0].Stats;

        /// <summary>
        /// Searches for the next stream in a concatenated file.  Will raise <see cref="NewStream"/> for the found stream, and will add it to <see cref="Streams"/> if not marked as ignored.
        /// </summary>
        /// <returns><see langword="true"/> if a new stream was found, otherwise <see langword="false"/>.</returns>
        public bool FindNextStream()
        {
            if (_containerReader == null) return false;
            return _containerReader.FindNextStream();
        }

        [Obsolete("Stream switching is no longer supported.", true)]
        public bool SwitchStreams(int index) => throw new NotSupportedException();

        /// <summary>
        /// Seeks the stream to the specified time.
        /// </summary>
        /// <param name="timePosition">The time to seek to.</param>
        public void SeekTo(TimeSpan timePosition)
        {
            _decoders[0].SeekTo(timePosition);
            _tailBuffer = null;
        }

        /// <summary>
        /// Seeks the stream to the specified sample.
        /// </summary>
        /// <param name="samplePosition">The sample position to seek to.</param>
        public void SeekTo(long samplePosition)
        {
            _decoders[0].SeekTo(samplePosition);
            _tailBuffer = null;
        }

        /// <summary>
        /// Reads samples into the specified buffer.
        /// </summary>
        /// <param name="buffer">The buffer to read the samples into.</param>
        /// <param name="offset">The index to start reading samples into the buffer.</param>
        /// <param name="count">The number of samples that should be read into the buffer.</param>
        /// <returns>The number of floats read into the buffer.</returns>
        //
        // Previous versions of NVorbis.VorbisReader could handle partial-sample reading (count is an odd number when reading a stereo stream).
        // The new decoder design can't do that, so we have to fake it...
        // While we're at it, we're adding logic to handle the parameter change such that we have the same semantics as before.
        [Obsolete("Use Read(float[], int, int, out bool) instead.")]
        public int ReadSamples(float[] buffer, int offset, int count)
        {
            // if we have a tail buffer, we have floats
            if (_tailBuffer != null)
            {
                var delta = _tailBuffer.Length - _tailIdx;
                if (delta >= count)
                {
                    // just satisfy the request out of our buffer...
                    delta = count;
                    while (_tailIdx < _tailBuffer.Length && --delta >= 0)
                    {
                        buffer[offset++] = _tailBuffer[_tailIdx++];
                    }
                    // if we've used up the buffer, remove it
                    if (_tailIdx == _tailBuffer.Length)
                    {
                        _tailBuffer = null;
                    }
                    return count;
                }

                // update to reflect that we have data already buffered
                offset += delta;
                count -= delta;
            }

            // figure out the overage left in the count
            var tailLen = count % Channels;
            if (tailLen > 0)
            {
                // don't ask for the last sample; we'll do that separately
                count -= tailLen;
            }

            // read the bulk of the data
            int readCount;
            try
            {
                readCount = _decoders[0].Read(buffer, offset, count, out var isParamterChange);
                if (readCount == 0 && isParamterChange)
                {
                    IsParameterChange = true;
                    throw new InvalidOperationException("Currently pending a paramter change.  Read new parameters before requesting further samples!");
                }
                IsParameterChange = false;
            }
            catch
            {
                _tailBuffer = null;
                throw;
            }

            // if we've read anything and have a tail buffer, pop in the leading float values.
            if (_tailBuffer != null)
            {
                offset -= _tailBuffer.Length - _tailIdx;
                while (_tailIdx < _tailBuffer.Length)
                {
                    buffer[offset++] = _tailBuffer[_tailIdx++];
                    ++readCount;
                }
            }

            // if we don't have anything else to read, clear the buffer
            if (tailLen == 0)
            {
                _tailBuffer = null;
            }
            // if we can satify the read request with another sample, grab it.
            // note that if we can read one more sample to get the exact request, we don't; chances are it wouldn't succeed anyway.
            else if (readCount + tailLen >= count)
            {
                _tailBuffer = _tailBuffer ?? new float[Channels];
                _tailIdx = 0;

                int tailRead;
                try
                {
                    // we really don't care if this read succeeds... the primary read has already done so and all semantics belong to it.
                    // so even if the parameters have changed, we'll let the next pass trigger the exception.
                    // and if we don't read anything here, no harm done; the caller will probably try again.
                    tailRead = _decoders[0].Read(_tailBuffer, 0, _tailBuffer.Length, out _);
                }
                catch
                {
                    tailRead = 0;

                    // we should probably think about how to handle this exception intelligently...
                    // we can't reasonbly throw or bubble it here, because the primary read succeeded.
                    // but we probably shouldn't just swallow it, either...
                    // save it off for next pass, but only throw it if the primary read also throws?
                    // not sure...
                }

                if (tailRead > 0)
                {
                    // if we get here, we have a full sample (all channels)

                    offset += readCount;
                    while (++readCount <= count)
                    {
                        buffer[offset++] = _tailBuffer[_tailIdx++];
                    }
                }
                else
                {
                    _tailBuffer = null;
                }
            }

            return readCount;
        }

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
        public int Read(float[] buffer, int offset, int count, out bool isParameterChange)
        {
            _tailBuffer = null;
            var cnt = _decoders[0].Read(buffer, offset, count, out isParameterChange);
            IsParameterChange |= isParameterChange;
            return cnt;
        }

        /// <summary>
        /// Acknowledges a parameter change as signalled by <see cref="Read(float[], int, int, out bool)"/>.
        /// </summary>
        [Obsolete("IsParamterChange will be removed in a later release.  This method will be removed with it.")]
        public void ClearParameterChange()
        {
            IsParameterChange = false;
        }

        #endregion
    }
}
