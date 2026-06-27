using NVorbis.Contracts;
using NVorbis.Contracts.Ogg;
using System;

namespace NVorbis.Ogg
{
    internal class Packet : DataPacket
    {
        // size with 1-2 packet segments (> 2 packet segments should be very uncommon):
        //   x86:  68 bytes
        //   x64: 104 bytes

        // _firstPart stores the 24:8 packed (pageIndex:packetIndex) of the first page.
        // _extraParts stores packed pageIndex values for any continuation pages (null for
        // the common single-page case, avoiding the List<int> + backing-array allocation).
        private readonly int _firstPart;
        private readonly int[] _extraParts;
        private readonly int _partCount;
        private IPacketReader _packetReader;                    // IntPtr
        int _dataCount;
        Memory<byte> _data;
        int _dataIndex;                                         // 4
        int _dataOfs;                                           // 4

        internal Packet(int firstPart, IPacketReader packetReader, Memory<byte> initialData)
        {
            _firstPart = firstPart;
            _partCount = 1;
            _packetReader = packetReader;
            _data = initialData;
        }

        internal Packet(int firstPart, int[] extraParts, IPacketReader packetReader, Memory<byte> initialData)
        {
            _firstPart = firstPart;
            _extraParts = extraParts;
            _partCount = 1 + extraParts.Length;
            _packetReader = packetReader;
            _data = initialData;
        }

        private int GetPart(int index) => index == 0 ? _firstPart : _extraParts[index - 1];

        protected override int TotalBits => (_dataCount + _data.Length) * 8;

        protected override int ReadNextByte()
        {
            if (_dataIndex == _partCount) return -1;

            var b = _data.Span[_dataOfs];

            if (++_dataOfs == _data.Length)
            {
                _dataOfs = 0;
                _dataCount += _data.Length;
                if (++_dataIndex < _partCount)
                {
                    _data = _packetReader.GetPacketData(GetPart(_dataIndex));
                }
                else
                {
                    _data = Memory<byte>.Empty;
                }
            }

            return b;
        }

        public override void Reset()
        {
            _dataIndex = 0;
            _dataOfs = 0;
            if (_partCount > 0)
            {
                _data = _packetReader.GetPacketData(_firstPart);
            }

            base.Reset();
        }

        public override void Done()
        {
            _packetReader?.InvalidatePacketCache(this);

            base.Done();
        }
    }
}
