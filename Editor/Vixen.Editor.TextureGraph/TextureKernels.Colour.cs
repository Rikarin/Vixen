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

/// <summary>Which metal's normal-incidence reflectance a <c>MetalReflectance</c> op writes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Names and indices, and deliberately not the numbers.</b> The F0 triples are in
///         <c>Shaders/MetalReflectance.rvn</c> and nowhere else, with the published source they come
///         from named in its header. A second copy here would be a table nothing compares against: a
///         plan hands the kernel an index and reads back an image, so a drifted CPU copy would never
///         be dispatched and never be wrong out loud —
///         <a href="https://github.com/Rikarin/Vixen/issues/1095">#1095</a>'s defect in a third
///         costume. <c>TextureColourKernelTests</c> reads the branches out of the kernel and requires
///         one per member here, in this order.
///     </para>
///     <para>
///         ⚠ <b>The numbers are the kernel's contract</b>, the way <c>TextureShapeKind</c>'s are:
///         nothing in the compilation would notice a renumbering, because every entry is a plausible
///         metal colour and a graph asking for gold would simply be brass.
///     </para>
///     <para>
///         <b><see cref="Iron" /> is zero because something has to be.</b> Doc 48's rule is that zero
///         usually means "off" and a lookup has no "off", so the zero is the entry an author is
///         likeliest to have meant — and iron is what every "worn metal" graph in § 4.9 starts from.
///     </para>
/// </remarks>
enum TextureMetal {
    /// <summary>Very slightly blue, and the least chromatic of the greys. The default.</summary>
    Iron = 0,

    /// <summary>Neutral, a shade darker than iron.</summary>
    Chromium = 1,

    /// <summary>Warm and dark — what a "dirty steel" graph usually wants.</summary>
    Nickel = 2,

    /// <summary>The darkest here, and warm.</summary>
    Titanium = 3,

    /// <summary>Bright and very slightly warm.</summary>
    Platinum = 4,

    /// <summary>Bright and neutral to a thousandth. ⚠ Not silver, which is warmer.</summary>
    Aluminium = 5,

    /// <summary>The brightest, and warmer than aluminium.</summary>
    Silver = 6,

    /// <summary>Saturated: red saturates and blue is a third of it.</summary>
    Gold = 7,

    /// <summary>Saturated, and redder than gold.</summary>
    Copper = 8,

    /// <summary>Between gold and aluminium, which is what an alloy of copper and zinc looks like.</summary>
    Brass = 9
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
///         ⚠ <b>And one from § 4.9 — <see cref="MetalReflectance" />.</b> It is a Surface <em>row</em>
///         rather than a § 4.2 kernel; it is declared here because the node it backs is a colour
///         constant and the name the row wants belongs to the compound over it, which its own remark
///         explains.
///     </para>
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
///         <see cref="AutoLevels" /> reads that. ⚠ Every op <c>TextureAdjust.AutoLevels</c> emits
///         says so on itself — <c>TextureOp.DependsOnEveryTexel</c>, and <c>TexturePlan</c>'s
///         <c>TilingRefusals</c> is what a tiled evaluator asks
///         (<a href="https://github.com/Rikarin/Vixen/issues/636">#636</a>). Without it one would run
///         the reduction per tile and produce a plausible picture with a different stretch in each.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureColourKernels {
    /// <summary>An input range remapped through a gamma into an output range.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § 4.2's first entry, shipped by § M1 and declared by nobody until
    ///     <a href="https://github.com/Rikarin/Vixen/issues/756">#756</a>.</b> Every other kernel in
    ///     this assembly is a <c>const string</c> on a surface and a member of that surface's
    ///     <c>All</c>; <c>Levels</c> was a bare literal in <c>LevelsNode</c> and nowhere else, so a
    ///     mistyped name was a missing-resource exception at bake time rather than a build error, and
    ///     it sat outside every per-kernel theory the others are given by being in a list.
    ///     ⚠ <b>#756 asked for it on <c>TextureFilters</c> and that is the wrong family</b>: § 4.4 is
    ///     blurs and warps, and § 4.2's own table opens with this line — which is also where
    ///     <see cref="AutoLevels" />, the kernel that automates it, already lives.
    /// </remarks>
    public const string Levels = "Levels";

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

    /// <summary>
    ///     A named metal's F0, as a constant image — doc 48 § 4.9's Surface row, and the one kernel
    ///     here that reads no input.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The node is <c>Colour/Metal Reflectance</c> and the § 4.9 compound over it is
    ///     <c>Surface/Metal Reflectance</c>, and the two menus are not a preference.</b> A published
    ///     compound and a node class share one path namespace, so an atom and the compound that wraps
    ///     it cannot both be called the row's name — <c>TextureCompoundLibrary</c> refuses the second
    ///     with "Two different node types claim the path". Same shape as
    ///     <c>Placement/Tile Sampler</c> under <c>Patterns/Tile Random</c>.
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1096">#1096</a>.
    /// </remarks>
    public const string MetalReflectance = "MetalReflectance";

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
        Levels,
        MetalReflectance,
        MinMaxReduce,
        Mirror,
        Resample,
        Tile,
        Transform2D
    ];
}
