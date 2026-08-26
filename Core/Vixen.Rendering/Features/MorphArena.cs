// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Rendering.Features;

/// <summary>
///     A device-local buffer of fixed-stride records, suballocated, staged and state-tracked.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="GeometryBuffer" />'s vertex half, without the index half and with a state
///         that moves.</b> Both hold many callers' records in one allocation, both free-list with
///         coalescing, and both stage a host write and record the copy later. What that type cannot
///         be is this: its buffer is a vertex buffer that settles in
///         <see cref="ResourceState.VertexInput" /> and stays there, and both of
///         <see cref="MorphRenderFeature" />'s buffers change state within a frame — one is written
///         by a dispatch and read by a draw, the other is copied into and then read by a dispatch.
///     </para>
///     <para>
///         ⚠ <b>Allocation is rounded up to <see cref="Alignment" /> records, and that is a binding
///         rule rather than a tidiness one.</b> A storage buffer bound at a run's own offset needs
///         that offset to be a multiple of <c>minStorageBufferOffsetAlignment</c> — up to 256 bytes
///         on the devices that report the most — so the entry arena aligns to sixteen sixteen-byte
///         records and the vertex arena, which is always bound whole and addressed by the kernel's
///         <c>baseVertex</c>, aligns to one.
///     </para>
///     <para>
///         ⚠ <b>Fixed capacity, refused rather than grown</b>, for the reason
///         <see cref="GeometryBuffer" /> gives at length: the handle is already in every
///         <see cref="MeshDraw" /> that was attached, so growing means finding and rewriting all of
///         them or leaving draws pointing at a destroyed buffer.
///     </para>
/// </remarks>
sealed class MorphArena : IDisposable {
    readonly IGraphicsDevice device;
    readonly string name;

    // Free ranges, sorted by offset so a release coalesces with its neighbours. A list rather than a
    // tree, GeometryBuffer's reasoning: there are meshes and instances of them, so a linear first-fit
    // over a handful of ranges beats what a tree would cost to maintain.
    readonly List<(int Offset, int Count)> free = [];
    readonly List<(long Source, long Destination, long Size)> pending = [];

    BufferHandle staging;
    long stagingUsed;
    long stagingCapacity;
    ResourceState state = ResourceState.Undefined;
    bool disposed;

    /// <summary>Creates the buffer and the free list that covers it.</summary>
    /// <param name="device">The device.</param>
    /// <param name="stride">How many bytes one record occupies.</param>
    /// <param name="capacity">How many records fit.</param>
    /// <param name="alignment">What a run's first record index is rounded to.</param>
    /// <param name="usage">What the buffer is for, besides being a copy destination.</param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    public MorphArena(
        IGraphicsDevice device,
        int stride,
        int capacity,
        int alignment,
        BufferUsage usage,
        string name
    ) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        this.device = device;
        this.name = name;

        Stride = stride;
        Capacity = capacity;
        Alignment = alignment;

        free.Add((0, capacity));

        Buffer = device.CreateBuffer(new((long)capacity * stride, usage, MemoryAccess.DeviceLocal, name));
    }

    /// <summary>The buffer everything here lives in.</summary>
    public BufferHandle Buffer { get; }

    /// <summary>How many bytes one record occupies.</summary>
    public int Stride { get; }

    /// <summary>How many records fit.</summary>
    public int Capacity { get; }

    /// <summary>What a run's first record index is rounded to.</summary>
    public int Alignment { get; }

    /// <summary>How many records are allocated, before rounding.</summary>
    public int Used { get; private set; }

    /// <summary>How many free ranges the space is in, which is what fragmentation looks like.</summary>
    public int FragmentCount => free.Count;

    /// <summary>What the buffer was last left in.</summary>
    public ResourceState State => state;

    /// <summary>Finds room for a run of records, or answers that there is none.</summary>
    /// <param name="count">How many records.</param>
    /// <param name="first">The index of its first record.</param>
    /// <returns>Whether it fitted.</returns>
    public bool TryAllocate(int count, out int first) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        first = 0;

        var wanted = Rounded(count);

        for (var index = 0; index < free.Count; index++) {
            var (offset, available) = free[index];

            if (available < wanted) {
                continue;
            }

            first = offset;

            if (available == wanted) {
                free.RemoveAt(index);
            } else {
                free[index] = (offset + wanted, available - wanted);
            }

            Used += count;

            return true;
        }

        return false;
    }

    /// <summary>Gives a run back, coalescing with whatever is free beside it.</summary>
    /// <param name="first">What <see cref="TryAllocate" /> handed out.</param>
    /// <param name="count">How many records it was for.</param>
    public void Free(int first, int count) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (count <= 0) {
            return;
        }

        Used -= count;
        Release(first, Rounded(count));
    }

    /// <summary>Stages a run's bytes, to be copied by the next <see cref="Flush" />.</summary>
    /// <param name="first">Which record they start at.</param>
    /// <param name="data">The bytes. Need not fill the whole rounded-up run.</param>
    /// <returns>Whether they fitted in this flush's staging region.</returns>
    /// <exception cref="ArgumentException">The bytes overrun the buffer itself.</exception>
    /// <remarks>
    ///     ⚠ <b>False rather than a throw, because the region can only be grown while nothing refers
    ///     to it.</b> Growing it with bytes already staged would move memory a recorded copy names, and
    ///     throwing would be an exception in the middle of an extraction that had already settled half
    ///     its entities. <see cref="GeometryBuffer.CanStage" /> makes the same argument and answers it
    ///     by being asked; this answers it by refusing, because its caller has something useful to do
    ///     with a refusal and nothing useful to do with a prediction.
    ///
    ///     An empty region always fits, however large the write, since nothing refers to it and it is
    ///     simply grown — so only the second and later meshes of one frame can be refused, and only
    ///     when their deltas together exceed what the region has grown to.
    /// </remarks>
    public bool Write(int first, ReadOnlySpan<byte> data) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var at = (long)first * Stride;

        if (at + data.Length > (long)Capacity * Stride) {
            throw new ArgumentException(
                $"'{name}' was given {data.Length} bytes at record {first}, which runs past the end of "
                + $"{Capacity} records of {Stride}.",
                nameof(data)
            );
        }

        if (!EnsureStaging(stagingUsed + data.Length)) {
            return false;
        }

        device.Write(staging, stagingUsed, data);
        pending.Add((stagingUsed, at, data.Length));
        stagingUsed += data.Length;

        return true;
    }

    /// <summary>Records the copies everything staged needs, and leaves the buffer in a state.</summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <param name="into">What the buffer is about to be used as.</param>
    /// <returns>How many copies were recorded.</returns>
    /// <remarks>
    ///     ⚠ The staging region is reset here, so the bytes must reach the device before the next
    ///     <see cref="Write" /> overwrites them — which is what recording the copy into a list and
    ///     submitting that list means. <see cref="GeometryBuffer.Flush" /> carries the same warning
    ///     and it is the same hazard.
    /// </remarks>
    public int Flush(ICommandList list, ResourceState into) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (pending.Count == 0) {
            Transition(list, into);
            return 0;
        }

        Transition(list, ResourceState.CopyDestination);

        foreach (var (source, destination, size) in pending) {
            list.CopyBuffer(staging, source, Buffer, destination, size);
        }

        Transition(list, into);

        var count = pending.Count;

        pending.Clear();
        stagingUsed = 0;

        return count;
    }

    /// <summary>Moves the buffer into another state, and remembers that it did.</summary>
    /// <param name="list">An open command list.</param>
    /// <param name="into">What it is about to be used as.</param>
    /// <remarks>
    ///     A transition to the state it is already in records nothing — see
    ///     <see cref="GeometryBuffer.Transition" />, which says why that is not merely an
    ///     optimisation.
    /// </remarks>
    public void Transition(ICommandList list, ResourceState into) {
        ArgumentNullException.ThrowIfNull(list);

        if (state == into) {
            return;
        }

        list.Barrier(new([new(Buffer, state, into)], []));
        state = into;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (staging.IsValid) {
            device.Destroy(staging);
            staging = default;
        }

        device.Destroy(Buffer);
    }

    int Rounded(int count) => ((count + Alignment - 1) / Alignment) * Alignment;

    bool EnsureStaging(long bytes) {
        if (staging.IsValid && bytes <= stagingCapacity) {
            return true;
        }

        if (stagingUsed > 0) {
            // Growing while something is staged would move the bytes a recorded copy refers to.
            return false;
        }

        if (staging.IsValid) {
            device.Destroy(staging);
        }

        stagingCapacity = Math.Max(bytes, 64 * 1024);

        staging = device.CreateBuffer(
            new(stagingCapacity, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{name}.Staging")
        );

        return true;
    }

    void Release(int offset, int count) {
        var index = 0;

        while (index < free.Count && free[index].Offset < offset) {
            index++;
        }

        free.Insert(index, (offset, count));

        if (index + 1 < free.Count && free[index].Offset + free[index].Count == free[index + 1].Offset) {
            free[index] = (free[index].Offset, free[index].Count + free[index + 1].Count);
            free.RemoveAt(index + 1);
        }

        if (index > 0 && free[index - 1].Offset + free[index - 1].Count == free[index].Offset) {
            free[index - 1] = (free[index - 1].Offset, free[index - 1].Count + free[index].Count);
            free.RemoveAt(index);
        }
    }
}
