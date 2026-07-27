// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>
///     One frame's worth of unmanaged records in a storage buffer, addressed by index.
/// </summary>
/// <typeparam name="T">The element. Blittable, because an upload is a blit.</typeparam>
/// <remarks>
///     <para>
///         Shared by skinning, instancing and the clustered light path, which want the same thing: a
///         run of records written once a frame and indexed by a shader. A storage buffer rather than
///         a uniform block because all three overrun a uniform block's guaranteed 16 KB immediately —
///         that is 256 matrices, which is one skeleton or one small crowd.
///     </para>
///     <para>
///         <strong>Indices, not offsets.</strong> Callers get the index of their first record and the
///         shader indexes the array, so nothing here has to know about
///         <c>minStorageBufferOffsetAlignment</c> and no run is padded up to it. The features then
///         get the index to the shader by different routes — a push constant for skinning, the draw
///         call's own <c>firstInstance</c> for instancing, and nothing at all for a light list every
///         fragment reads whole.
///     </para>
///     <para>
///         Refilled from scratch every frame. Retaining ranges across frames would mean tracking
///         which objects changed and compacting the holes left by the ones that went away, which is a
///         defragmenter in the frame path to save writing a few thousand matrices a host already had
///         to compute.
///     </para>
/// </remarks>
public sealed class UploadBuffer<T>(string name) : IDisposable where T : unmanaged {
    T[] staging = [];
    int count;
    int capacity;
    BufferHandle buffer;
    bool disposed;

    /// <summary>The device the buffer lives on. Set before the first upload.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>The buffer, valid once something has been added and uploaded.</summary>
    public BufferHandle Buffer => buffer;

    /// <summary>How many records this frame holds.</summary>
    public int Count => count;

    /// <summary>How many the buffer has room for.</summary>
    public int Capacity => capacity;

    /// <summary>What has been written this frame, for a test or an inspector.</summary>
    public ReadOnlySpan<T> Items => staging.AsSpan(0, count);

    /// <summary>Forgets last frame's contents.</summary>
    public void Begin() => count = 0;

    /// <summary>Appends a run and returns the index of its first matrix.</summary>
    public int Add(ReadOnlySpan<T> items) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (items.IsEmpty) {
            return count;
        }

        Reserve(count + items.Length);

        var first = count;
        items.CopyTo(staging.AsSpan(first));
        count += items.Length;

        return first;
    }

    /// <summary>Writes this frame's records to the device.</summary>
    /// <remarks>
    ///     One write for the whole frame. The runs are contiguous by construction, and a call into
    ///     the driver costs far more than the bytes do.
    /// </remarks>
    public void Upload() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Device is null || count == 0) {
            return;
        }

        EnsureBuffer();
        Device.Write(buffer, 0, MemoryMarshal.AsBytes(staging.AsSpan(0, count)));
    }

    void Reserve(int required) {
        if (required <= staging.Length) {
            return;
        }

        Array.Resize(ref staging, Math.Max(required, Math.Max(staging.Length * 2, 256)));
    }

    void EnsureBuffer() {
        if (Device is null || staging.Length <= capacity && buffer.IsValid) {
            return;
        }

        if (buffer.IsValid) {
            Device.Destroy(buffer);
        }

        capacity = staging.Length;

        buffer = Device.CreateBuffer(
            new(
                (long)capacity * Unsafe.SizeOf<T>(),
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                name
            )
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (buffer.IsValid) {
            Device?.Destroy(buffer);
            buffer = default;
        }

        staging = [];
        count = 0;
        capacity = 0;
    }
}
