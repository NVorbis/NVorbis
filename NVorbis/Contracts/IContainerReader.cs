using System;
using System.Collections.Generic;

namespace NVorbis.Contracts
{
    /// <summary>
    /// Provides an interface for a Vorbis logical stream container.
    /// </summary>
    public interface IContainerReader : IDisposable
    {
        /// <summary>
        /// Gets or sets the callback to invoke when a new stream is encountered in the container.
        /// </summary>
        Func<IPacketProvider, bool> NewStreamCallback { get; set; }

        /// <summary>
        /// Gets a read-only list of the logical streams discovered in this container.
        /// </summary>
        IReadOnlyList<IPacketProvider> Streams { get; }

        /// <summary>
        /// Gets the number of bits dedicated to container framing and overhead.
        /// </summary>
        long ContainerBits { get; }

        /// <summary>
        /// Gets the number of bits that were skipped due to container framing and overhead.
        /// </summary>
        long WasteBits { get; }

        /// <summary>
        /// Attempts to initialize the container.
        /// </summary>
        /// <returns><see langword="true"/> if successful, otherwise <see langword="false"/>.</returns>
        bool TryInit();

        /// <summary>
        /// Searches for the next logical stream in the container.
        /// </summary>
        /// <returns><see langword="true"/> if a new stream was found, otherwise <see langword="false"/>.</returns>
        bool FindNextStream();
    }
}
