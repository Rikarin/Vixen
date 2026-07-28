// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Graphics.WebGPU.Browser;

/// <summary>A descriptor, flattened into bytes for one trip across the interop boundary.</summary>
/// <remarks>
///     <para>
///         <b>Why not one <c>[JSImport]</c> parameter per field?</b> Because a render pipeline
///         descriptor has around sixty of them, most of them nested in arrays whose length is not
///         known until run time, and the marshaller has no shape for that. The choices are a call per
///         field — dozens of boundary crossings to create one pipeline — or a JSON string, which
///         means allocating and parsing text on a path that runs while the frame budget is ticking.
///     </para>
///     <para>
///         So: a byte buffer, written here and read by a <c>DataView</c> on the other side. Every
///         layout is stated in a comment at the method that writes it, and the JavaScript reader
///         repeats it — two places, deliberately, because a mismatch between them is silent and the
///         comment is what makes it findable.
///     </para>
///     <para>
///         Little-endian throughout, which is not an assumption: WebAssembly is little-endian by
///         specification, and <c>DataView</c> is told so explicitly at every read.
///     </para>
///     <para>
///         Nothing here is aligned, and <c>DataView</c> does not care. Padding to keep 64-bit fields
///         on 8-byte boundaries would grow every descriptor to buy a property no reader needs.
///     </para>
/// </remarks>
sealed class WebGpuPacker {
    byte[] buffer = new byte[512];
    int written;

    /// <summary>What has been written so far.</summary>
    public Span<byte> Written => buffer.AsSpan(0, written);

    /// <summary>Empties it, keeping the memory.</summary>
    /// <remarks>
    ///     One packer per binding, reused. Descriptors are built on the frame path — a render pass
    ///     descriptor once per pass — and a fresh array each time would be an allocation per pass per
    ///     frame.
    /// </remarks>
    public WebGpuPacker Reset() {
        written = 0;
        return this;
    }

    /// <summary>Writes a 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public WebGpuPacker Int(int value) {
        Reserve(4);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(written), value);
        written += 4;

        return this;
    }

    /// <summary>Writes a 32-bit integer, from an enum whose value is <c>webgpu.h</c>'s.</summary>
    /// <param name="value">The value.</param>
    public WebGpuPacker Enum(uint value) => Int(unchecked((int)value));

    /// <summary>Writes a boolean as a 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public WebGpuPacker Bool(bool value) => Int(value ? 1 : 0);

    /// <summary>Writes a WebGPU object as its table index.</summary>
    /// <param name="value">The object, or <see cref="WebGpuObject.Null" />.</param>
    /// <remarks>
    ///     A browser-side token is a slot in a JavaScript array, so it fits in an integer. That is
    ///     the whole reason <see cref="WebGpuObject" /> is opaque rather than a pointer.
    /// </remarks>
    public WebGpuPacker Object(WebGpuObject value) => Int((int)value.Value);

    /// <summary>Writes a 32-bit float.</summary>
    /// <param name="value">The value.</param>
    public WebGpuPacker Float(float value) {
        Reserve(4);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(written), value);
        written += 4;

        return this;
    }

    /// <summary>Writes a 64-bit float.</summary>
    /// <param name="value">The value.</param>
    public WebGpuPacker Double(double value) {
        Reserve(8);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(written), value);
        written += 8;

        return this;
    }

    /// <summary>Writes a size or an offset, as a 64-bit float.</summary>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     What every size and offset crosses as. JavaScript has no 64-bit integer that survives
    ///     arithmetic without <c>BigInt</c>, and a double holds every integer up to 2^53 exactly —
    ///     which is nine petabytes, and therefore every buffer size WebGPU will ever accept.
    /// </remarks>
    public WebGpuPacker Long(long value) {
        Reserve(8);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(written), value);
        written += 8;

        return this;
    }

    void Reserve(int bytes) {
        if (written + bytes <= buffer.Length) {
            return;
        }

        Array.Resize(ref buffer, Math.Max(buffer.Length * 2, written + bytes));
    }
}
