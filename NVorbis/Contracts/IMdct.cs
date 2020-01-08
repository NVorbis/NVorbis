using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis.Contracts
{
    interface IMdct
    {
        void Reverse(float[] samples, int sampleCount);
    }
}
