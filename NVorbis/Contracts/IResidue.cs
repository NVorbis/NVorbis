using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis.Contracts
{
    interface IResidue
    {
        void Init(IPacket packet, int channels, ICodebook[] codebooks);
        void Decode(IPacket packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer);
    }
}
