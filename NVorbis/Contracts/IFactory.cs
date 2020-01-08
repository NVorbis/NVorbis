using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis.Contracts
{
    interface IFactory
    {
        ICodebook CreateCodebook();
        IFloor CreateFloor(IPacket packet);
        IResidue CreateResidue(IPacket packet);
        IMapping CreateMapping(IPacket packet);
        IMode CreateMode();
        IMdct CreateMdct();
    }
}
