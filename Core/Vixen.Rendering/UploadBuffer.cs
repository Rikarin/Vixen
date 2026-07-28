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
///     <para>
///         <strong>One region per frame in flight, and the caller binds at
///         <see cref="Offset" />.</strong> Writing the same bytes every frame is a race the API cannot
///         report: <c>Write</c> on a host-visible buffer is a memcpy into memory the GPU may still be
///         reading for a frame that has not finished, and the symptom is a skeleton or an instance
///         list that is briefly a blend of two frames. The ring is the same one
///         <see cref="Graphics.DescriptorAllocator" /> uses and for the same reason, and it is offsets
///         rather than shifted indices so that nothing downstream — a shader indexing from zero, a
///         push-constant base, a <c>firstInstance</c> — has to know the ring exists.
///     </para>
/// </remarks>
public sealed class UploadBuffer<T>(string name) : IDisposable where T : unmanaged {
    T[] staging = [];
    int count;
    int capacity;
    int slot;
    int slots = 1;
    BufferHandle buffer;
    bool disposed;

    /// <summary>The device the buffer lives on. Set before the first upload.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>The buffer, valid once something has been added and uploaded.</summary>
    public BufferHandle Buffer => buffer;

    /// <summary>How many records this frame holds.</summary>
    public int Count => count;

    /// <summary>How many the buffer has room for, per frame in flight.</summary>
    public int Capacity => capacity;

    /// <summary>
    ///     Where this frame's region starts, in bytes. Bind the buffer at this offset.
    /// </summary>
    /// <remarks>
    ///     A descriptor bound at zero reads the region some other frame is writing. The offset is
    ///     aligned to <see cref="Alignment" />, which is what a buffer binding's offset has to be.
    /// </remarks>
    public long Offset => (long)slot * Stride;

    /// <summary>How many bytes one frame's region occupies, including its alignment padding.</summary>
    public long Stride { get; private set; }

    /// <summary>How many frames the ring is deep.</summary>
    public int Slots => slots;

    /// <summary>
    ///     What a buffer binding's offset must be a multiple of.
    /// </summary>
    /// <remarks>
    ///     Two hundred and fifty-six, which is the largest <c>minStorageBufferOffsetAlignment</c> any
    ///     target reports — the same number and the same argument as
    ///     <see cref="Features.ForwardLightingRenderFeature.OffsetAlignment" />. Querying the device
    ///     would save memory that a handful of frames' padding does not cost.
    /// </remarks>
    public int Alignment { get; set; } = 256;

    /// <summary>What has been written this frame, for a test or an inspector.</summary>
    public ReadOnlySpan<T> Items => staging.AsSpan(0, count);

    /// <summary>Starts a frame: moves to the next region and forgets the last one's contents.</summary>
    /// <remarks>
    ///     The region this moves on to is the one used <see cref="Slots" /> frames ago, which the
    ///     device has finished with — the same invariant, and the same reason, as
    ///     <see cref="Graphics.DescriptorAllocator.BeginFrame" />.
    /// </remarks>
    public void Begin() {
        // Against the ring the buffer actually has, not the device's current answer: the two differ
        // until EnsureBuffer has built one, and advancing past the end of the buffer would write
        // where there is nothing.
        slot = (slot + 1) % slots;
        count = 0;
    }

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
        Device.Write(buffer, Offset, MemoryMarshal.AsBytes(staging.AsSpan(0, count)));
    }

    void Reserve(int required) {
        if (required <= staging.Length) {
            return;
        }

        Array.Resize(ref staging, Math.Max(required, Math.Max(staging.Length * 2, 256)));
    }

    void EnsureBuffer() {
        var wanted = Device is null ? slots : Math.Max(1, Device.FramesInFlight);

        if (Device is null || (staging.Length <= capacity && buffer.IsValid && slots == wanted)) {
            return;
        }

        // Destroyed rather than retired, and that is the one thing this still gets wrong: growing
        // frees a buffer an unfinished frame may be reading. It happens at the high-water mark and
        // then never again, so it is a hazard during warm-up rather than in a steady frame — and
        // fixing it needs a device-level retirement queue that does not exist yet.
        if (buffer.IsValid) {
            Device.Destroy(buffer);
        }

        capacity = staging.Length;
        slots = wanted;
        slot = Math.Min(slot, slots - 1);

        var bytes = (long)capacity * Unsafe.SizeOf<T>();
        var alignment = Math.Max(1, Alignment);
        Stride = (bytes + alignment - 1) / alignment * alignment;

        buffer = Device.CreateBuffer(
            new(Stride * slots, BufferUsage.Storage, MemoryAccess.HostUpload, name)
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
