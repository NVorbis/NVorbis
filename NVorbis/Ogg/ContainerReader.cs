using NVorbis.Contracts;
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
        Contracts.Ogg.IPageReader _reader;
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
            _reader = new PageReader(stream, closeOnDispose, ProcessNewStream);
            _streams = new List<IPacketProvider>();
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

        private bool ProcessNewStream(PacketProvider packetProvider)
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
        /// Retrieves the number of pages read in the logical stream having the specified stream serial.
        /// </summary>
        /// <param name="streamSerial">The serial number of the logical stream to query.</param>
        /// <returns>The number of pages read in the logical stream if found, otherwise 0.</returns>
        //public int GetStreamPageCount(int streamSerial) => _reader.GetStreamPagesRead(streamSerial);

        /// <summary>
        /// Retrieves the total number of pages in the logical stream having the specified stream serial.
        /// </summary>
        /// <param name="streamSerial">The serial number of the logical stream to query.</param>
        /// <returns>The total number of pages in the logical stream if found, otherwise 0.</returns>
        /// <exception cref="InvalidOperationException"><see cref="IContainerReader.CanSeek"/> is <c>False</c>.</exception>
        //public int GetStreamTotalPageCount(int streamSerial) => _reader.GetStreamTotalPageCount(streamSerial);

        /// <summary>
        /// Retrieves the total number of pages in the container.
        /// </summary>
        /// <returns>The total number of pages.</returns>
        //public int GetTotalPageCount()
        //{
        //    _reader.Lock();
        //    try
        //    {
        //        _reader.ReadAllPages();
        //        return _reader.PageCount;
        //    }
        //    finally
        //    {
        //        _reader.Release();
        //    }
        //}

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
