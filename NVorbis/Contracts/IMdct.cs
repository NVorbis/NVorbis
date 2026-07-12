namespace NVorbis.Contracts
{
    /// <summary>
    /// Performs the inverse MDCT that converts a channel's spectral data into time-domain samples.
    /// </summary>
    interface IMdct
    {
        /// <summary>
        /// Applies the inverse MDCT to <paramref name="samples"/> in place.
        /// </summary>
        /// <param name="samples">
        /// On entry, the first <paramref name="sampleCount"/> / 2 entries hold the spectral
        /// coefficients for the block; on return, all <paramref name="sampleCount"/> entries hold
        /// time-domain samples (window overlap-add is the caller's responsibility).
        /// Must hold at least <paramref name="sampleCount"/> entries.
        /// </param>
        /// <param name="sampleCount">
        /// The block size; must be a power of two between 64 and 8192 (the legal Vorbis block sizes).
        /// </param>
        /// <remarks>
        /// Implementations must be safe for concurrent calls on the same instance with distinct buffers.
        /// </remarks>
        void Reverse(float[] samples, int sampleCount);
    }
}
