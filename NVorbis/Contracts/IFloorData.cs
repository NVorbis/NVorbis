namespace NVorbis.Contracts
{
    // Three independent states (execute / forced-execute / forced-skip), not one bool: channel coupling
    // means "should this channel run" has genuinely distinct reasons (own energy, coupled partner's energy,
    // wrong submap for this pass). ExecuteChannel computes the answer once from decode-time info so consumers
    // don't re-derive it from the coupling table. Don't collapse to a single bool.
    interface IFloorData
    {
        bool ExecuteChannel { get; }
        bool ForceEnergy { get; set; }
        bool ForceNoEnergy { get; set; }
    }
}
