using NVorbis.Contracts;
using System;

namespace NVorbis
{
    class StreamStats : IStreamStats
    {
        private int _sampleRate;

        private readonly int[] _packetBits = new int[2];
        private readonly int[] _packetSamples = new int[2];
        private int _packetIndex;

        private long _totalSamples;
        private long _audioBits;
        private long _overheadBits;
        private long _wasteBits;

        private object _lock = new object();
        private int _packetCount;

        public int EffectiveBitRate
        {
            get
            {
                long samples, bits;
                lock (_lock)
                {
                    samples = _totalSamples;
                    bits = _audioBits + _overheadBits + _wasteBits;
                }
                if (samples > 0)
                {
                    return (int)(((double)bits / samples) * _sampleRate);
                }
                return 0;
            }
        }

        public int InstantBitRate
        {
            get
            {
                int samples, bits;
                lock (_lock)
                {
                    bits = _packetBits[0] + _packetBits[1];
                    samples = _packetSamples[0] + _packetSamples[1];
                }
                if (samples > 0)
                {
                    return (int)(((double)bits / samples) * _sampleRate);
                }
                return 0;
            }
        }

        public long OverheadBits => _overheadBits;

        public long AudioBits => _audioBits;

        public long WasteBits => _wasteBits;

        public int PacketCount => _packetCount;

        #region Deprecated
        public TimeSpan PageLatency => throw new NotSupportedException();

        public TimeSpan PacketLatency => throw new NotSupportedException();

        public TimeSpan SecondLatency => throw new NotSupportedException();

        public int PagesRead => throw new NotSupportedException();

        public int TotalPages => throw new NotSupportedException();

        public bool Clipped => throw new NotSupportedException();
        #endregion

        public void ResetStats()
        {
            lock (_lock)
            {
                _packetBits[0] = _packetBits[1] = 0;
                _packetSamples[0] = _packetSamples[1] = 0;
                _packetIndex = 0;
                _packetCount = 0;
                _audioBits = 0;
                _totalSamples = 0;
                _overheadBits = 0;
                _wasteBits = 0;
            }
        }

        internal void SetSampleRate(int sampleRate)
        {
            lock (_lock)
            {
                _sampleRate = sampleRate;

                _packetBits[0] = _packetBits[1] = 0;
                _packetSamples[0] = _packetSamples[1] = 0;
                _packetIndex = 0;

                _audioBits = 0;
                _totalSamples = 0;
                _wasteBits = 0;
            }
        }

        internal void AddPacket(int samples, int bits, int overhead, int containerOverhead)
        {
            lock (_lock)
            {
                ++_packetCount;
                _audioBits += bits;

                if (samples >= 0)
                {
                    _wasteBits += overhead;
                    _overheadBits += containerOverhead;
                    _totalSamples += samples;
                    _packetBits[_packetIndex] = bits + overhead;
                    _packetSamples[_packetIndex] = samples;

                    if (++_packetIndex == 2)
                    {
                        _packetIndex = 0;
                    }
                }
                else
                {
                    _overheadBits += overhead + containerOverhead;
                }
            }
        }
    }
}
