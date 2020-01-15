using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NVorbis.Ogg
{
    public sealed class FwdOnlyContainerReader : IContainerReader
    {
        internal static Func<Stream, bool, Func<IPacketProvider, bool>, IPageReader> CreatePageReader { get; set; } = (s, cod, cb) => new FwdOnlyPageReader(s, cod, cb);

        IPageReader _reader;
        List<IPacketProvider> _streams;

        public Func<IPacketProvider, bool> NewStreamCallback { get; set; }

        public IReadOnlyList<IPacketProvider> Streams => _streams;

        public long ContainerBits => _reader.ContainerBits;

        public long WasteBits => _reader.WasteBits;

        public FwdOnlyContainerReader(Stream stream, bool closeOnDispose)
        {
            if (!(stream ?? throw new ArgumentNullException(nameof(stream))).CanSeek)
            {
                throw new ArgumentException("Stream must be seek-able!", nameof(stream));
            }
            _reader = CreatePageReader(stream, closeOnDispose, ProcessNewStream);
            _streams = new List<IPacketProvider>();
        }

        public bool TryInit()
        {
            return FindNextStream();
        }

        public bool FindNextStream()
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

        private bool ProcessNewStream(IPacketProvider packetProvider)
        {
            if (NewStreamCallback?.Invoke(packetProvider) ?? true)
            {
                _streams.Add(packetProvider);
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
        }
    }
}
