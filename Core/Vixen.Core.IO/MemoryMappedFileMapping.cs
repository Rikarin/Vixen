// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Vixen.Core.IO;

/// <summary>A file mapped into the address space, presented as <see cref="ReadOnlyMemory{T}" />.</summary>
/// <remarks>
///     <para>
///         A <see cref="MemoryManager{T}" /> rather than a copy, because the point is that there is
///         no copy. The serializer reads a bundle straight out of the page cache, and the pages it
///         never touches are never read from disk at all — which for a bundle where one asset is
///         wanted out of two hundred is most of it.
///     </para>
///     <para>
///         The mapping holds the file open. Disposing it invalidates every
///         <see cref="ReadOnlyMemory{T}" /> handed out, and reading afterwards is an access
///         violation rather than an exception, so the lifetime belongs to whoever asked for the
///         mapping.
///     </para>
/// </remarks>
sealed unsafe class MemoryMappedFileMapping : MemoryManager<byte>, IMappedFile {
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor accessor;
    readonly int length;
    byte* pointer;

    MemoryMappedFileMapping(MemoryMappedFile file, MemoryMappedViewAccessor accessor, byte* pointer, int length) {
        this.file = file;
        this.accessor = accessor;
        this.pointer = pointer;
        this.length = length;
    }

    ReadOnlyMemory<byte> IMappedFile.Memory => Memory;

    internal static MemoryMappedFileMapping Open(string osPath, int length) {
        var file = MemoryMappedFile.CreateFromFile(
            new FileStream(osPath, FileMode.Open, FileAccess.Read, FileShare.Read),
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: false
        );

        MemoryMappedViewAccessor? accessor = null;

        try {
            accessor = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

            // The view may begin before the requested offset for alignment reasons. Offset zero is
            // the only one asked for here, but reading PointerOffset rather than assuming it is the
            // difference between correct and correct-by-accident.
            return new(file, accessor, pointer + accessor.PointerOffset, length);
        } catch {
            accessor?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public override Span<byte> GetSpan() => new(pointer, length);

    public override MemoryHandle Pin(int elementIndex = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, length);

        // Already pinned, permanently: the pages are outside the collector's world.
        return new(pointer + elementIndex, default, this);
    }

    public override void Unpin() { }

    protected override void Dispose(bool disposing) {
        if (!disposing || pointer is null) {
            return;
        }

        pointer = null;
        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        accessor.Dispose();
        file.Dispose();
    }
}
