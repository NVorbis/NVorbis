using System;
using System.IO;

namespace NVorbis.Ogg
{
    /// <summary>
    /// Implements <see cref="IContainerReader"/> for Ogg format files for low memory cost.
    /// </summary>
    public sealed class LightContainerReader : IContainerReader
    {
        LightPageReader _reader;

        /// <summary>
        /// Gets the list of stream serials found in the container so far.
        /// </summary>
        public int[] StreamSerials => _reader.FoundSerials;

        /// <summary>
        /// Gets whether the container supports seeking.
        /// </summary>
        public bool CanSeek => true;

        /// <summary>
        /// Gets the number of bits in the container that are not associated with a logical stream.
        /// </summary>
        public long WasteBits => _reader.WasteBits;

        /// <summary>
        /// Gets the number of pages that have been read in the container.
        /// </summary>
        public int PagesRead => _reader.PageCount;

        /// <summary>
        /// Event raised when a new logical stream is found in the container.
        /// </summary>
        public event EventHandler<NewStreamEventArgs> NewStream;

        /// <summary>
        /// Creates a new instance of <see cref="LightContainerReader"/>.
        /// </summary>
        /// <param name="stream">The <see cref="Stream"/> to read.</param>
        /// <param name="closeOnDispose"><c>True</c> to close the stream when disposed, otherwise <c>false</c>.</param>
        /// <exception cref="ArgumentException"><paramref name="stream"/>'s <see cref="Stream.CanSeek"/> is <c>False</c>.</exception>
        public LightContainerReader(Stream stream, bool closeOnDispose)
        {
            if (!(stream ?? throw new ArgumentNullException(nameof(stream))).CanSeek)
            {
                throw new ArgumentException("Stream must be seek-able!", nameof(stream));
            }
            _reader = new LightPageReader(stream, closeOnDispose, NewStreamCallback);
        }

        /// <summary>
        /// Initializes the container and finds the first stream.
        /// </summary>
        /// <returns><c>True</c> if a valid logical stream is found, otherwise <c>False</c>.</returns>
        public bool Init()
        {
            // if we find a stream at all, init was successful
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
                var cnt = _reader.FoundStreams;
                while (_reader.ReadNextPage())
                {
                    if (cnt < _reader.FoundStreams)
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

        private bool NewStreamCallback(LightPacketProvider packetProvider)
        {
            var relock = _reader.Release();
            var ea = new NewStreamEventArgs(packetProvider);
            try
            {
                NewStream?.Invoke(this, ea);
            }
            finally
            {
                if (relock)
                {
                    _reader.Lock();
                }
            }
            return !ea.IgnoreStream;
        }

        /// <summary>
        /// Retrieves the total number of pages in the container.
        /// </summary>
        /// <returns>The total number of pages.</returns>
        public int GetTotalPageCount()
        {
            _reader.Lock();
            try
            {
                _reader.ReadAllPages();
                return _reader.PageCount;
            }
            finally
            {
                _reader.Release();
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
