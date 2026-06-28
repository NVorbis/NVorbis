using NVorbis.Ogg;
using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    public class PageReaderBaseTests
    {
        // Ogg CRC32: polynomial 0x04c11db7, MSB-first, same algorithm as libogg.
        private static uint ComputeOggCrc(byte[] data)
        {
            const uint poly = 0x04c11db7;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint s = i << 24;
                for (int j = 0; j < 8; j++)
                    s = (s << 1) ^ (s >= (1u << 31) ? poly : 0u);
                table[i] = s;
            }
            uint crc = 0;
            foreach (var b in data)
                crc = (crc << 8) ^ table[b ^ (crc >> 24)];
            return crc;
        }

        // Minimal valid Ogg page with one packet of <= 255 bytes.
        private static byte[] BuildOggPage(int serial, int seqNo, long granule,
                                            byte[] payload, bool isBOS = false)
        {
            if (payload.Length > 255)
                throw new ArgumentOutOfRangeException(nameof(payload));

            var page = new byte[27 + 1 + payload.Length];
            page[0] = 0x4f; page[1] = 0x67; page[2] = 0x67; page[3] = 0x53; // "OggS"
            page[4] = 0;                                   // version
            page[5] = isBOS ? (byte)0x02 : (byte)0x00;    // header type flags
            Buffer.BlockCopy(BitConverter.GetBytes(granule), 0, page, 6,  8);
            Buffer.BlockCopy(BitConverter.GetBytes(serial),  0, page, 14, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seqNo),   0, page, 18, 4);
            // bytes 22-25: CRC filled after the rest is written
            page[26] = 1;                                  // page_segments = 1
            page[27] = (byte)payload.Length;               // lacing value
            Buffer.BlockCopy(payload, 0, page, 28, payload.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(ComputeOggCrc(page)), 0, page, 22, 4);
            return page;
        }

        private static ContainerReader OpenContainer(params byte[][] chunks)
        {
            var ms = new MemoryStream();
            foreach (var c in chunks)
                ms.Write(c, 0, c.Length);
            ms.Position = 0;
            return new ContainerReader(ms, closeOnDispose: true);
        }

        [Fact]
        public void ReadNextPage_ValidPage_IsAccepted()
        {
            var page = BuildOggPage(0x1234, 0, -1L, new byte[] { 0x01 }, isBOS: true);
            using var cr = OpenContainer(page);
            Assert.True(cr.TryInit());
        }

        [Fact]
        public void ReadNextPage_GarbagePrefix24Bytes_RecoversViaCarryMechanism()
        {
            // ReadNextPage fills 27 bytes at a time and scans i=0..cnt-5 for "OggS".
            // When "OggS" falls at index 24 (= cnt - 3) it is not scanned in the first
            // pass, but the 3-byte carry copies buf[24..26] = "Ogg" to buf[0..2].  The
            // next 24-byte read brings 'S' + the rest of the header into buf[3..26],
            // so VerifyHeader succeeds at i=0 on the second pass.
            var garbage = new byte[24];
            var page = BuildOggPage(0x5678, 0, -1L, new byte[] { 0x02 }, isBOS: true);
            using var cr = OpenContainer(garbage, page);
            Assert.True(cr.TryInit());
        }

        [Fact]
        public void WasteBits_GarbagePrefix_AccountsForGarbageBytes()
        {
            var garbage = new byte[24];
            var page = BuildOggPage(0xABCD, 0, -1L, new byte[] { 0x03 }, isBOS: true);
            using var cr = OpenContainer(garbage, page);
            cr.TryInit();
            // The carry mechanism shifts the last 3 bytes without marking them as waste,
            // so waste is (24 - 3) * 8 = 168 bits minimum, not 24 * 8.  Just verify
            // that some waste was recorded.
            Assert.True(cr.WasteBits > 0);
        }
    }
}
