using NVorbis.Contracts;

namespace NVorbis
{
    // each channel gets its own pass, with the dimensions interleaved
    // Inherits ~95% shared partition/classification/cascade logic from Residue0; only the vector-scatter
    // differs, so only WriteVectors is overridden. Keep new type-specific logic scoped to the override.
    class Residue1 : Residue0
    {
        protected override bool WriteVectors(ICodebook codebook, IPacket packet, float[][] residue, int channel, int offset, int partitionSize)
        {
            var res = residue[channel];

            for (int i = 0; i < partitionSize;)
            {
                var entry = codebook.DecodeScalar(packet);
                if (entry == -1)
                {
                    return true;
                }
                // codebook.Dimensions >= 1 is guaranteed by Codebook.Init; i always advances
                for (int j = 0; j < codebook.Dimensions; i++, j++)
                {
                    res[offset + i] += codebook[entry, j];
                }
            }

            return false;
        }
    }
}
