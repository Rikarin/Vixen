// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Editor.TextureGraph;

/// <summary>Which of <c>Shape</c>'s eight patterns an op draws.</summary>
/// <remarks>
///     ⚠ <b>The numbers are the contract with <c>Shaders/Shape.rvn</c>'s <c>kind</c>, which compares
///     against them literally.</b> Nothing in the compilation would notice a renumbering — the picture
///     would simply be a different shape, which is a perfectly plausible picture. What pins them is
///     <c>TextureSourceDeviceTests</c>, where every kind is identified by a closed form that only it
///     satisfies.
/// </remarks>
internal enum TextureShapeKind {
    /// <summary>A circle, with a soft edge of <c>falloff</c>.</summary>
    Disc = 0,

    /// <summary>An axis-aligned square, with a soft edge of <c>falloff</c>.</summary>
    Square = 1,

    /// <summary>An equilateral triangle pointing up at rotation zero, with a soft edge.</summary>
    Triangle = 2,

    /// <summary><c>1 − r²</c>. Ignores <c>falloff</c>.</summary>
    Paraboloid = 3,

    /// <summary>Worth exactly one part in a thousand at the boundary. Ignores <c>falloff</c>.</summary>
    Gaussian = 4,

    /// <summary><c>1 − r</c>. Ignores <c>falloff</c>.</summary>
    Cone = 5,

    /// <summary><c>1 − smoothstep(r)</c>. Ignores <c>falloff</c>.</summary>
    HalfBell = 6,

    /// <summary>A linear ramp along the shape's own x axis. Ignores <c>falloff</c>.</summary>
    Gradation = 7
}

/// <summary>Which sweep a <c>Gradient</c> op runs along its ramp.</summary>
internal enum TextureGradientKind {
    /// <summary>Along <c>angle</c>, centred on the centre.</summary>
    Linear = 0,

    /// <summary>Outwards from the centre.</summary>
    Radial = 1,

    /// <summary>Around the centre, starting at <c>angle</c>.</summary>
    Angular = 2,

    /// <summary>The linear sweep mirrored about the centre.</summary>
    Reflected = 3
}

/// <summary>Which lattice a <c>Noise</c> op draws from.</summary>
/// <remarks>
///     ⚠ <b>Doc 48 § 4.1 asks for a <c>[Permutation]</c> and this is a uniform.</b> There is no way to
///     select a permutation through the evaluator M1 built:
///     <c>TexturePlanEvaluator.VariantFor</c> compiles <c>EffectKey.Of(kernel)</c> with no defines and
///     caches on <c>(kernel, output format)</c>, so a permutation in a texture-graph kernel would take
///     its <c>.rvn</c> default in every op for ever. That is the registered-permutation trap arriving
///     from the other side — not a value missing from a key list, but no key list at all.
/// </remarks>
internal enum TextureNoiseBasis {
    /// <summary>A hash per lattice corner, smoothed. Grey.</summary>
    Value = 0,

    /// <summary>Perlin. Grey.</summary>
    Gradient = 1,

    /// <summary>Cellular. ⚠ Writes F1, F2 and a cell index into red, green and blue.</summary>
    Worley = 2,

    /// <summary>One uncorrelated value per <em>cell</em> — see <c>Noise.rvn</c> on why not per texel.</summary>
    White = 3
}

/// <summary>The eight source kernels of doc 48 § 4.1, as ops a plan can hold.</summary>
/// <remarks>
///     <para>
///         <b>Why builders rather than a <c>TextureOp</c> written out at each call site.</b>
///         <c>TexturePlanEvaluator.Uniforms</c> refuses an op that does not carry every parameter its
///         kernel declares — deliberately, because zero is a valid-looking number for almost all of
///         them. <c>Shape</c> declares six and <c>Noise</c> seven, so writing one out by hand is six
///         chances to produce an exception at bake time and, worse, one chance to produce a plausible
///         picture by naming the wrong one. Every builder here emits the complete set.
///     </para>
///     <para>
///         ⚠ <b>Every parameter is a scalar, and that is a property of the evaluator rather than a
///         style.</b> <c>Uniforms</c> writes one <see cref="float" /> per uniform-block member, so a
///         <c>float2</c> or <c>float4</c> member would receive its first component and zeros for the
///         rest — a centre of <c>(0.5, 0)</c> where <c>(0.5, 0.5)</c> was meant, which is a picture
///         nobody would call broken. So a colour is four parameters and a centre is two, in every
///         kernel under <c>Shaders/</c>.
///     </para>
///     <para>
///         <b>Nothing here is a <see cref="TextureParameterUnit.TexelsAtBase" />.</b> Not one of these
///         six kernels has a length in texels: a shape's scale, a gradient's span and a noise's
///         frequency are all fractions of the image, so each is the same picture at every resolution
///         without the evaluator scaling anything. Doc 48 § D8's rule applies to the filters, and this
///         is the half of § 4.1 it costs nothing.
///     </para>
///     <para>
///         <b>Internal, because nothing outside this assembly has a caller yet.</b> The node classes
///         of § M4 are what will want these public, and they are the ones who should widen them.
///     </para>
/// </remarks>
internal static class TextureSources {
    /// <summary>A constant colour or grey.</summary>
    /// <param name="output">The image to fill.</param>
    /// <param name="red">The red channel, linear.</param>
    /// <param name="green">The green channel.</param>
    /// <param name="blue">The blue channel.</param>
    /// <param name="alpha">The alpha channel.</param>
    /// <returns>The op.</returns>
    public static TextureOp Uniform(int output, float red, float green, float blue, float alpha = 1f) =>
        new() {
            Kernel = "Uniform",
            Output = output,
            Parameters = [new("red", red), new("green", green), new("blue", blue), new("alpha", alpha)]
        };

    /// <summary>A constant grey, which is the same kernel with one number.</summary>
    /// <param name="output">The image to fill.</param>
    /// <param name="grey">The value every colour channel takes.</param>
    /// <returns>The op.</returns>
    public static TextureOp Uniform(int output, float grey) => Uniform(output, grey, grey, grey);

    /// <summary>An imported image, resampled into the op's own resolution.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The external image the caller supplies.</param>
    /// <param name="srgb">
    ///     Whether the asset's declared colour space is sRGB. ⚠ The decode happens here, once, and
    ///     before the filter — see <c>Bitmap.rvn</c> for why those are two separate claims.
    /// </param>
    /// <param name="bilinear">Whether to interpolate, rather than take the nearest texel.</param>
    /// <returns>The op.</returns>
    public static TextureOp Bitmap(int output, int source, bool srgb, bool bilinear = true) =>
        new() {
            Kernel = "Bitmap",
            Output = output,
            Inputs = [source],
            Parameters = [new("srgb", srgb ? 1f : 0f), new("filter", bilinear ? 1f : 0f)]
        };

    /// <summary>A sweep along a ramp.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="ramp">
    ///     The ramp strip, as an external image. Row 0 is read and interpolated along x. It is baked
    ///     from <c>Vixen.Ui.Controls.Advanced.Gradient</c> by the node, so that the control's own stop
    ///     lists and interpolation space are evaluated once, by the class that owns them.
    /// </param>
    /// <param name="kind">Which sweep.</param>
    /// <param name="angle">Radians, clockwise on screen.</param>
    /// <param name="centreX">Where it starts, in 0..1 of the image.</param>
    /// <param name="centreY">Where it starts, in 0..1 of the image.</param>
    /// <param name="scale">How much of the image the sweep spans.</param>
    /// <returns>The op.</returns>
    public static TextureOp Gradient(
        int output,
        int ramp,
        TextureGradientKind kind = TextureGradientKind.Linear,
        float angle = 0f,
        float centreX = 0.5f,
        float centreY = 0.5f,
        float scale = 1f
    ) =>
        new() {
            Kernel = "Gradient",
            Output = output,
            Inputs = [ramp],
            Parameters = [
                new("kind", (float)kind),
                new("angle", angle),
                new("centreX", centreX),
                new("centreY", centreY),
                new("scale", scale)
            ]
        };

    /// <summary>One of the eight analytic patterns.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="kind">Which pattern.</param>
    /// <param name="scale">Its diameter as a fraction of the image.</param>
    /// <param name="rotation">Radians, clockwise on screen.</param>
    /// <param name="falloff">
    ///     The width of the soft edge, in units of the radius. ⚠ Read only by
    ///     <see cref="TextureShapeKind.Disc" />, <see cref="TextureShapeKind.Square" /> and
    ///     <see cref="TextureShapeKind.Triangle" />; the five profiles carry their falloff in their
    ///     formula.
    /// </param>
    /// <param name="centreX">Where, in 0..1 of the image.</param>
    /// <param name="centreY">Where, in 0..1 of the image.</param>
    /// <returns>The op.</returns>
    public static TextureOp Shape(
        int output,
        TextureShapeKind kind = TextureShapeKind.Disc,
        float scale = 1f,
        float rotation = 0f,
        float falloff = 0.01f,
        float centreX = 0.5f,
        float centreY = 0.5f
    ) =>
        new() {
            Kernel = "Shape",
            Output = output,
            Parameters = [
                new("kind", (float)kind),
                new("scale", scale),
                new("rotation", rotation),
                new("falloff", falloff),
                new("centreX", centreX),
                new("centreY", centreY)
            ]
        };

    /// <summary>A noise field.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="basis">Which lattice.</param>
    /// <param name="scale">How many cells across the image at the first octave.</param>
    /// <param name="octaves">How many are summed. ⚠ Ignored by <see cref="TextureNoiseBasis.Worley" />.</param>
    /// <param name="lacunarity">The frequency multiplier between octaves.</param>
    /// <param name="gain">The amplitude multiplier between octaves.</param>
    /// <param name="tiling">
    ///     Whether the lattice wraps. ⚠ Exact only when <paramref name="scale" /> and
    ///     <paramref name="lacunarity" /> are whole numbers — there is no integer period for a lattice
    ///     of 5.5 cells to wrap at, and the kernel rounds rather than refusing.
    /// </param>
    /// <returns>The op. Its <c>seed</c> is supplied by the evaluator from <see cref="TexturePlan.SeedFor" />.</returns>
    public static TextureOp Noise(
        int output,
        TextureNoiseBasis basis = TextureNoiseBasis.Value,
        float scale = 8f,
        int octaves = 1,
        float lacunarity = 2f,
        float gain = 0.5f,
        bool tiling = false
    ) =>
        new() {
            Kernel = "Noise",
            Output = output,
            Parameters = [
                new("basis", (float)basis),
                new("scale", scale),
                new("octaves", octaves),
                new("lacunarity", lacunarity),
                new("gain", gain),
                new("tiling", tiling ? 1f : 0f)
            ]
        };

    /// <summary>A checkerboard.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="scaleX">Cells across the image horizontally.</param>
    /// <param name="scaleY">Cells across the image vertically.</param>
    /// <param name="rotation">Radians, clockwise on screen, about the image centre.</param>
    /// <param name="offsetX">A shift in cells.</param>
    /// <param name="offsetY">A shift in cells.</param>
    /// <returns>The op.</returns>
    public static TextureOp Checker(
        int output,
        float scaleX = 8f,
        float scaleY = 8f,
        float rotation = 0f,
        float offsetX = 0f,
        float offsetY = 0f
    ) =>
        new() {
            Kernel = "Checker",
            Output = output,
            Parameters = [
                new("scaleX", scaleX),
                new("scaleY", scaleY),
                new("rotation", rotation),
                new("offsetX", offsetX),
                new("offsetY", offsetY)
            ]
        };

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what a test over the builders prints on the day one of them is forgotten.</b> A
    ///     theory with an <c>InlineData</c> per builder passes silently when a seventh is added and
    ///     not listed; this list is what the parameter-agreement test walks, so a builder reaches it
    ///     by existing.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [
        Uniform(0, 0.5f),
        Bitmap(0, 1, srgb: true),
        Gradient(0, 1),
        Shape(0),
        Noise(0),
        Checker(0)
    ];
}
