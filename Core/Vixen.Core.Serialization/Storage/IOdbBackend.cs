// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Serialization.Storage;

/// <summary>A blob a backend handed out, and the lifetime of the memory holding it.</summary>
/// <remarks>
///     Disposable because a bundle backend answers with a slice of a memory-mapped file rather than
///     a copy. The bytes stay valid until this is disposed, and not one instruction longer — which is
///     the same contract <c>Vixen.Core.IO</c>'s <c>IMappedFile</c> makes, for the same reason.
/// </remarks>
public interface IOdbBlob : IDisposable {
    /// <summary>The bytes. Valid until this is disposed.</summary>
    ReadOnlyMemory<byte> Bytes { get; }
}

/// <summary>Where chunks are kept.</summary>
/// <remarks>
///     <para>
///         Deliberately dumb. A backend maps an <see cref="ObjectId" /> to a blob and has no idea
///         what a chunk header is, what compression was used, or what type wrote it — all of that
///         belongs to <see cref="ObjectDatabase" />. That is what lets a backend be a directory, a
///         packed file, an HTTP endpoint, or a dictionary, without any of them learning the format.
///     </para>
///     <para>
///         Content addressing makes <see cref="Write" /> idempotent: the id is a function of the
///         bytes, so writing the same content twice is writing the same bytes to the same place. A
///         backend is entitled to notice that and do nothing.
///     </para>
/// </remarks>
public interface IOdbBackend {
    /// <summary>Whether this backend refuses writes.</summary>
    bool IsReadOnly { get; }

    /// <summary>Whether a chunk is here.</summary>
    /// <param name="id">The chunk.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    bool Exists(ObjectId id);

    /// <summary>Reads a chunk.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="blob">Its stored bytes, which the caller disposes.</param>
    /// <returns><see langword="false" /> if it is not here.</returns>
    bool TryRead(ObjectId id, [NotNullWhen(true)] out IOdbBlob? blob);

    /// <summary>Stores a chunk.</summary>
    /// <param name="id">The chunk.</param>
    /// <param name="blob">Its stored bytes.</param>
    /// <returns><see langword="false" /> if it was already there and nothing was written.</returns>
    /// <exception cref="NotSupportedException">The backend is read-only.</exception>
    bool Write(ObjectId id, ReadOnlySpan<byte> blob);

    /// <summary>Removes a chunk.</summary>
    /// <param name="id">The chunk.</param>
    /// <returns><see langword="false" /> if it was not here.</returns>
    /// <exception cref="NotSupportedException">The backend is read-only.</exception>
    bool Delete(ObjectId id);

    /// <summary>Every chunk here, in id order.</summary>
    /// <returns>The ids.</returns>
    IEnumerable<ObjectId> Enumerate();
}
