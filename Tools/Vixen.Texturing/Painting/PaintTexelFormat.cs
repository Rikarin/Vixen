// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Mathematics;
using Vixen.TextureGraph;

namespace Vixen.Texturing.Painting;

/// <summary>How wide one texel of a paint channel is: what it can tell apart, and what it costs.</summary>
/// <remarks>
///     <para>
///         <b>A channel's and not a canvas's</b> (Patina ADR 0020): a base colour reads well at eight bits and a height
///         painted in eight bits terraces into every normal derived from it, so a layer that paints both holds each at
///         its own width.
///     </para>
///     <para>
///         ⚠ <b>Floats and not sixteen-bit integers</b>, because they are what a texture graph already holds between
///         its kernels (<c>TextureFormat.Rgba16Float</c> and <c>Rgba32Float</c>), so a channel uploads as it is held,
///         with no conversion and no format a device may not filter.
///     </para>
/// </remarks>
public enum PaintTexelFormat : byte {
    /// <summary>Four eight-bit lanes, 0 to 1. What every channel was before there was a choice, and still the default.</summary>
    Rgba8 = 0,

    /// <summary>Four half-float lanes: eleven bits of precision across 0 to 1, for a height or a mask.</summary>
    Rgba16Float = 1,

    /// <summary>Four 32-bit float lanes, for values past 0 to 1 or that must survive derivation exactly.</summary>
    Rgba32Float = 2
}

/// <summary>What each <see cref="PaintTexelFormat" /> is in bytes.</summary>
public static class PaintTexelFormats {
    /// <summary>How many bytes one texel of a format occupies.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not one of the three.</exception>
    public static int BytesPerTexel(PaintTexelFormat format) =>
        format switch {
            PaintTexelFormat.Rgba8 => 4,
            PaintTexelFormat.Rgba16Float => 8,
            PaintTexelFormat.Rgba32Float => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a paint texel format.")
        };

    /// <summary>What a texture graph calls a format: how a channel of it is uploaded.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The texture format, of the same bytes.</returns>
    public static TextureFormat Texture(PaintTexelFormat format) =>
        format switch {
            PaintTexelFormat.Rgba16Float => TextureFormat.Rgba16Float,
            PaintTexelFormat.Rgba32Float => TextureFormat.Rgba32Float,
            _ => TextureFormat.Rgba8
        };

    /// <summary>Whether a format holds every value another does, so a channel can move to it and lose nothing.</summary>
    /// <param name="format">The format moved to.</param>
    /// <param name="than">The format moved from.</param>
    /// <returns>Whether it is at least as wide.</returns>
    /// <remarks>
    ///     Every eight-bit level is a half float exactly (a half has eleven bits of mantissa), and every half a single,
    ///     so the order of the enumeration is the order of width.
    /// </remarks>
    public static bool Holds(PaintTexelFormat format, PaintTexelFormat than) => format >= than;

    /// <summary>Writes one texel.</summary>
    /// <param name="format">Its format.</param>
    /// <param name="value">Its lanes.</param>
    /// <param name="into">Exactly <see cref="BytesPerTexel" /> bytes.</param>
    public static void Encode(PaintTexelFormat format, Vector4 value, Span<byte> into) {
        switch (format) {
            case PaintTexelFormat.Rgba8:
                BinaryPrimitives.WriteUInt32LittleEndian(into, PaintImage.Pack(value.X, value.Y, value.Z, value.W));
                break;
            case PaintTexelFormat.Rgba16Float:
                BinaryPrimitives.WriteHalfLittleEndian(into, (Half)value.X);
                BinaryPrimitives.WriteHalfLittleEndian(into[2..], (Half)value.Y);
                BinaryPrimitives.WriteHalfLittleEndian(into[4..], (Half)value.Z);
                BinaryPrimitives.WriteHalfLittleEndian(into[6..], (Half)value.W);
                break;
            default:
                BinaryPrimitives.WriteSingleLittleEndian(into, value.X);
                BinaryPrimitives.WriteSingleLittleEndian(into[4..], value.Y);
                BinaryPrimitives.WriteSingleLittleEndian(into[8..], value.Z);
                BinaryPrimitives.WriteSingleLittleEndian(into[12..], value.W);
                break;
        }
    }

    /// <summary>Reads one texel.</summary>
    /// <param name="format">Its format.</param>
    /// <param name="from">At least <see cref="BytesPerTexel" /> bytes.</param>
    /// <returns>Its lanes.</returns>
    public static Vector4 Decode(PaintTexelFormat format, ReadOnlySpan<byte> from) =>
        format switch {
            PaintTexelFormat.Rgba8 => PaintImage.Unpack(BinaryPrimitives.ReadUInt32LittleEndian(from)),
            PaintTexelFormat.Rgba16Float => new(
                (float)BinaryPrimitives.ReadHalfLittleEndian(from),
                (float)BinaryPrimitives.ReadHalfLittleEndian(from[2..]),
                (float)BinaryPrimitives.ReadHalfLittleEndian(from[4..]),
                (float)BinaryPrimitives.ReadHalfLittleEndian(from[6..])
            ),
            _ => new(
                BinaryPrimitives.ReadSingleLittleEndian(from),
                BinaryPrimitives.ReadSingleLittleEndian(from[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(from[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(from[12..])
            )
        };
}
