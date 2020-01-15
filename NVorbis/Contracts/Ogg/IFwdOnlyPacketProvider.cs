using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis.Contracts.Ogg
{
    interface IFwdOnlyPacketProvider : IPacketProvider
    {
        void AddPage(byte[] buf, bool isResync);
        void SetEndOfStream();
    }
}
