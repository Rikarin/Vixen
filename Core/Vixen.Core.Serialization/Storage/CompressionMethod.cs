// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Serialization.Storage;

/// <summary>How a chunk's payload is stored.</summary>
/// <remarks>
///     Chosen per chunk, not per database, and recorded in the chunk's own header — so a bundle can
///     hold an LZ4 mesh next to an already-compressed texture that would only get bigger.
/// </remarks>
public enum CompressionMethod : byte {
    /// <summary>Stored as written.</summary>
    /// <remarks>
    ///     The right answer more often than it looks. BCn, ASTC and Ogg payloads are already
    ///     compressed, and a small chunk spends more bytes on a compression header than it saves.
    /// </remarks>
    None = 0,

    /// <summary>LZ4: the decode-speed choice, for content that ships on the device.</summary>
    Lz4 = 1,

    /// <summary>Zstandard: the size choice, for content that has to travel over a network.</summary>
    Zstd = 2
}
