// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.IO;

namespace Vixen.Core.Serialization.Storage;

/// <summary>Loose files under a virtual directory, one per chunk. The edit-time backend.</summary>
/// <remarks>
///     <para>
///         A chunk lands at <c>&lt;root&gt;/ab/cdef…</c> — the first byte of the id as a directory,
///         the rest as the file name. That is git's layout and it is here for git's reason: a
///         project accumulates hundreds of thousands of artefacts, and a directory with all of them
///         in it is slow to enumerate on every filesystem and unusable on some.
///     </para>
///     <para>
///         Writes are skipped when the file already exists. Content addressing means an existing
///         file with that name holds exactly those bytes, so rewriting it is work with no effect —
///         and at edit time, where most of a reimport produces artefacts identical to the last one,
///         that is most of the writes.
///     </para>
///     <para>
///         ⚠ <b>Two threads writing one id is the ordinary case, not an exotic one</b>, and it used
///         to fail an asset. Content addressing means two assets that import to identical bytes
///         produce identical ids on purpose — so a parallel import reaches this with the same id from
///         two threads, both see the file absent, and both open it. The provider opens with
///         <c>FileShare.None</c>, so the loser gets a sharing violation reported as
///         "&lt;importer&gt; threw", about a file that is perfectly good. <see cref="Write" /> claims
///         an id before it opens anything and the second caller is told what it would have been told
///         if the first had already finished: <see langword="false" />, nothing to do.
///     </para>
/// </remarks>
public sealed class FileOdbBackend : IOdbBackend {
    readonly VirtualFileSystem files;
    readonly VirtualPath root;
    readonly Lock gate = new();
    readonly HashSet<ObjectId> writing = [];

    /// <inheritdoc />
    public bool IsReadOnly { get; }

    /// <summary>Where the chunks live.</summary>
    public VirtualPath Root => root;

    /// <summary>Serves chunks from a virtual directory.</summary>
    /// <param name="files">The file system.</param>
    /// <param name="root">The directory, usually <c>/db</c>.</param>
    /// <param name="isReadOnly">Whether to refuse writes.</param>
    public FileOdbBackend(VirtualFileSystem files, VirtualPath root, bool isReadOnly = false) {
        ArgumentNullException.ThrowIfNull(files);

        if (root.IsEmpty) {
            throw new ArgumentException("The backend needs a root.", nameof(root));
        }

        this.files = files;
        this.root = root;
        IsReadOnly = isReadOnly;
    }

    /// <inheritdoc />
    public bool Exists(ObjectId id) => files.Exists(PathOf(id));

    /// <inheritdoc />
    public bool TryRead(ObjectId id, [NotNullWhen(true)] out IOdbBlob? blob) {
        var path = PathOf(id);

        // Mapped where the provider can, because a chunk is read far more often than it is written
        // and the pages the loader never touches are never faulted in.
        if (files.TryMap(path, out var mapped)) {
            blob = new MappedBlob(mapped);
            return true;
        }

        if (!files.Exists(path)) {
            blob = null;
            return false;
        }

        using var stream = files.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        blob = new ArrayBlob(buffer.ToArray());
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The claim covers the whole write and not just the existence check, because the window that
    ///     matters is the one between opening the file and finishing it — that is where a second
    ///     caller would collide with a half-written chunk rather than with an absent one.
    /// </remarks>
    public bool Write(ObjectId id, ReadOnlySpan<byte> blob) {
        ThrowIfReadOnly();
        var path = PathOf(id);

        lock (gate) {
            if (files.Exists(path) || !writing.Add(id)) {
                return false;
            }
        }

        try {
            using var stream = files.OpenWrite(path);
            stream.Write(blob);
        } finally {
            lock (gate) {
                writing.Remove(id);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Delete(ObjectId id) {
        ThrowIfReadOnly();
        return files.Delete(PathOf(id));
    }

    /// <inheritdoc />
    public IEnumerable<ObjectId> Enumerate() {
        foreach (var entry in files.Enumerate(root, recursive: true)) {
            if (!entry.IsDirectory && TryParseId(entry.Path, out var id)) {
                yield return id;
            }
        }
    }

    static bool TryParseId(VirtualPath path, out ObjectId id) {
        var name = path.FileName;
        var prefix = path.Parent.FileName;

        if (name.Length + prefix.Length != ObjectId.TextLength) {
            id = default;
            return false;
        }

        Span<char> text = stackalloc char[ObjectId.TextLength];
        prefix.CopyTo(text);
        name.CopyTo(text[prefix.Length..]);
        return ObjectId.TryParse(text, null, out id);
    }

    VirtualPath PathOf(ObjectId id) {
        Span<char> text = stackalloc char[ObjectId.TextLength];
        id.TryFormat(text, out _, default, null);
        return root / string.Concat(text[..2], "/", text[2..]);
    }

    void ThrowIfReadOnly() {
        if (IsReadOnly) {
            throw new NotSupportedException($"The object database at '{root.Value}' is read-only.");
        }
    }

    sealed class ArrayBlob(byte[] bytes) : IOdbBlob {
        public ReadOnlyMemory<byte> Bytes => bytes;

        public void Dispose() { }
    }

    sealed class MappedBlob(IMappedFile mapped) : IOdbBlob {
        public ReadOnlyMemory<byte> Bytes => mapped.Memory;

        public void Dispose() => mapped.Dispose();
    }
}
