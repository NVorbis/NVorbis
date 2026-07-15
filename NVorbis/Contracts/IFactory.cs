namespace NVorbis.Contracts
{
    // Internal (not public) on purpose: this is dependency injection for testability, NOT runtime
    // configurability. CreateFloor/CreateResidue read the wire-format discriminator themselves so dispatch
    // stays colocated with construction, and tests can substitute a fake. There is no supported scenario
    // where an end user supplies their own IFactory - don't make it public without a concrete use case.
    interface IFactory
    {
        ICodebook CreateCodebook();
        IFloor CreateFloor(IPacket packet);
        IResidue CreateResidue(IPacket packet);
        IMapping CreateMapping(IPacket packet);
        IMode CreateMode();
        IMdct CreateMdct();
        IHuffman CreateHuffman();
    }
}
