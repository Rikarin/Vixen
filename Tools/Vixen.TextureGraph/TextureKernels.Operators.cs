// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph;

/// <summary>The colour and blend operators Patina's audit found missing, by kernel name — Patina #171.</summary>
/// <remarks>
///     <para>
///         <b>Pointwise, every one</b>, except the <see cref="MeanReduce" /> ladder
///         <c>Colour/Colour Match</c> climbs before it can shift anything. None takes a length, so a
///         bake at any resolution draws the same picture, and none reads past the texel it writes, so
///         none can break tiling.
///     </para>
///     <para>
///         ⚠ <b>Two kernels each back two nodes, and the pairs are inverses or near it.</b>
///         <see cref="SrgbConvert" /> decodes or encodes by a switch, <see cref="Premultiply" />
///         multiplies or divides, and <see cref="ReplaceColour" /> shifts from a named colour or from
///         the image's measured mean. Four kernels for the four nodes would be the same arithmetic
///         twice over.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureColourOperators {
    /// <summary>Each colour channel held inside a range.</summary>
    public const string Clamp = "Clamp";

    /// <summary>Each colour channel to nought or one about a level, with an optional soft edge.</summary>
    public const string Threshold = "Threshold";

    /// <summary>Each colour channel to nought or one by a comparison with a value.</summary>
    public const string Compare = "Compare";

    /// <summary>Each colour channel onto a few evenly spaced values.</summary>
    public const string Quantise = "Quantise";

    /// <summary>Each output colour channel as a weighted sum of the input ones, plus an offset.</summary>
    public const string ChannelMix = "ChannelMix";

    /// <summary>A grey read through one row of a ramp that is itself an image.</summary>
    public const string ImageRamp = "ImageRamp";

    /// <summary>The whole image moved in HSL by however far one colour is from another.</summary>
    public const string ReplaceColour = "ReplaceColour";

    /// <summary>Colours near one colour moved towards another, and nothing else.</summary>
    public const string ReplaceColourRange = "ReplaceColourRange";

    /// <summary>One rung of the mean <c>Colour/Colour Match</c> measures, carrying a coverage in alpha.</summary>
    public const string MeanReduce = "MeanReduce";

    /// <summary>The foreground or the background, outright, per texel, in the proportion of an opacity.</summary>
    public const string Dissolve = "Dissolve";

    /// <summary>The sRGB transfer function, either way.</summary>
    public const string SrgbConvert = "SrgbConvert";

    /// <summary>Colour multiplied by alpha, or divided by it.</summary>
    public const string Premultiply = "Premultiply";

    /// <summary>A colour graded through a lookup cube stored as a strip of slices.</summary>
    public const string ColourLookup = "ColourLookup";

    /// <summary>Every texel snapped to the nearest of up to eight colours.</summary>
    public const string Palette = "Palette";

    /// <summary>One of four images, chosen by a number.</summary>
    public const string SelectInput = "SelectInput";

    /// <summary>Every one of them.</summary>
    public static IReadOnlyList<string> All { get; } = [
        ChannelMix,
        Clamp,
        ColourLookup,
        Compare,
        Dissolve,
        ImageRamp,
        MeanReduce,
        Palette,
        Premultiply,
        Quantise,
        ReplaceColour,
        ReplaceColourRange,
        SelectInput,
        SrgbConvert,
        Threshold
    ];
}
