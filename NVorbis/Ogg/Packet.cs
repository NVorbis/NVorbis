using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    internal class Packet : DataPacket
    {
        // size with 1-2 packet segments (> 2 packet segments should be very uncommon):
        //   x86:  68 bytes
        //   x64: 104 bytes

        private IReadOnlyList<Memory<byte>> _dataSrc;           // IntPtr + (IntPtr + 4 + 4 + IntPtr + (IntPtr * 4 + 4) * 2) # the page data buffer is rooted at PageReader._packets and doesn't count here.
        private IPacketReader _packetReader;                    // IntPtr
        int _dataIndex;                                         // 4
        int _dataOfs;                                           // 4

        internal Packet(IReadOnlyList<Memory<byte>> data, IPacketReader packetReader)
        {
            _dataSrc = data;
            _packetReader = packetReader;
        }

        protected override int TotalBits
        {
            get
            {
                var ttl = 0;
                for (var i = 0; i < _dataSrc.Count; i++)
                {
                    ttl += _dataSrc[i].Length;
                }
                return ttl * 8;
            }
        }

        protected override int ReadNextByte()
        {
            if (_dataIndex == _dataSrc.Count) return -1;

            var b = _dataSrc[_dataIndex].Span[_dataOfs];

            if (++_dataOfs == _dataSrc[_dataIndex].Length)
            {
                _dataOfs = 0;
                ++_dataIndex;
            }

            return b;
        }

        public override void Reset()
        {
            _dataIndex = 0;
            _dataOfs = 0;

            base.Reset();
        }

        public override void Done()
        {
            _packetReader?.InvalidatePacketCache(this);

            base.Done();
        }
    }
}