// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>How a <c>SlopeBlur</c> op accumulates the source along the walk.</summary>
/// <remarks>
///     ⚠ <b>The numbers are the contract with <c>Shaders/SlopeBlur.rvn</c>'s <c>mode</c>, which
///     compares against them literally.</b> Nothing in the compilation would notice a renumbering —
///     a min where a max was meant is a perfectly plausible picture. <c>TextureFilterDeviceTests</c>
///     pins each one by a property only it has.
/// </remarks>
enum TextureSlopeMode {
    /// <summary>The mean of every sample along the walk, including the texel it started from.</summary>
    Blend = 0,

    /// <summary>The darkest sample along the walk — an erosion along the grain.</summary>
    Min = 1,

    /// <summary>The brightest sample along the walk — a dilation along the grain.</summary>
    Max = 2
}

/// <summary>The eleven filter kernels of doc 48 § 4.4, as ops a plan can hold.</summary>
/// <remarks>
///     <para>
///         <b>Why builders rather than a <see cref="TextureOp" /> written out at each call site.</b>
///         <c>TexturePlanEvaluator.Uniforms</c> refuses an op that does not carry every parameter its
///         kernel declares — deliberately, because zero is a valid-looking number for almost all of
///         them. Half the kernels here declare four or more, so writing one out by hand is four
///         chances at an exception and one chance at a plausible picture. Every builder emits the
///         complete set.
///     </para>
///     <para>
///         ⚠ <b>Every parameter is a scalar, and that is a property of the evaluator rather than a
///         style.</b> <c>Uniforms</c> writes one <see cref="float" /> per uniform-block member, so a
///         <c>float2</c> would receive its x and a zero — a centre of <c>(0.5, 0)</c> where
///         <c>(0.5, 0.5)</c> was meant. So a centre is two parameters, in every kernel under
///         <c>Shaders/</c>.
///     </para>
///     <para>
///         <b>Which of these are lengths, and which only look like it.</b> A blur's sigma, a smear's
///         length, a box's radius and a warp's intensity are all
///         <see cref="TextureParameterUnit.TexelsAtBase" /> and are scaled by
///         <c>TexturePlan.Resolve</c>. A radial blur's <c>amount</c> is a fraction of the distance to
///         its centre, an emboss's <c>intensity</c> multiplies a slope taken per unit of image width,
///         and a sharpen's <c>amount</c> is a ratio — none of those is a length, and scaling one
///         would make the same graph a different material at 4K in the opposite direction from the
///         bug § D8 is about.
///     </para>
///     <para>
///         <b>Internal, because nothing outside this assembly has a caller yet.</b> The node classes
///         of § M4 are what will want these public, and they are the ones who should widen them.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureFilters {
    /// <summary>A box blur along one axis. Doc 48 § 4.4's <c>Blur</c>, which § M1 already shipped.</summary>
    public const string Blur = "Blur";

    /// <summary>A gaussian along one axis.</summary>
    public const string BlurHq = "BlurHq";

    /// <summary>A box smear along a continuous direction.</summary>
    public const string DirectionalBlur = "DirectionalBlur";

    /// <summary>A smear along the ray from a centre.</summary>
    public const string RadialBlur = "RadialBlur";

    /// <summary>A box whose radius is read from a map, per texel.</summary>
    public const string NonUniformBlur = "NonUniformBlur";

    /// <summary>An unsharp mask.</summary>
    public const string Sharpen = "Sharpen";

    /// <summary>A height lit from one direction, as a grey about a mid tone.</summary>
    public const string Emboss = "Emboss";

    /// <summary>A displacement by the gradient of a grey field.</summary>
    public const string Warp = "Warp";

    /// <summary>A displacement along one direction by the value of a grey field.</summary>
    public const string DirectionalWarp = "DirectionalWarp";

    /// <summary>A displacement by a signed two-channel map.</summary>
    public const string VectorWarp = "VectorWarp";

    /// <summary>An iterative walk down a slope field, accumulating the source along it.</summary>
    public const string SlopeBlur = "SlopeBlur";

    /// <summary>Every kernel this slice registers, plus the one § M1 shipped that belongs to § 4.4.</summary>
    /// <remarks>
    ///     ⚠ <b>The roll call finds this by reflection</b> — <c>TextureColourKernelTests.Declared</c>
    ///     walks every type in the assembly with a static <c>All</c>, so a slice appears in the union
    ///     by existing rather than by editing a shared file. <c>Blur</c> is listed because § 4.4 owns
    ///     it even though § M1 wrote it; the roll call takes a union and a duplicate costs nothing.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } = [
        Blur,
        BlurHq,
        DirectionalBlur,
        Emboss,
        NonUniformBlur,
        RadialBlur,
        Sharpen,
        SlopeBlur,
        VectorWarp,
        Warp,
        DirectionalWarp
    ];

    /// <summary>A gaussian blur along one axis.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to read.</param>
    /// <param name="sigma">The standard deviation, in texels at the plan's base resolution.</param>
    /// <param name="vertical">Whether the taps run down rather than across.</param>
    /// <returns>The op. Two of these with <paramref name="vertical" /> swapped is a round blur.</returns>
    public static TextureOp BlurHqOp(int output, int source, float sigma, bool vertical = false) =>
        new() {
            Kernel = BlurHq,
            Output = output,
            Inputs = [source],
            Parameters = [
                new("sigma", sigma, TextureParameterUnit.TexelsAtBase),
                new("stepX", vertical ? 0f : 1f),
                new("stepY", vertical ? 1f : 0f)
            ]
        };

    /// <summary>A box smear along a continuous direction.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to read.</param>
    /// <param name="angle">Radians from +x towards +y, and +y is down the image.</param>
    /// <param name="length">How far the smear reaches each way, in texels at the base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp DirectionalBlurOp(int output, int source, float angle, float length) =>
        new() {
            Kernel = DirectionalBlur,
            Output = output,
            Inputs = [source],
            Parameters = [new("angle", angle), new("length", length, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>A smear along the ray from a centre.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to read.</param>
    /// <param name="amount">How much of the distance to the centre the samples span. 0 is a copy.</param>
    /// <param name="centreX">Where the rays meet, in 0..1 of the image.</param>
    /// <param name="centreY">Where the rays meet, in 0..1 of the image.</param>
    /// <param name="samples">How many samples the span is cut into. 1 is a copy at any amount.</param>
    /// <returns>The op.</returns>
    public static TextureOp RadialBlurOp(
        int output,
        int source,
        float amount,
        float centreX = 0.5f,
        float centreY = 0.5f,
        int samples = 16
    ) =>
        new() {
            Kernel = RadialBlur,
            Output = output,
            Inputs = [source],
            Parameters = [
                new("centreX", centreX),
                new("centreY", centreY),
                new("amount", amount),
                new("samples", samples)
            ]
        };

    /// <summary>A box whose radius is read from a map, per texel.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to blur.</param>
    /// <param name="radiusMap">The map whose red channel scales the radius.</param>
    /// <param name="maxRadius">What a fully lit texel is worth, in texels at the base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp NonUniformBlurOp(int output, int source, int radiusMap, float maxRadius) =>
        new() {
            Kernel = NonUniformBlur,
            Output = output,
            Inputs = [source, radiusMap],
            Parameters = [new("maxRadius", maxRadius, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>An unsharp mask.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to read.</param>
    /// <param name="amount">How much of the difference is added back. 0 is a copy.</param>
    /// <param name="radius">The half-width of the subtracted box, in texels at the base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp SharpenOp(int output, int source, float amount, float radius = 1f) =>
        new() {
            Kernel = Sharpen,
            Output = output,
            Inputs = [source],
            Parameters = [new("amount", amount), new("radius", radius, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>A height lit from one direction.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The height, as its red channel.</param>
    /// <param name="angle">Radians from +x towards +y, and +y is down the image.</param>
    /// <param name="elevation">Radians above the surface. A quarter turn flattens the relief.</param>
    /// <param name="intensity">How steep the relief reads. 0 is a flat mid grey.</param>
    /// <returns>The op.</returns>
    public static TextureOp EmbossOp(int output, int source, float angle, float elevation, float intensity) =>
        new() {
            Kernel = Emboss,
            Output = output,
            Inputs = [source],
            Parameters = [new("angle", angle), new("elevation", elevation), new("intensity", intensity)]
        };

    /// <summary>A displacement by the gradient of a grey field.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to displace.</param>
    /// <param name="warp">The grey field, as its red channel.</param>
    /// <param name="intensity">
    ///     How far a unit of slope displaces, in texels at the base resolution. ⚠ The slope is per
    ///     unit of image width, so a ramp spanning the image has a slope of one.
    /// </param>
    /// <returns>The op.</returns>
    public static TextureOp WarpOp(int output, int source, int warp, float intensity) =>
        new() {
            Kernel = Warp,
            Output = output,
            Inputs = [source, warp],
            Parameters = [new("intensity", intensity, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>A displacement along one direction by the value of a grey field.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to displace.</param>
    /// <param name="warp">The grey field, as its red channel. Raw 0..1, never centred.</param>
    /// <param name="angle">Radians from +x towards +y, and +y is down the image.</param>
    /// <param name="intensity">What a fully lit texel displaces by, in texels at the base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp DirectionalWarpOp(int output, int source, int warp, float angle, float intensity) =>
        new() {
            Kernel = DirectionalWarp,
            Output = output,
            Inputs = [source, warp],
            Parameters = [new("angle", angle), new("intensity", intensity, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>A displacement by a signed two-channel map.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image to displace.</param>
    /// <param name="vectors">
    ///     The displacement map. ⚠ Red is x and green is y, both <em>biased</em>: 0.5 is rest, 1 is
    ///     <c>+intensity</c> and 0 is <c>−intensity</c>. The one-sided reading of the same bytes is
    ///     half the amplitude and never negative, and it looks entirely plausible.
    /// </param>
    /// <param name="intensity">What a fully deflected channel displaces by, in texels at the base resolution.</param>
    /// <returns>The op.</returns>
    public static TextureOp VectorWarpOp(int output, int source, int vectors, float intensity) =>
        new() {
            Kernel = VectorWarp,
            Output = output,
            Inputs = [source, vectors],
            Parameters = [new("intensity", intensity, TextureParameterUnit.TexelsAtBase)]
        };

    /// <summary>An iterative walk down a slope field, accumulating the source along it.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The image the walk samples.</param>
    /// <param name="slope">The slope field, as its red channel.</param>
    /// <param name="intensity">
    ///     How far a unit of slope walks, in texels at the base resolution, over all the steps
    ///     together — <see cref="WarpOp" />'s quantity. ⚠ <b>Not the distance walked</b>: the path is
    ///     <c>|∇h| · intensity</c>, so a slope field spanning 0.1 rather than 1.0 walks a tenth as
    ///     far. <see href="https://github.com/Rikarin/Vixen/issues/706">#706</see> — both this line
    ///     and <c>SlopeBlur.rvn</c>'s said "the whole distance walked", which is the number only over
    ///     a field of unit slope.
    /// </param>
    /// <param name="samples">
    ///     How many steps the walk is cut into. ⚠ <b>This changes the answer wherever the field
    ///     curves</b>, which is the node rather than a quality setting — see <c>SlopeBlur.rvn</c>.
    ///     0 is a copy.
    /// </param>
    /// <param name="mode">How the samples are accumulated.</param>
    /// <returns>The op.</returns>
    public static TextureOp SlopeBlurOp(
        int output,
        int source,
        int slope,
        float intensity,
        int samples = 8,
        TextureSlopeMode mode = TextureSlopeMode.Blend
    ) =>
        new() {
            Kernel = SlopeBlur,
            Output = output,
            Inputs = [source, slope],
            Parameters = [
                new("intensity", intensity, TextureParameterUnit.TexelsAtBase),
                new("samples", samples),
                new("mode", (float)mode)
            ]
        };

    /// <summary>
    ///     Every op in a plan whose resolved length is past the kernel's own loop ceiling.
    /// </summary>
    /// <param name="plan">The plan to walk.</param>
    /// <returns>One line per offending op, empty when there is nothing to say.</returns>
    /// <remarks>
    ///     ⚠ <b>The table this walks is no longer here, and the move is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1011">#1011</a>.</b> A kernel's loop bound
    ///     is not a property of the Filters family: <c>Curvature</c>, <c>HeightToNormal</c> and
    ///     <c>EdgeDetect</c> clip the same way and were outside a table that lived on this class only
    ///     because <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a>'s batch did not own
    ///     <c>TexturePlan.cs</c>. <see cref="TextureKernelCeilings" /> holds all seven and the sweep
    ///     that keeps them whole; this stays as the name <c>TexturePlan.Validate</c> and the filter
    ///     suites already call, because two spellings of one walk is what the move was avoiding.
    /// </remarks>
    public static ImmutableArray<string> Verify(TexturePlan plan) => TextureKernelCeilings.Verify(plan);
}
