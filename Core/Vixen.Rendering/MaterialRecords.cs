// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>
///     Every material of one effect, as records of one buffer.
/// </summary>
/// <remarks>
///     <para>
///         <strong>What replaces a descriptor set per material.</strong> A draw that binds a set per
///         material cannot be merged with a draw that binds a different one, which is the sentence
///         everything in <c>docs/bindless-materials.md</c> follows from. A buffer of records is bound
///         once and subscripted by a number the draw carries, so two materials' draws differ in their
///         data and in nothing a command list can see.
///     </para>
///     <para>
///         <strong>Per effect, and that is a correction to the plan.</strong> The sketch said one
///         buffer per <em>variant</em>, which turns out to be one buffer per material: a variant is a
///         <c>(material, flags, shader)</c> triple, so there is exactly one material in each. What
///         several materials genuinely share is their <em>effect</em> — same shader, same composition,
///         same permutation — and that is what a record buffer is keyed by. The layout is the
///         effect's, so every record in one buffer has the same shape by construction rather than by
///         a check.
///     </para>
///     <para>
///         <strong>Written once, not once a frame.</strong> A record is rewritten when its material's
///         values change and the buffer goes to the device when any of them did. A settled scene
///         uploads nothing, which is the same economy <see cref="EffectConstants" /> has and the same
///         reason: the bytes are the expensive part, not the bookkeeping.
///     </para>
///     <para>
///         <strong>An index, once given out, does not move.</strong> Same argument as
///         <see cref="BindlessTable" />'s: it is written into a per-object block the host has already
///         filled, and there is nothing to go back and renumber. So the buffer grows and a released
///         record leaves a hole, which is what <see cref="Count" /> counts and what a caller trades
///         against.
///     </para>
/// </remarks>
public sealed class MaterialRecords : IDisposable {
    readonly IGraphicsDevice device;
    readonly string name;
    readonly Dictionary<int, int> indices = [];

    byte[] staging = [];
    BufferHandle buffer;
    long capacity;
    bool dirty;
    bool disposed;

    /// <summary>Creates a record buffer for one effect.</summary>
    /// <param name="device">The device the buffer comes from.</param>
    /// <param name="stride">One record's size in bytes, which is the effect's own.</param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    public MaterialRecords(IGraphicsDevice device, int stride, string name = "MaterialRecords") {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);

        this.device = device;
        this.name = name;
        Stride = stride;
    }

    /// <summary>How many bytes one record occupies.</summary>
    /// <remarks>
    ///     The effect's, laid out std430 — which is what the reflection reports for a storage
    ///     buffer's element and what the shader's own <c>ArrayStride</c> decoration says. A host that
    ///     picked its own would write every record after the first at an offset the shader does not
    ///     read it from.
    /// </remarks>
    public int Stride { get; }

    /// <summary>How many records have been handed out.</summary>
    public int Count => indices.Count;

    /// <summary>The buffer, invalid until something has been written.</summary>
    public BufferHandle Buffer => buffer;

    /// <summary>How many times the records have actually gone to the GPU.</summary>
    /// <remarks>
    ///     For the test that a settled scene is not re-uploading a buffer nobody changed, which is
    ///     the whole reason the writes are compared rather than repeated.
    /// </remarks>
    public int UploadCount { get; private set; }

    /// <summary>The records as they were last filled.</summary>
    /// <remarks>
    ///     What the GPU was given. A device that took the bytes cannot be asked what they were, so
    ///     this is the only way to check that a material's values landed at the record the shader
    ///     will subscript for it.
    /// </remarks>
    public ReadOnlySpan<byte> Bytes => staging.AsSpan(0, Count * Stride);

    /// <summary>The record one variant occupies, giving it one if it has none.</summary>
    /// <param name="variant">Whatever the caller identifies a material by.</param>
    public int IndexOf(int variant) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (indices.TryGetValue(variant, out var existing)) {
            return existing;
        }

        var index = indices.Count;
        indices[variant] = index;
        Grow(index);
        return index;
    }

    /// <summary>Fills one record, and answers whether the bytes were different from last time.</summary>
    /// <param name="index">Which record, from <see cref="IndexOf" />.</param>
    /// <param name="bytes">The record's contents. Shorter than a stride is padded with what was there.</param>
    /// <remarks>
    ///     Compared rather than written unconditionally, because this runs every frame and the upload
    ///     is what costs: a material nobody touched must not put its buffer back on the bus.
    /// </remarks>
    public bool Write(int index, ReadOnlySpan<byte> bytes) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        if (bytes.Length > Stride) {
            throw new ArgumentException(
                $"A record of '{name}' is {Stride} bytes and {bytes.Length} were given. The stride is "
                + "the effect's own, so a longer record means the layout it was filled from is not "
                + "the layout the shader reads.",
                nameof(bytes)
            );
        }

        Grow(index);
        var target = staging.AsSpan(index * Stride, bytes.Length);

        if (bytes.SequenceEqual(target)) {
            return false;
        }

        bytes.CopyTo(target);
        dirty = true;
        return true;
    }

    /// <summary>Puts the records on the device, if any of them changed.</summary>
    /// <returns>The buffer, or an invalid handle when there is nothing in it.</returns>
    public BufferHandle Upload() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (Count == 0) {
            return default;
        }

        var size = (long)Count * Stride;

        if (!buffer.IsValid || capacity < size) {
            // Destroyed rather than kept: the RHI defers the free by frames-in-flight, so a buffer a
            // recorded frame still reads stays alive until it does not — which is what makes growing
            // mid-frame safe at all.
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }

            capacity = Math.Max(size, 1024);
            buffer = device.CreateBuffer(new(capacity, BufferUsage.Storage, MemoryAccess.HostUpload, name));
            dirty = true;
        }

        if (dirty) {
            device.Write(buffer, 0, staging.AsSpan(0, (int)size));
            UploadCount++;
            dirty = false;
        }

        return buffer;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (buffer.IsValid) {
            device.Destroy(buffer);
            buffer = default;
        }

        indices.Clear();
        staging = [];
    }

    void Grow(int index) {
        var required = (index + 1) * Stride;

        if (staging.Length < required) {
            Array.Resize(ref staging, Math.Max(required, staging.Length * 2));
        }
    }
}
