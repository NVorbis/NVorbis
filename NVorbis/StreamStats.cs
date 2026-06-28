using NVorbis.Contracts;
using System.Threading;

namespace NVorbis
{
    class StreamStats : IStreamStats
    {
        private int _sampleRate;

        // Each slot packs (bits: int hi, samples: int lo) so InstantBitRate reads
        // a consistent pair with a single Volatile.Read — no lock required.
        private long _packetSlot0;
        private long _packetSlot1;
        private int _packetIndex;

        private long _totalSamples;
        private long _audioBits;
        private long _headerBits;
        private long _containerBits;
        private long _wasteBits;
        private int _packetCount;

        private static long Pack(int bits, int samples) => ((long)bits << 32) | (uint)samples;
        private static int UnpackBits(long slot) => (int)(slot >> 32);
        private static int UnpackSamples(long slot) => (int)slot;

        public int EffectiveBitRate
        {
            get
            {
                var samples = _totalSamples;
                var bits = _audioBits + _headerBits + _containerBits + _wasteBits;
                return samples > 0 ? (int)(((double)bits / samples) * _sampleRate) : 0;
            }
        }

        public int InstantBitRate
        {
            get
            {
                var slot0 = Volatile.Read(ref _packetSlot0);
                var slot1 = Volatile.Read(ref _packetSlot1);
                var bits = UnpackBits(slot0) + UnpackBits(slot1);
                var samples = UnpackSamples(slot0) + UnpackSamples(slot1);
                return samples > 0 ? (int)(((double)bits / samples) * _sampleRate) : 0;
            }
        }

        public long ContainerBits => _containerBits;

        public long OverheadBits => _headerBits;

        public long AudioBits => _audioBits;

        public long WasteBits => _wasteBits;

        public int PacketCount => _packetCount;

        public void ResetStats()
        {
            Volatile.Write(ref _packetSlot0, 0L);
            Volatile.Write(ref _packetSlot1, 0L);
            _packetIndex = 0;
            _packetCount = 0;
            _audioBits = 0;
            _totalSamples = 0;
            _headerBits = 0;
            _containerBits = 0;
            _wasteBits = 0;
        }

        internal void SetSampleRate(int sampleRate)
        {
            _sampleRate = sampleRate;
            ResetStats();
        }

        internal void AddPacket(int samples, int bits, int waste, int container)
        {
            if (samples >= 0)
            {
                // audio packet
                _audioBits += bits;
                _wasteBits += waste;
                _containerBits += container;
                _totalSamples += samples;
                var slot = Pack(bits + waste, samples);
                if (_packetIndex == 0)
                    Volatile.Write(ref _packetSlot0, slot);
                else
                    Volatile.Write(ref _packetSlot1, slot);
                _packetCount++;
                if (++_packetIndex == 2)
                    _packetIndex = 0;
            }
            else
            {
                // header packet
                _headerBits += bits;
                _wasteBits += waste;
                _containerBits += container;
            }
        }
    }
}
