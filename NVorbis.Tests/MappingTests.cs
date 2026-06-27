using System.IO;
using Xunit;
using NVorbis.Contracts;

namespace NVorbis.Tests
{
    public class MappingTests
    {
        // Minimal DataPacket backed by a fixed byte array.
        // Vorbis reads bits LSB-first; DataPacket handles the bit unpacking.
        private class ByteArrayPacket : DataPacket
        {
            private readonly byte[] _data;
            private int _pos;

            public ByteArrayPacket(byte[] data) => _data = data;

            protected override int TotalBits => _data.Length * 8;
            protected override int ReadNextByte() => _pos < _data.Length ? _data[_pos++] : -1;
        }

        // Builds a two-byte mapping header for a 2-channel, 2-submap stream
        // with no coupling and the given mux[0] value (4 bits, LSB-first).
        //
        // Bit layout (LSB-first within each byte):
        //   byte 0: bit0=1 (hasSubmaps), bits1-4=0001 (submapCount-1=1→count=2),
        //           bit5=0 (noCoupling), bits6-7=00 (reserved)
        //   byte 1: bits8-11=mux[0], bits12-15=mux[1]=0
        private static byte[] MuxHeader(int mux0Value)
        {
            // byte 0 = 0b00000011 = 3 (fixed for this configuration)
            // byte 1: mux[0] in bits 0-3
            return new byte[] { 0x03, (byte)(mux0Value & 0x0F) };
        }

        [Fact]
        public void MuxEqualToSubmapCount_ThrowsInvalidDataException()
        {
            // mux[0] = 2, submapCount = 2  →  index equals the array length: OOB
            var packet = new ByteArrayPacket(MuxHeader(mux0Value: 2));
            var mapping = new Mapping();

            Assert.Throws<InvalidDataException>(() =>
                mapping.Init(packet, channels: 2,
                    floors: new IFloor[0], residues: new IResidue[0], mdct: null));
        }

        [Fact]
        public void MuxGreaterThanSubmapCount_ThrowsInvalidDataException()
        {
            // mux[0] = 3, submapCount = 2  →  was already caught before the fix
            var packet = new ByteArrayPacket(MuxHeader(mux0Value: 3));
            var mapping = new Mapping();

            Assert.Throws<InvalidDataException>(() =>
                mapping.Init(packet, channels: 2,
                    floors: new IFloor[0], residues: new IResidue[0], mdct: null));
        }
    }
}
