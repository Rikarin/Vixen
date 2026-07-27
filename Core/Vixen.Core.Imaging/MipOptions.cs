// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging;

/// <summary>What a texture's contents mean, for the purpose of averaging them.</summary>
/// <remarks>
///     <para>
///         A box filter is the same arithmetic whatever a texture holds, and it is the <i>right</i>
///         arithmetic for almost none of them. Colour has to be averaged in linear light, a cut-out's
///         invisible texels must not vote on the colour of its visible ones, and a normal is a
///         direction rather than three numbers. None of that can be worked out from the pixel format:
///         a normal map and an albedo map are both <c>Rgba8UNorm</c>, and a mask packed into an sRGB
///         format is neither.
///     </para>
///     <para>
///         So it is a setting the caller supplies, which in practice is the importer reading it from
///         a <c>.meta</c> file. That is the same division <see cref="MipChain" /> already draws for
///         the transfer function, and this is where it is written down.
///     </para>
/// </remarks>
public readonly record struct MipOptions {
    /// <summary>
    ///     Average in linear light rather than on the stored values. Right for colour; wrong for a
    ///     normal map, a roughness map or a mask that happens to live in an sRGB format.
    /// </summary>
    /// <remarks>
    ///     Half black and half white is 188 in linear light and 127 if the encoded bytes are
    ///     averaged. The second is the classic mip-generation bug and it makes every texture darken
    ///     as it recedes.
    /// </remarks>
    public bool Srgb { get; init; }

    /// <summary>
    ///     Weight each texel's colour by its alpha. Right for a cut-out or a texture with unused
    ///     regions; the colour under a fully transparent texel is usually whatever the painter left
    ///     there, and it should not get a vote.
    /// </summary>
    public bool AlphaWeighted { get; init; }

    /// <summary>
    ///     Treat the first two or three channels as a unit vector: reconstruct, average, normalise.
    ///     The average of four unit vectors is not a unit vector, and a normal map whose mips are
    ///     shorter than one lights as though the surface were flatter than it is.
    /// </summary>
    public bool RenormaliseNormals { get; init; }

    /// <summary>Plain box filter on the stored values. What a linear, opaque, non-directional texture wants.</summary>
    public static MipOptions Linear => default;

    /// <summary>An sRGB colour texture.</summary>
    public static MipOptions Colour => new() { Srgb = true };

    /// <summary>An sRGB colour texture with cut-out or unused alpha.</summary>
    public static MipOptions CutoutColour => new() { Srgb = true, AlphaWeighted = true };

    /// <summary>A tangent-space normal map.</summary>
    public static MipOptions NormalMap => new() { RenormaliseNormals = true };
}
