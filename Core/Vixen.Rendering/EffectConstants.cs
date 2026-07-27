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
    Effect? uploaded;
    int version = -1;
    int capacity;
    bool disposed;

    /// <summary>The buffer the block lives in, invalid until something has been uploaded.</summary>
    public BufferHandle Buffer => buffer;

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
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (effect.ConstantBufferSize <= 0) {
            return false;
        }

        Size = effect.ConstantBufferSize;

        if (ReferenceEquals(uploaded, effect) && version == parameters.Version && buffer.IsValid) {
            return true;
        }

        if (staging.Length < Size) {
            staging = new byte[Size];
        }

        // Cleared rather than left as the last fill: a parameter the previous effect had and this one
        // does not would otherwise stay in the block, at an offset this variant reads as something
        // else entirely.
        Array.Clear(staging, 0, Size);

        foreach (var parameter in effect.Parameters) {
            Write(parameter);
        }

        Recreate();
        device.Write(buffer, 0, staging.AsSpan(0, Size));

        uploaded = effect;
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
        if (buffer.IsValid && capacity >= Size) {
            return;
        }

        if (buffer.IsValid) {
            device.Destroy(buffer);
        }

        capacity = Size;
        buffer = device.CreateBuffer(new(Size, BufferUsage.Uniform, MemoryAccess.HostUpload, name));
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
