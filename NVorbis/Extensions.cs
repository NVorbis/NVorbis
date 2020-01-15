using NVorbis.Contracts;
using System;
using System.Collections.Generic;
using System.Text;

namespace NVorbis
{
    /// <summary>
    /// Provides extension methods for NVorbis types.
    /// </summary>
    public static class Extensions
    {
        /// <summary>
        /// Reads into the specified buffer.
        /// </summary>
        /// <param name="packet">The packet instance to use.</param>
        /// <param name="buffer">The buffer to read into.</param>
        /// <param name="index">The index into the buffer to use.</param>
        /// <param name="count">The number of bytes to read into the buffer.</param>
        /// <returns>The number of bytes actually read into the buffer.</returns>
        public static int Read(this IPacket packet, byte[] buffer, int index, int count)
        {
            if (index < 0 || index >= buffer.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (count < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
            {
                var value = (byte)packet.TryPeekBits(8, out var bitsRead);
                if (bitsRead == 0)
                {
                    return i;
                }
                buffer[index++] = value;
                packet.SkipBits(8);
            }
            return count;
        }

        /// <summary>
        /// Reads one bit from the packet and advances the read position.
        /// </summary>
        /// <returns><see langword="true"/> if the bit was a one, otehrwise <see langword="false"/>.</returns>
        public static bool ReadBit(this IPacket packet)
        {
            return packet.ReadBits(1) == 1;
        }
    }
}
