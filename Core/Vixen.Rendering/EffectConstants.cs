// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     One effect's uniform block, filled from a <see cref="ParameterCollection" />.
/// </summary>
/// <remarks>
///     <para>
///         The other half of the effect system, and the first thing in the engine to use it. An
///         <see cref="Effect" /> carries <see cref="Effect.ConstantBufferSize" /> and a
///         <see cref="EffectParameter" /> per value it declares — a key, an offset and a size — which
///         is exactly a layout for the block a shader reads. This is what turns that table plus a
///         collection of values into bytes on the GPU.
///     </para>
///     <para>
///         <strong>Every parameter is written, not only the ones somebody set.</strong> A shader
///         declaring <c>var exposure: float = 1f</c> and a host that never mentions exposure must get
///         one, not zero — and zero exposure is a black frame with no error anywhere. The default
///         comes off the key, which is where the generator put what the shader declared.
///     </para>
///     <para>
///         <strong>It re-uploads when the values change, not every frame.</strong>
///         <see cref="ParameterCollection.Version" /> exists for this, and the effect is part of the
///         comparison as well: resolving a different variant changes the layout, so the same version
///         against a different effect is a different block.
///     </para>
/// </remarks>
public sealed class EffectConstants(IGraphicsDevice device, string name = "Constants") : IDisposable {
    byte[] staging = [];
    BufferHandle buffer;
    object? uploaded;
    int version = -1;
    int capacity;
    int slot;
    int slots = 1;
    bool disposed;

    /// <summary>The buffer the block lives in, invalid until something has been uploaded.</summary>
    public BufferHandle Buffer => buffer;

    /// <summary>
    ///     Where this frame's block starts, in bytes. Bind the buffer here rather than at zero.
    /// </summary>
    /// <remarks>
    ///     The buffer holds one block per frame in flight, because a block whose values change is one
    ///     rewritten while an unfinished frame may be reading it — and a uniform read half from one
    ///     frame and half from another is a value that was never set anywhere. The same ring, and the
    ///     same argument, as <see cref="DescriptorAllocator" />'s.
    /// </remarks>
    public long Offset => (long)slot * Stride;

    /// <summary>How many bytes one frame's block occupies, including its alignment padding.</summary>
    public long Stride { get; private set; }

    /// <summary>What a uniform binding's offset must be a multiple of.</summary>
    public int Alignment { get; set; } = 256;

    /// <summary>How many bytes the current block is.</summary>
    public int Size { get; private set; }

    /// <summary>The block as it was last filled.</summary>
    /// <remarks>
    ///     What the GPU was given, which is the only way to check that a parameter landed at the
    ///     offset the reflection said — a device that took the bytes cannot be asked what they were.
    /// </remarks>
    public ReadOnlySpan<byte> Bytes => staging.AsSpan(0, Math.Min(Size, staging.Length));

    /// <summary>How many times the bytes have actually gone to the GPU.</summary>
    /// <remarks>
    ///     For the test that the frame is not re-uploading a block nobody changed, which is the whole
    ///     reason the version is compared at all.
    /// </remarks>
    public int UploadCount { get; private set; }

    /// <summary>Fills and uploads the block if anything changed, and returns whether there is one.</summary>
    /// <param name="effect">The variant whose layout the block has.</param>
    /// <param name="parameters">The values to fill it from.</param>
    /// <returns>False when the effect declares no constants, so there is nothing to bind.</returns>
    public bool Update(Effect effect, ParameterCollection parameters) {
        ArgumentNullException.ThrowIfNull(effect);
        return Update(effect, effect.ConstantBufferSize, effect.Parameters.AsSpan(), parameters);
    }

    /// <summary>
    ///     Fills and uploads a block whose layout does not come from an effect.
    /// </summary>
    /// <param name="layout">
    ///     What identifies this layout. Compared by reference to decide whether the block's shape
    ///     changed — an <see cref="Effect" /> for a material's block, and whatever owns the layout for
    ///     a block shared across effects.
    /// </param>
    /// <param name="size">The block's size in bytes.</param>
    /// <param name="members">Where each value goes.</param>
    /// <param name="parameters">The values to fill it from.</param>
    /// <remarks>
    ///     A per-view block is the case this exists for: every shader in a frame reads the same one,
    ///     so it belongs to no single effect — which is exactly what the four-set convention says
    ///     about set 1.
    /// </remarks>
    public bool Update(
        object layout,
        int size,
        ReadOnlySpan<EffectParameter> members,
        ParameterCollection parameters
    ) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (size <= 0) {
            return false;
        }

        Size = size;

        if (ReferenceEquals(uploaded, layout) && version == parameters.Version && buffer.IsValid) {
            return true;
        }

        // A new block goes in the next region rather than over the last one. Only when something
        // changed: a frame that re-asserts the same values keeps reading the region it already has,
        // which is what makes the ring cost nothing in the common case.
        slot = slots <= 1 ? 0 : (slot + 1) % slots;

        if (staging.Length < Size) {
            staging = new byte[Size];
        }

        // Cleared rather than left as the last fill: a parameter the previous effect had and this one
        // does not would otherwise stay in the block, at an offset this variant reads as something
        // else entirely.
        Array.Clear(staging, 0, Size);

        foreach (var parameter in members) {
            Write(parameter);
        }

        Recreate();
        device.Write(buffer, Offset, staging.AsSpan(0, Size));

        uploaded = layout;
        version = parameters.Version;
        UploadCount++;
        return true;

        void Write(in EffectParameter parameter) {
            if (parameter.Offset < 0 || parameter.Size <= 0 || parameter.Offset + parameter.Size > Size) {
                // A parameter that does not fit the block it belongs to is a mismatch between the
                // reflection and the size beside it. Skipped rather than thrown, because the frame
                // that notices is the wrong place to find out and the other parameters are still
                // right — the effect provider is where this would be caught.
                return;
            }

            var destination = staging.AsSpan(parameter.Offset, parameter.Size);
            var bytes = parameters.Bytes(parameter.Key);

            if (bytes.IsEmpty) {
                bytes = parameter.Key.DefaultBytes;
            }

            // Shorter is a value narrower than the slot — a float into a float4's first component,
            // which is what a scalar in a std140 block is. Longer cannot happen from a key of the
            // right type, and clamping is cheaper than trusting it.
            bytes[..Math.Min(bytes.Length, destination.Length)].CopyTo(destination);
        }
    }

    void Recreate() {
        var wanted = Math.Max(1, device.FramesInFlight);

        if (buffer.IsValid && capacity >= Size && slots == wanted) {
            return;
        }

        if (buffer.IsValid) {
            device.Destroy(buffer);
        }

        capacity = Size;
        slots = wanted;
        slot = Math.Min(slot, slots - 1);

        var alignment = Math.Max(1, Alignment);
        Stride = (Size + alignment - 1) / alignment * alignment;

        buffer = device.CreateBuffer(
            new(Stride * slots, BufferUsage.Uniform, MemoryAccess.HostUpload, name)
        );
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
    }
}
