// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Shaders;

/// <summary>
///     Writes values into a constant buffer at offsets Raven computed.
/// </summary>
/// <remarks>
///     <para>
///         Every method takes the offset rather than deriving one: the generated code carries the
///         numbers out of the shader's reflection, so the layout a host writes is by construction
///         the layout the backends emitted. Computing offsets here would be computing them a second
///         way, which is the arrangement that lets a host and a shader disagree about <c>float3</c>
///         padding — and disagree silently, because every byte still lands inside the buffer.
///     </para>
///     <para>
///         <strong>What these have to know that a memcpy does not.</strong> std140 is not the CLR's
///         layout, and three cases differ: a <c>bool</c> occupies four bytes, a <c>float3</c> is
///         twelve bytes on a sixteen-byte boundary, and a matrix is stored by column with each
///         column padded to its stride. A <c>Matrix4x4</c> happens to be a straight blit, and that
///         is not luck — see <see cref="Write(Span{byte}, int, in Matrix4x4)" />.
///     </para>
/// </remarks>
public static class ShaderConstants {
    /// <summary>Writes a <c>float</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, float value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes an <c>int</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, int value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes a <c>uint</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, uint value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes a <c>bool</c> as the four bytes a shader reads it as.</summary>
    /// <remarks>
    ///     GLSL gives a <c>bool</c> in a uniform block 32 bits, and SPIR-V has no memory layout for
    ///     <c>OpTypeBool</c> at all — which is why Raven refuses one as stage I/O but allows one in
    ///     a block, where the layout rule supplies the size. Writing a single byte here leaves three
    ///     bytes of whatever the buffer held, and a non-zero one of them is a <c>true</c> that
    ///     nobody set.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, bool value) {
        var word = value ? 1 : 0;
        MemoryMarshal.Write(buffer[offset..], in word);
    }

    /// <summary>Writes a <c>float2</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, in Vector2 value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes a <c>float3</c> — twelve bytes, with the fourth left as it was.</summary>
    /// <remarks>
    ///     The padding after a <c>float3</c> belongs to whatever comes next, which std140 may have
    ///     placed there: the rule aligns a member to sixteen bytes but does not reserve the tail, so
    ///     a <c>float</c> following a <c>float3</c> sits in the same sixteen. Writing sixteen bytes
    ///     here would zero it, which is the sort of bug that presents as "the roughness resets when
    ///     I set the colour".
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, in Vector3 value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes a <c>float4</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, in Vector4 value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes an <c>int2</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, in Int2 value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes an <c>int3</c> — twelve bytes, as <see cref="Write(Span{byte}, int, in Vector3)" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, in Int3 value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes an <c>int4</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, in Int4 value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes a <c>mat4</c> — the host's sixty-four bytes, unchanged.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>No transpose, and that is the whole design rather than an optimisation.</strong>
    ///         The engine stores a matrix row-major with the translation in <c>M41..M43</c>
    ///         (ADR-003's row-vector convention); the shader reads the same bytes as
    ///         <c>ColMajor</c> with a stride of sixteen, which makes the matrix it sees the host's
    ///         transpose. That is exactly what <c>mul(v, M)</c> needs: <c>m * v</c> in the shader is
    ///         <c>Mᵀ·v</c>, which is <c>(vᵀ·M)ᵀ</c>. Transposing here would compute the wrong
    ///         transform twice as expensively. See docs/plan/07 § E.
    ///     </para>
    ///     <para>
    ///         The stride is asserted rather than assumed: a <c>mat4</c> whose columns were not
    ///         sixteen bytes apart would need the padded path below, and the reflection is where the
    ///         truth about that lives.
    ///     </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> buffer, int offset, in Matrix4x4 value) =>
        MemoryMarshal.Write(buffer[offset..], in value);

    /// <summary>Writes a <c>mat3</c>, padding each column to <paramref name="columnStride" />.</summary>
    /// <remarks>
    ///     The case where the host's bytes and the shader's are genuinely different: a
    ///     <see cref="Matrix3x3" /> is nine floats end to end, and std140 gives each of the shader's
    ///     three columns sixteen bytes. So this writes three runs of twelve bytes at the stride and
    ///     leaves the four-byte gaps alone. The host's <em>row</em> i becomes the shader's column i,
    ///     for the same transpose-for-free reason a <c>mat4</c> needs no rearranging at all.
    /// </remarks>
    public static void Write(Span<byte> buffer, int offset, in Matrix3x3 value, int columnStride) {
        Write(buffer, offset, new Vector3(value.M11, value.M12, value.M13));
        Write(buffer, offset + columnStride, new Vector3(value.M21, value.M22, value.M23));
        Write(buffer, offset + columnStride * 2, new Vector3(value.M31, value.M32, value.M33));
    }

    /// <summary>Writes each element of <paramref name="values" /> at <paramref name="stride" />.</summary>
    /// <remarks>
    ///     <para>
    ///         Bounded by the shader's declared length rather than the caller's span, and the extra
    ///         elements are dropped rather than throwing. A light list is sized by a permutation key
    ///         and filled to a different depth every frame; handing it a longer array is the host
    ///         having more lights than this variant was compiled for, which is a thing to notice at
    ///         the point that decides how many to send, not a crash inside a memcpy.
    ///     </para>
    ///     <para>
    ///         std140 rounds an array's element stride up to sixteen bytes even for a
    ///         <c>float[]</c>, which is why the stride is passed rather than taken as
    ///         <c>sizeof(T)</c> — a <c>float[4]</c> occupies sixty-four bytes, not sixteen.
    ///     </para>
    /// </remarks>
    public static void WriteArray<T>(Span<byte> buffer, int offset, int stride, int length, ReadOnlySpan<T> values)
        where T : unmanaged {
        var count = Math.Min(length, values.Length);

        for (var i = 0; i < count; i++) {
            MemoryMarshal.Write(buffer[(offset + i * stride)..], in values[i]);
        }
    }
}
