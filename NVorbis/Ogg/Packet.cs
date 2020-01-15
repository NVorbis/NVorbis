using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    internal class Packet : DataPacket
    {
        private IReadOnlyList<ValueTuple<long, int>> _dataSrc;  // IntPtr + 12 * segment_count
        private IPacketReader _packetReader;                    // IntPtr
        int _dataIndex;                                         // 4
        int _dataOfs;                                           // 4
        byte[] _dataBuf;                                        // IntPtr + cur_segment_size

        internal Packet(IReadOnlyList<ValueTuple<long, int>> data, IPacketReader packetReader)
        {
            _dataSrc = data;
            _packetReader = packetReader;
        }

        protected override int TotalBits
        {
            get
            {
                var ttl = _dataSrc[0].Item2;
                for (var i = 1; i < _dataSrc.Count; i++)
                {
                    ttl += _dataSrc[i].Item2;
                }
                return ttl * 8;
            }
        }

        protected override int ReadNextByte()
        {
            if (_dataIndex == _dataSrc.Count) return -1;

            if (_dataOfs == 0)
            {
                var ofs = _dataSrc[_dataIndex].Item1;
                _dataBuf = new byte[_dataSrc[_dataIndex].Item2];

                var idx = 0;
                int cnt;
                while (idx < _dataBuf.Length && (cnt = _packetReader.FillBuffer(ofs + idx, _dataBuf, idx, _dataBuf.Length - idx)) > 0)
                {
                    idx += cnt;
                }
                if (idx < _dataBuf.Length)
                {
                    // uh-oh...  bad packet
                    _dataBuf = null;
                    _dataIndex = _dataSrc.Count;
                    return -1;
                }
            }

            var b = _dataBuf[_dataOfs];

            if (++_dataOfs == _dataSrc[_dataIndex].Item2)
            {
                _dataOfs = 0;
                if (++_dataIndex == _dataSrc.Count)
                {
                    _dataBuf = null;
                }
            }

            return b;
        }

        public override void Reset()
        {
            _dataIndex = 0;
            _dataOfs = 0;
            _dataBuf = null;

            base.Reset();
        }

        public override void Done()
        {
            _packetReader.InvalidatePacketCache(this);
            _dataBuf = null;

            base.Done();
        }
    }
}