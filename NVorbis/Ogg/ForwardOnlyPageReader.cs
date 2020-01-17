using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis.Ogg
{
    class ForwardOnlyPageReader : PageReaderBase
    {
        internal static Func<IPageReader, int, IForwardOnlyPacketProvider> CreatePacketProvider { get; set; } = (pr, ss) => new ForwardOnlyPacketProvider(pr, ss);

        private readonly Dictionary<int, IForwardOnlyPacketProvider> _packetProviders = new Dictionary<int, IForwardOnlyPacketProvider>();
        private readonly Func<IPacketProvider, bool> _newStreamCallback;

        public ForwardOnlyPageReader(Stream stream, bool closeOnDispose, Func<IPacketProvider, bool> newStreamCallback)
            : base(stream, closeOnDispose)
        {
            _newStreamCallback = newStreamCallback;
        }

        protected override bool AddPage(int streamSerial, byte[] pageBuf, bool isResync)
        {
            if (_packetProviders.TryGetValue(streamSerial, out var pp))
            {
                pp.AddPage(pageBuf, isResync);
                if (((PageFlags)pageBuf[5] & PageFlags.EndOfStream) != 0)
                {
                    // if it's done, just remove it
                    _packetProviders.Remove(streamSerial);
                }
            }
            else
            {
                pp = CreatePacketProvider(this, streamSerial);
                pp.AddPage(pageBuf, isResync);
                _packetProviders.Add(streamSerial, pp);
                if (!_newStreamCallback(pp))
                {
                    _packetProviders.Remove(streamSerial);
                    return false;
                }
            }
            return true;
        }

        protected override void SetEndOfStreams()
        {
            foreach (var kvp in _packetProviders)
            {
                kvp.Value.SetEndOfStream();
            }
            _packetProviders.Clear();
        }

        public override bool ReadPageAt(long offset) => throw new NotSupportedException();
    }
}
