using NVorbis.Contracts;
using System;

namespace NVorbis
{
    class Mode : IMode
    {
        const float M_PI = 3.1415926539f; //(float)Math.PI;
        const float M_PI2 = M_PI / 2;

        int _channels;
        bool _blockFlag;
        int _block0Size;
        int _block1Size;
        IMapping _mapping;

        public void Init(IPacket packet, int channels, int block0Size, int block1Size, IMapping[] mappings)
        {
            _channels = channels;
            _block0Size = block0Size;
            _block1Size = block1Size;

            _blockFlag = packet.ReadBit();
            if (0 != packet.ReadBits(32))
            {
                throw new System.IO.InvalidDataException("Mode header had invalid window or transform type!");
            }

            var mappingIdx = (int)packet.ReadBits(8);
            if (mappingIdx >= mappings.Length)
            {
                throw new System.IO.InvalidDataException("Mode header had invalid mapping index!");
            }
            _mapping = mappings[mappingIdx];

            if (_blockFlag)
            {
                Windows = new float[][]
                {
                    new float[_block1Size],
                    new float[_block1Size],
                    new float[_block1Size],
                    new float[_block1Size],
                };
            }
            else
            {
                Windows = new float[][]
                {
                    new float[_block0Size],
                };
            }
            CalcWindows();
        }

        private void CalcWindows()
        {
            // 0: prev = s, next = s || BlockFlag = false
            // 1: prev = l, next = s
            // 2: prev = s, next = l
            // 3: prev = l, next = l

            for (int idx = 0; idx < Windows.Length; idx++)
            {
                var array = Windows[idx];

                var left = ((idx & 1) == 0 ? _block0Size : _block1Size) / 2;
                var wnd = BlockSize;
                var right = ((idx & 2) == 0 ? _block0Size : _block1Size) / 2;

                var leftbegin = wnd / 4 - left / 2;
                var rightbegin = wnd - wnd / 4 - right / 2;

                for (int i = 0; i < left; i++)
                {
                    var x = (float)Math.Sin((i + .5) / left * M_PI2);
                    x *= x;
                    array[leftbegin + i] = (float)Math.Sin(x * M_PI2);
                }

                for (int i = leftbegin + left; i < rightbegin; i++)
                {
                    array[i] = 1.0f;
                }

                for (int i = 0; i < right; i++)
                {
                    var x = (float)Math.Sin((right - i - .5) / right * M_PI2);
                    x *= x;
                    array[rightbegin + i] = (float)Math.Sin(x * M_PI2);
                }
            }
        }

        public bool Decode(IPacket packet, float[][] buffer, out int packetStartindex, out int packetValidLength, out int packetTotalLength)
        {
            int blockSize;
            bool prevFlag;
            bool nextFlag;
            if (_blockFlag)
            {
                blockSize = _block1Size;
                prevFlag = packet.ReadBit();
                nextFlag = packet.ReadBit();
            }
            else
            {
                blockSize = _block0Size;
                prevFlag = nextFlag = false;
            }

            if (packet.IsShort)
            {
                packetStartindex = 0;
                packetValidLength = 0;
                packetTotalLength = 0;
                return false;
            }

            _mapping.DecodePacket(packet, BlockSize, _channels, buffer);

            var window = Windows[(prevFlag ? 1 : 0) + (nextFlag ? 2 : 0)];
            for (var i = 0; i < blockSize; i++)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    buffer[ch][i] *= window[i];
                }
            }

            var leftOverlapHalfSize = (prevFlag ? _block1Size : _block0Size) / 4;
            var rightOverlapHalfSize = (nextFlag ? _block1Size : _block0Size) / 4;

            packetStartindex = blockSize / 4 - leftOverlapHalfSize;
            packetTotalLength = blockSize / 4 * 3 + rightOverlapHalfSize;
            packetValidLength = packetTotalLength - rightOverlapHalfSize * 2;
            return true;
        }

        public int GetPacketSampleCount(IPacket packet, bool isFirst)
        {
            int blockSize;
            bool prevFlag;
            bool nextFlag;
            if (_blockFlag)
            {
                blockSize = _block1Size;
                prevFlag = packet.ReadBit();
                nextFlag = packet.ReadBit();
            }
            else
            {
                blockSize = _block0Size;
                prevFlag = nextFlag = false;
            }

            var leftOverlapHalfSize = (prevFlag ? _block1Size : _block0Size) / 4;
            var rightOverlapHalfSize = (nextFlag ? _block1Size : _block0Size) / 4;

            return blockSize / 4 * 3 - rightOverlapHalfSize - (blockSize / 4 + (isFirst ? leftOverlapHalfSize : -leftOverlapHalfSize));
        }

        public int BlockSize => _blockFlag ? _block1Size : _block0Size;

        public float[][] Windows { get; private set; }
    }
}
