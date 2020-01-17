using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NVorbis
{
    /// <summary>
    /// Implements an easy to use wrapper around <see cref="IContainerReader"/> and <see cref="IStreamDecoder"/>.
    /// </summary>
    public sealed class VorbisReader : IDisposable
    {
        internal static Func<Stream, bool, IContainerReader> CreateContainerReader { get; set; } = (s, cod) => new Ogg.ContainerReader(s, cod);
        internal static Func<IPacketProvider, IStreamDecoder> CreateStreamDecoder { get; set; } = pp => new StreamDecoder(pp, new Factory());

        private readonly List<IStreamDecoder> _decoders;
        private readonly IContainerReader _containerReader;
        private readonly bool _closeOnDispose;

        private IStreamDecoder _streamDecoder;

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
        /// <param name="closeOnDispose"><see langword="true"/> to take ownership and clean up the instance when disposed, otherwise <see langword="false"/>.</param>
        public VorbisReader(Stream stream, bool closeOnDispose = true)
        {
            _decoders = new List<IStreamDecoder>();

            var containerReader = CreateContainerReader(stream, closeOnDispose);
            containerReader.NewStreamCallback = ProcessNewStream;

            if (!containerReader.TryInit() || _decoders.Count == 0)
            {
                containerReader.NewStreamCallback = null;
                containerReader.Dispose();

                if (closeOnDispose)
                {
                    stream.Dispose();
                }

                throw new ArgumentException("Could not load the specified container!", nameof(containerReader));
            }
            _closeOnDispose = closeOnDispose;
            _containerReader = containerReader;
            _streamDecoder = _decoders[0];
        }

        [Obsolete("Use \"new StreamDecoder(IPacketProvider)\" and the container's NewStreamCallback or Streams property instead.", true)]
        public VorbisReader(IContainerReader containerReader) => throw new NotSupportedException();

        [Obsolete("Use \"new StreamDecoder(IPacketProvider)\" instead.", true)]
        public VorbisReader(IPacketProvider packetProvider) => throw new NotSupportedException();

        private bool ProcessNewStream(IPacketProvider packetProvider)
        {
            var decoder = CreateStreamDecoder(packetProvider);
            decoder.ClipSamples = true;

            var ea = new NewStreamEventArgs(decoder);
            NewStream?.Invoke(this, ea);
            if (!ea.IgnoreStream)
            {
                _decoders.Add(decoder);
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
            }

            if (_containerReader != null)
            {
                _containerReader.NewStreamCallback = null;
                if (_closeOnDispose)
                {
                    _containerReader.Dispose();
                }
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
        public int Channels => _streamDecoder.Channels;

        /// <summary>
        /// Gets the sample rate of the stream.
        /// </summary>
        public int SampleRate => _streamDecoder.SampleRate;

        /// <summary>
        /// Gets the upper bitrate limit for the stream, if specified.
        /// </summary>
        public int UpperBitrate => _streamDecoder.UpperBitrate;

        /// <summary>
        /// Gets the nominal bitrate of the stream, if specified.  May be calculated from <see cref="LowerBitrate"/> and <see cref="UpperBitrate"/>.
        /// </summary>
        public int NominalBitrate => _streamDecoder.NominalBitrate;

        /// <summary>
        /// Gets the lower bitrate limit for the stream, if specified.
        /// </summary>
        public int LowerBitrate => _streamDecoder.LowerBitrate;

        /// <summary>
        /// Gets the tag data from the stream's header.
        /// </summary>
        public ITagData Tags => _streamDecoder.Tags;

        /// <summary>
        /// Gets the encoder's vendor string for the current selected Vorbis stream
        /// </summary>
        [Obsolete("Use .Tags.EncoderVendor instead.")]
        public string Vendor => _streamDecoder.Tags.EncoderVendor;

        /// <summary>
        /// Gets the comments in the current selected Vorbis stream
        /// </summary>
        [Obsolete("Use .Tags.All instead.")]
        public string[] Comments => _streamDecoder.Tags.All.SelectMany(k => k.Value, (kvp, Item) => $"{kvp.Key}={Item}").ToArray();

        /// <summary>
        /// Gets whether the previous short sample count was due to a parameter change in the stream.
        /// </summary>
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

        /// <summary>
        /// Gets the currently-selected stream's index.
        /// </summary>
        public int StreamIndex => _decoders.IndexOf(_streamDecoder);

        /// <summary>
        /// Returns the number of logical streams found so far in the physical container.
        /// </summary>
        public int StreamCount => _decoders.Count;

        /// <summary>
        /// Gets or Sets the current timestamp of the decoder.  Is the timestamp before the next sample to be decoded.
        /// </summary>
        [Obsolete("Use VorbisReader.TimePosition instead.")]
        public TimeSpan DecodedTime
        {
            get => _streamDecoder.TimePosition;
            set => TimePosition = value;
        }

        /// <summary>
        /// Gets or Sets the current position of the next sample to be decoded.
        /// </summary>
        [Obsolete("Use VorbisReader.SamplePosition instead.")]
        public long DecodedPosition
        {
            get => _streamDecoder.SamplePosition;
            set => SamplePosition = value;
        }

        /// <summary>
        /// Gets the total duration of the decoded stream.
        /// </summary>
        public TimeSpan TotalTime => _streamDecoder.TotalTime;

        /// <summary>
        /// Gets the total number of samples in the decoded stream.
        /// </summary>
        public long TotalSamples => _streamDecoder.TotalSamples;

        /// <summary>
        /// Gets or sets the current time position of the stream.
        /// </summary>
        public TimeSpan TimePosition
        {
            get => _streamDecoder.TimePosition;
            set
            {
                _streamDecoder.TimePosition = value;
                _tailBuffer = null;
            }
        }

        /// <summary>
        /// Gets or sets the current sample position of the stream.
        /// </summary>
        public long SamplePosition
        {
            get => _streamDecoder.SamplePosition;
            set
            {
                _streamDecoder.SamplePosition = value;
                _tailBuffer = null;
            }
        }

        /// <summary>
        /// Gets whether the current stream has ended.
        /// </summary>
        public bool IsEndOfStream => _streamDecoder.IsEndOfStream;

        /// <summary>
        /// Gets or sets whether to clip samples returned by <see cref="Read(float[], int, int, out bool)"/>.
        /// </summary>
        public bool ClipSamples
        {
            get => _streamDecoder.ClipSamples;
            set => _streamDecoder.ClipSamples = value;
        }

        /// <summary>
        /// Gets whether <see cref="Read(float[], int, int, out bool)"/> has returned any clipped samples.
        /// </summary>
        public bool HasClipped => _streamDecoder.HasClipped;

        /// <summary>
        /// Gets the <see cref="IStreamStats"/> instance for this stream.
        /// </summary>
        public IStreamStats Stats => _streamDecoder.Stats;

        /// <summary>
        /// Searches for the next stream in a concatenated file.  Will raise <see cref="NewStream"/> for the found stream, and will add it to <see cref="Streams"/> if not marked as ignored.
        /// </summary>
        /// <returns><see langword="true"/> if a new stream was found, otherwise <see langword="false"/>.</returns>
        public bool FindNextStream()
        {
            if (_containerReader == null) return false;
            return _containerReader.FindNextStream();
        }

        /// <summary>
        /// Switches to an alternate logical stream.
        /// </summary>
        /// <param name="index">The logical stream index to switch to</param>
        /// <returns><see langword="true"/> if the properties of the logical stream differ from those of the one previously being decoded. Otherwise, <see langword="false"/>.</returns>
        public bool SwitchStreams(int index)
        {
            if (index < 0 || index >= _decoders.Count) throw new ArgumentOutOfRangeException(nameof(index));

            var newDecoder = _decoders[index];
            var oldDecoder = _streamDecoder;
            if (newDecoder == oldDecoder) return false;

            // carry-through the clipping setting
            newDecoder.ClipSamples = oldDecoder.ClipSamples;

            _streamDecoder = newDecoder;

            return newDecoder.Channels != oldDecoder.Channels || newDecoder.SampleRate != oldDecoder.SampleRate;
        }

        /// <summary>
        /// Seeks the stream to the specified time.
        /// </summary>
        /// <param name="timePosition">The time to seek to.</param>
        public void SeekTo(TimeSpan timePosition)
        {
            _streamDecoder.SeekTo(timePosition);
            _tailBuffer = null;
        }

        /// <summary>
        /// Seeks the stream to the specified sample.
        /// </summary>
        /// <param name="samplePosition">The sample position to seek to.</param>
        public void SeekTo(long samplePosition)
        {
            _streamDecoder.SeekTo(samplePosition);
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
                readCount = _streamDecoder.Read(buffer, offset, count, out var isParamterChange);
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
                    tailRead = _streamDecoder.Read(_tailBuffer, 0, _tailBuffer.Length, out _);
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
            var cnt = _streamDecoder.Read(buffer, offset, count, out isParameterChange);
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
