using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis.Ogg
{
    /// <summary>
    /// Implements <see cref="IContainerReader"/> for Ogg format files for low memory cost.
    /// </summary>
    public sealed class ContainerReader : IContainerReader
    {
        internal static Func<Stream, bool, Func<IPacketProvider, bool>, IPageReader> CreatePageReader { get; set; } = (s, cod, cb) => new PageReader(s, cod, cb);

        IPageReader _reader;
        List<IPacketProvider> _streams;

        /// <summary>
        /// Gets or sets the callback to invoke when a new stream is encountered in the container.
        /// </summary>
        public Func<IPacketProvider, bool> NewStreamCallback { get; set; }

        /// <summary>
        /// Gets a list of streams available from this container.
        /// </summary>
        public IReadOnlyList<IPacketProvider> Streams => _streams;

        /// <summary>
        /// Gets the number of bits in the container that are not associated with a logical stream.
        /// </summary>
        public long WasteBits => _reader.WasteBits;

        /// <summary>
        /// Gets the number of bits in the container that are strictly for framing of logical streams.
        /// </summary>
        public long ContainerBits => _reader.ContainerBits;


        /// <summary>
        /// Creates a new instance of <see cref="ContainerReader"/>.
        /// </summary>
        /// <param name="stream">The <see cref="Stream"/> to read.</param>
        /// <param name="closeOnDispose"><c>True</c> to close the stream when disposed, otherwise <c>false</c>.</param>
        /// <exception cref="ArgumentException"><paramref name="stream"/>'s <see cref="Stream.CanSeek"/> is <c>False</c>.</exception>
        public ContainerReader(Stream stream, bool closeOnDispose)
        {
            if (!(stream ?? throw new ArgumentNullException(nameof(stream))).CanSeek)
            {
                throw new ArgumentException("Stream must be seek-able!", nameof(stream));
            }
            _reader = CreatePageReader(stream, closeOnDispose, ProcessNewStream);
            _streams = new List<IPacketProvider>();
        }

        /// <summary>
        /// Attempts to initialize the container.
        /// </summary>
        /// <returns><see langword="true"/> if successful, otherwise <see langword="false"/>.</returns>
        public bool TryInit()
        {
            return FindNextStream();
        }

        /// <summary>
        /// Finds the next new stream in the container.
        /// </summary>
        /// <returns><c>True</c> if a new stream was found, otherwise <c>False</c>.</returns>
        public bool FindNextStream()
        {
            _reader.Lock();
            try
            {
                var cnt = _streams.Count;
                while (_reader.ReadNextPage())
                {
                    if (cnt < _streams.Count)
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                _reader.Release();
            }
        }

        private bool ProcessNewStream(IPacketProvider packetProvider)
        {
            var relock = _reader.Release();
            try
            {
                if (NewStreamCallback?.Invoke(packetProvider) ?? true)
                {
                    _streams.Add(packetProvider);
                    return true;
                }
                return false;
            }
            finally
            {
                if (relock)
                {
                    _reader.Lock();
                }
            }
        }

        /// <summary>
        /// Cleans up
        /// </summary>
        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
        }
    }
}
