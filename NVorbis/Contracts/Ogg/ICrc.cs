using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis.Contracts.Ogg
{
    interface ICrc
    {
        void Reset();
        void Update(int nextVal);
        bool Test(uint checkCrc);
    }
}
