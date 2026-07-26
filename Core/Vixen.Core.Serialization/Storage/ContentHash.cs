// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Hashing;

namespace Vixen.Core.Serialization.Storage;

/// <summary>Turns bytes into the <see cref="ObjectId" /> that names them.</summary>
/// <remarks>
///     <para>
///         This is the function <c>Vixen.Core</c> deliberately does not have.
///         <see cref="ObjectId" /> is 128 bits of identity, formatting, parsing and ordering, and
///         nothing else — because XxHash128 lives in a NuGet package and taking it would have cost
///         <c>Vixen.Core</c>'s "no dependencies beyond BCL" rule for a type that does not need the
///         algorithm. The code that hashes has the content in front of it, and that code is here.
///     </para>
///     <para>
///         Non-cryptographic on purpose. An id is a name, not a signature: it exists so that two
///         identical chunks are one chunk, so that a corrupted read is noticed, and so that an
///         update can tell what changed. Anything that needs to resist a deliberate collision needs
///         a signature over the bundle, which is a shipping concern and not this one.
///     </para>
/// </remarks>
public static class ContentHash {
    /// <summary>Hashes bytes into the id that names them.</summary>
    /// <param name="content">The bytes.</param>
    /// <returns>The id.</returns>
    public static ObjectId Compute(ReadOnlySpan<byte> content) {
        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(content, hash);

        // Big-endian halves, so that the id's hexadecimal text reads in the same order as the bytes
        // XxHash128 produced. ObjectId's ToString and WriteTo already agree on big-endian; this is
        // what makes ids from two machines comparable as text and as bytes.
        return new(
            BinaryPrimitives.ReadUInt64BigEndian(hash),
            BinaryPrimitives.ReadUInt64BigEndian(hash[8..])
        );
    }

    /// <summary>Hashes a type's name into the identifier a chunk header carries.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The identifier.</returns>
    /// <remarks>
    ///     A hash of the name rather than an index into a table, because there is no table yet:
    ///     <c>Vixen.Core.Reflection</c> is what builds one, and it will be able to key on exactly
    ///     this value. Hashing the name means the identifier is stable across builds, across
    ///     assemblies, and across the order in which types happened to be registered — none of
    ///     which is true of an index handed out at start-up.
    /// </remarks>
    public static ulong TypeId(Type type) {
        ArgumentNullException.ThrowIfNull(type);
        var name = type.FullName ?? type.Name;
        Span<byte> hash = stackalloc byte[8];
        XxHash64.Hash(System.Text.Encoding.UTF8.GetBytes(name), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
