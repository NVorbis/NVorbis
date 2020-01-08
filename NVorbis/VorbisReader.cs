using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis
{
    public sealed class VorbisReader : IDisposable
    {
        private List<IStreamDecoder> _decoders;
        private IContainerReader _containerReader;
        private bool _isOwned;

        public event EventHandler<NewStreamEventArgs> NewStream;

        public VorbisReader(string fileName)
            : this(File.OpenRead(fileName), true)
        {
        }

        public VorbisReader(Stream stream, bool isOwned = true)
            : this(new Ogg.ContainerReader(stream, isOwned))
        {
        }

        public VorbisReader(IContainerReader containerReader, bool isOwned = true)
        {
            _decoders = new List<IStreamDecoder>();
            if (!LoadContainer(containerReader) || _decoders.Count == 0)
            {
                if (isOwned)
                {
                    containerReader.Dispose();
                }

                throw new ArgumentException("Could not load the specified container!", nameof(containerReader));
            }
            _isOwned = isOwned;
            _containerReader = containerReader;
        }

        private bool LoadContainer(IContainerReader containerReader)
        {
            containerReader.NewStreamCallback = ProcessNewStream;
            if (!containerReader.FindNextStream())
            {
                containerReader.NewStreamCallback = null;
                return false;
            }
            return true;
        }

        private bool ProcessNewStream(IPacketProvider packetProvider)
        {
            var decoder = new StreamDecoder(packetProvider, new Factory());
            if (decoder.TryInit())
            {
                var ea = new NewStreamEventArgs(decoder);
                NewStream?.Invoke(this, ea);
                if (!ea.IgnoreStream)
                {
                    _decoders.Add(decoder);
                    decoder.ClipSamples = true;
                    return true;
                }
            }
            return false;
        }

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

        public IReadOnlyList<IStreamDecoder> Streams => _decoders;
    }
}
