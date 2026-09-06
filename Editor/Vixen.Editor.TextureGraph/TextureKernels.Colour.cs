// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.TextureGraph;

/// <summary>Which channel of which input one output channel of <c>ChannelShuffle</c> takes.</summary>
/// <remarks>
///     ⚠ <b>The numbers are the kernel's, and nothing but this file and a test keeps the two
///     tables the same.</b> <c>ChannelShuffle.rvn</c> reads a bare <c>int</c> and falls through to
///     the first input's red for anything it does not recognise, because a shader has nowhere to
///     raise from — so a selector that drifted would produce a plausible picture rather than an
///     error. <c>TextureColourKernelTests</c> reads the selector names out of the source and
///     requires them to be these.
/// </remarks>
enum TextureChannelSource {
    /// <summary>The first input's red.</summary>
    FirstRed = 0,

    /// <summary>The first input's green.</summary>
    FirstGreen = 1,

    /// <summary>The first input's blue.</summary>
    FirstBlue = 2,

    /// <summary>The first input's alpha.</summary>
    FirstAlpha = 3,

    /// <summary>The second input's red.</summary>
    SecondRed = 4,

    /// <summary>The second input's green.</summary>
    SecondGreen = 5,

    /// <summary>The second input's blue.</summary>
    SecondBlue = 6,

    /// <summary>The second input's alpha.</summary>
    SecondAlpha = 7,

    /// <summary>A constant zero, so a channel can be cleared without a second image in the pool.</summary>
    Zero = 8,

    /// <summary>A constant one — an opacity forced opaque, without a second image in the pool.</summary>
    One = 9
}

/// <summary>Which way <c>Mirror</c> folds.</summary>
enum TextureMirrorAxis {
    /// <summary>About a vertical line.</summary>
    X = 0,

    /// <summary>About a horizontal one.</summary>
    Y = 1,

    /// <summary>Both — doc 48 § 4.3's "corner".</summary>
    Corner = 2
}

/// <summary>What <c>Mirror</c> does with the fold.</summary>
/// <remarks>
///     ⚠ <b>The two obey different laws and a test that asserted the wrong one would pass.</b>
///     <see cref="Flip" /> is an involution — twice is the identity. <see cref="Reflect" /> is
///     idempotent — twice is once — and it happens to look like an involution on a symmetric image,
///     which is exactly the image a lazy test reaches for.
/// </remarks>
enum TextureMirrorMode {
    /// <summary>One half copied, reversed, over the other.</summary>
    Reflect = 0,

    /// <summary>The whole image reversed about the line.</summary>
    Flip = 1
}

/// <summary>What a space kernel does outside the source.</summary>
enum TextureTiling {
    /// <summary>The edge texel holds.</summary>
    Clamp = 0,

    /// <summary>The image repeats.</summary>
    Wrap = 1,

    /// <summary>The image repeats, reversed every other period.</summary>
    Mirror = 2
}

/// <summary>How a space kernel reads between texels.</summary>
enum TextureFilter {
    /// <summary>The nearest texel. Exact where the mapping lands on texel centres.</summary>
    Point = 0,

    /// <summary>The four around the position, weighted.</summary>
    Bilinear = 1,

    /// <summary>
    ///     The average of everything one output texel covers — <c>Resample</c> only, and the only
    ///     correct choice going down.
    /// </summary>
    Box = 2
}

/// <summary>The colour, channel and space kernels of doc 48 § 4.2 and § 4.3, by name.</summary>
/// <remarks>
///     <para>
///         <b>Names rather than a registry, because <see cref="TextureKernels" /> already is one.</b>
///         A kernel is embedded by the <c>Shaders\*.rvn</c> glob and found by its file name; what is
///         missing without this file is somewhere for a plan to say <c>Kernel = "ChannelShuffle"</c>
///         without a string literal, and somewhere for the integer contracts above to live beside the
///         sources that read them.
///     </para>
///     <para>
///         ⚠ <b><c>AutoLevels</c> is two kernels and cannot be evaluated in tiles.</b> It is the
///         first op in the catalogue whose output depends on every texel of its input:
///         <see cref="MinMaxReduce" /> is dispatched once per level down to a 1×1 image, and
///         <see cref="AutoLevels" /> reads that. Nothing on <c>TextureOp</c> records that property,
///         so a future tiled evaluator would run it per tile and produce a plausible picture with a
///         different stretch in every tile — see this assembly's README.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureColourKernels {
    /// <summary>A spline per channel, through a table baked by <see cref="TextureRamp" />.</summary>
    public const string Curve = "Curve";

    /// <summary>Grey through a colour ramp.</summary>
    public const string GradientMap = "GradientMap";

    /// <summary>Hue rotation, saturation and lightness.</summary>
    public const string Hsl = "Hsl";

    /// <summary>Colour to grey under three weights, which the kernel normalises.</summary>
    public const string Grayscale = "Grayscale";

    /// <summary>Per-channel inversion.</summary>
    public const string Invert = "Invert";

    /// <summary>Each output channel from a channel of one of two inputs.</summary>
    public const string ChannelShuffle = "ChannelShuffle";

    /// <summary>One level of the min/max reduction Auto Levels needs before it can map anything.</summary>
    public const string MinMaxReduce = "MinMaxReduce";

    /// <summary>The image stretched onto the full range by the extremes the reduction found.</summary>
    public const string AutoLevels = "AutoLevels";

    /// <summary>Rotate, scale, offset and shear, with a mip-correct minification.</summary>
    public const string Transform2D = "Transform2D";

    /// <summary>An axis, a line, and a reflect or a flip about it.</summary>
    public const string Mirror = "Mirror";

    /// <summary>An integer repeat with a shift per tile row and column.</summary>
    public const string Tile = "Tile";

    /// <summary>A rectangle of the source onto the whole of the target.</summary>
    public const string Crop = "Crop";

    /// <summary>The same picture at the resolution the plan asked for.</summary>
    public const string Resample = "Resample";

    /// <summary>Every one of them, which is what a test enumerates to be sure none was forgotten.</summary>
    public static IReadOnlyList<string> All { get; } = [
        AutoLevels,
        ChannelShuffle,
        Crop,
        Curve,
        GradientMap,
        Grayscale,
        Hsl,
        Invert,
        MinMaxReduce,
        Mirror,
        Resample,
        Tile,
        Transform2D
    ];
}
