// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.TextureGraph;

/// <summary>A rectangle of texels, half-open on the high side.</summary>
/// <param name="X">The low column.</param>
/// <param name="Y">The low row.</param>
/// <param name="Width">How many columns.</param>
/// <param name="Height">How many rows.</param>
public readonly record struct TextureRect(int X, int Y, int Width, int Height) {
    /// <summary>One past the last column.</summary>
    public int EndX => X + Width;

    /// <summary>One past the last row.</summary>
    public int EndY => Y + Height;
}

/// <summary>What changed since an evaluation, for the next one to repair rather than repeat.</summary>
/// <param name="Previous">The keys of that evaluation, <see cref="TextureBake.Keys" />.</param>
/// <param name="Changed">
///     Which external images changed, and over which rectangles. Everything else has to be as it was.
/// </param>
public sealed record TextureRegions(
    ImmutableArray<TextureResultKey> Previous,
    IReadOnlyDictionary<int, ImmutableArray<TextureRect>> Changed
);

/// <summary>Part of one image to rewrite.</summary>
/// <param name="Rect">Where.</param>
/// <param name="Texels">The rectangle's texels in the image's format, row by row.</param>
public readonly record struct TextureTileWrite(TextureRect Rect, ReadOnlyMemory<byte> Texels);

/// <summary>What a caller is changing in one uploaded image.</summary>
/// <param name="Image">The plan's image.</param>
/// <param name="Tiles">The rectangles.</param>
/// <param name="Content">The digest of the whole image afterwards.</param>
public sealed record TextureImageChange(int Image, IReadOnlyList<TextureTileWrite> Tiles, TextureResultKey Content);

/// <summary>Which ops can run over part of an image, and how far from a texel each one reads.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A list of kernels that were read, and never a guess.</b> Everything here computes one
///         output texel from the same texel of each input and the op's uniforms. A kernel that is
///         missing costs a whole dispatch; one that is listed wrongly leaves stale texels around every
///         stroke, which is why the list is short and why a repaired picture is compared to a clean
///         evaluation byte for byte.
///     </para>
///     <para>
///         ⚠ <b><c>Bitmap</c> has a reach of one</b>: filtered, it reads the next texel with a weight
///         that is zero only where the division happens to be exact. At equal extents that weight
///         cannot show in eight bits and can in half-floats, so a stroke's rectangle grows by it.
///     </para>
/// </remarks>
public static class TextureLocality {
    /// <summary>How many texels away an op reads, or <see langword="null" /> when no bound is known.</summary>
    /// <param name="op">The op.</param>
    /// <returns>Its reach, or nothing when it has to run whole.</returns>
    public static int? Of(TextureOp op) {
        ArgumentNullException.ThrowIfNull(op);

        if (op.Cpu is not null || op.DependsOnEveryTexel) {
            return null;
        }

        return op.Kernel switch {
            "Uniform" or "Blend" or "Mix" or "Levels" or "Hsl" or "Invert" or "Grayscale" or "Curve"
                or "ChannelShuffle" or "GradientMap" => 0,
            "Bitmap" => 1,
            _ => null
        };
    }
}
