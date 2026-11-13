// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>A constant colour.</summary>
/// <remarks>
///     <b>The shape every source node has: no image input, one image output, and numbers.</b> Its
///     colour is one <c>Float4</c> port rather than four scalars, because that is four boxes in one
///     row on the node and one wire when something eventually drives it — while the <em>kernel</em>
///     takes four separate uniforms, because every member of a plan's uniform block is a scalar. The
///     two shapes meet in <see cref="Compile" /> and nowhere else.
/// </remarks>
[Node("Source/Uniform", Preview = true, Summary = "A constant colour.")]
sealed partial class UniformNode : TextureNode {
    /// <summary>The colour, linear, with its alpha.</summary>
    [Input(Name = "Colour", Default = [1f, 1f, 1f, 1f])]
    public Float4 Colour;

    /// <summary>The image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        // Colour whatever is downstream, because a constant with an alpha is not a mask. A graph that
        // wanted a grey constant wants a Levels or a Grayscale after it, and pays one dispatch.
        var target = emitter.Write("Out", TextureChannels.Colour);

        emitter.Dispatch(
            TextureSources.Uniform(
                target,
                emitter.Number("Colour", 0),
                emitter.Number("Colour", 1),
                emitter.Number("Colour", 2),
                emitter.Number("Colour", 3)
            )
        );
    }
}

/// <summary>A noise field.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Its output is grey for four of the five bases and colour for <c>Worley</c></b>, which is
///         the cheapest demonstration that a node's channels are its own decision rather than its
///         input's: <c>Worley</c> writes F1, F2 and a cell index into red, green and blue, so calling
///         it grey would silently throw two thirds of the answer away at the first thing that read it.
///     </para>
///     <para>
///         <b>The seed is not a port and not a setting.</b> Doc 48 § D5 — a noise whose output changes
///         between runs is not a source asset — and the plan already answers it:
///         <c>TexturePlan.SeedFor</c> mixes the plan's seed with <c>TextureOp.Identity</c> — which
///         the compiler derives from the node that emitted the op — so two Noise nodes in one graph
///         differ and the same graph is the same picture on every machine. ⚠ It mixed the op's
///         <em>index</em> until <a href="https://github.com/Rikarin/Vixen/issues/875">#875</a>, which
///         made this node's picture a function of how many ops happened to precede it.
///     </para>
/// </remarks>
[Node("Source/Noise", Preview = true, Summary = "Value, gradient, Worley, white or gaussian noise.")]
sealed partial class NoiseNode : TextureNode {
    /// <summary>
    ///     Which lattice: <c>Value</c>, <c>Gradient</c>, <c>Worley</c>, <c>White</c> or
    ///     <c>Gaussian</c>.
    /// </summary>
    [Setting(AcceptedFrom = typeof(TextureNoiseBasis))]
    public string Basis = "Value";

    /// <summary>
    ///     How each octave is folded before the sum: <c>Plain</c>, <c>Ridged</c> or <c>Billow</c>.
    ///     Worley folds F1 and F2 and leaves the cell index alone.
    /// </summary>
    [Setting(AcceptedFrom = typeof(TextureNoiseProfile))]
    public string Profile = "Plain";

    /// <summary>
    ///     What a cell's shape is: <c>Euclidean</c> — round — <c>Chebyshev</c> — square — or
    ///     <c>Manhattan</c> — diamond. ⚠ Read by <c>Worley</c> and ignored by the other three.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A setting that three of the four bases ignore, and there is no way for the node to say
    ///         so
    ///     </b>
    ///     — <c>Shape</c>'s <c>Falloff</c> has exactly the same shape, and a setting cannot
    ///     hide a setting. What it buys is the whole of a cellular pattern library that is not round:
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1101">#1101</a> found it authoring
    ///     <c>Patterns/Cells</c>, where tile, cracked mud and snake skin all want a metric that was
    ///     not there.
    /// </remarks>
    [Setting(AcceptedFrom = typeof(TextureCellMetric))]
    public string Metric = "Euclidean";

    /// <summary>How many cells across the image at the first octave.</summary>
    /// <remarks>
    ///     ⚠ Whole cells along an axis that tiles, because a lattice can only wrap at a whole number:
    ///     5.5 draws 6, so the slider moves in steps there. The summary is what says so, since every
    ///     fractional scale in a shipped compound would otherwise be a warning on every compile.
    /// </remarks>
    [Input(Minimum = 0f, Summary = "Cells across the image. Rounded to a whole number along an axis that tiles.")]
    public Scalar Scale = 8f;

    /// <summary>A multiplier on <see cref="Scale" /> along x, for a lattice stretched along one axis.</summary>
    [Input(Name = "Scale X", Minimum = 0f, SoftMaximum = 8f, Summary = "A multiplier on Scale along x.")]
    public Scalar ScaleX = 1f;

    /// <summary>A multiplier on <see cref="Scale" /> along y.</summary>
    [Input(Name = "Scale Y", Minimum = 0f, SoftMaximum = 8f, Summary = "A multiplier on Scale along y.")]
    public Scalar ScaleY = 1f;

    /// <summary>
    ///     A shift of the lattice along x, in cells of the first octave's lattice — so half is half a cell,
    ///     the shift a running bond wants, whatever the scale.
    /// </summary>
    [Input(Name = "Offset X", SoftMinimum = 0f, SoftMaximum = 1f, Summary = "A shift along x, in cells.")]
    public Scalar OffsetX = 0f;

    /// <summary>A shift of the lattice along y, in cells.</summary>
    [Input(Name = "Offset Y", SoftMinimum = 0f, SoftMaximum = 1f, Summary = "A shift along y, in cells.")]
    public Scalar OffsetY = 0f;

    /// <summary>How many are summed. Worley sums F1 and F2, and keeps the first octave's cell index.</summary>
    [Input(Minimum = 1f, Maximum = 8f)]
    public Int Octaves = 1;

    /// <summary>The first octave summed: the coarser ones are skipped, at their own frequencies and hashes.</summary>
    [Input(Name = "Min Octave", Minimum = 0f, Maximum = 7f)]
    public Int MinOctave = 0;

    /// <summary>The frequency multiplier between octaves.</summary>
    [Input(Minimum = 1f)]
    public Scalar Lacunarity = 2f;

    /// <summary>The amplitude multiplier between octaves.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Gain = 0.5f;

    /// <summary>
    ///     Another draw of the same noise: every whole number is a different pattern with the same character, and
    ///     nought is the graph's own, so a picture drawn before the port existed is the picture it was.
    /// </summary>
    [Input(Minimum = 0f, SoftMaximum = 100f, Summary = "Another draw of the same noise. 0 is the graph's own.")]
    public Int Seed = 0;

    /// <summary>The field.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var basis = TextureSettings.Enum(emitter, nameof(Basis), TextureNoiseBasis.Value);

        var target = emitter.Write(
            "Out",
            basis == TextureNoiseBasis.Worley ? TextureChannels.Colour : TextureChannels.Grey
        );

        emitter.Dispatch(
            TextureSources.Noise(
                target,
                basis,
                emitter.Number(nameof(Scale)),
                emitter.Integer(nameof(Octaves)),
                emitter.Number(nameof(Lacunarity)),
                emitter.Number(nameof(Gain)),
                TextureSettings.Enum(emitter, nameof(Metric), TextureCellMetric.Euclidean),
                emitter.Number("Scale X"),
                emitter.Number("Scale Y"),
                emitter.Number("Offset X"),
                emitter.Number("Offset Y"),
                emitter.Integer("Min Octave"),
                TextureSettings.Enum(emitter, nameof(Profile), TextureNoiseProfile.Plain),
                emitter.Integer(nameof(Seed))
            )
        );
    }
}

/// <summary>A blue noise: a rank order whose every threshold is evenly spread points, with no clumps.</summary>
/// <remarks>
///     <para>
///         <b>A void-and-cluster tile, and not a filtered white noise</b> (Patina #167). Every threshold of it is a
///         set of points spread as evenly as that many can be, which is what a stipple, a dither or a scatter that
///         must not clump wants. The tile is made once on the CPU by <c>TextureBlueNoise</c> and reaches the kernel
///         as an external image, as a gradient's ramp does.
///     </para>
///     <para>
///         ⚠ <b>A seed moves the tile rather than remaking it</b>: a shift on the torus and one of the square's
///         eight symmetries, each of which keeps every distance between points. So every seed is as blue as the
///         tile, and every seed tiles.
///     </para>
///     <para>
///         <b>Point sampled</b>, because a blue noise read between texels is a blurred one and a blur removes the
///         very band this noise is empty in. At the default <c>Scale</c> one tile texel is one image texel at a
///         1024 base.
///     </para>
/// </remarks>
[Node(
    "Source/Blue Noise",
    Preview = true,
    Summary = "Evenly spread noise with no clumps: every threshold is a well-spaced set of points."
)]
sealed partial class BlueNoiseNode : TextureNode {
    /// <summary>How many tiles across the image, and down. Each tile is 64 texels square.</summary>
    [Input(Minimum = 1f, SoftMaximum = 64f)]
    public Int Scale = 16;

    /// <summary>Another draw of the same noise; nought is the graph's own.</summary>
    [Input(Minimum = 0f, SoftMaximum = 100f, Summary = "Another draw of the same noise. 0 is the graph's own.")]
    public Int Seed = 0;

    /// <summary>The noise: each texel's rank in its tile, 0 to 1.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var tile = emitter.External(
            TextureFormat.R32Float,
            TextureChannels.Grey,
            TextureBlueNoise.Side,
            TextureBlueNoise.Side,
            TextureBlueNoise.Bytes()
        );
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (tile < 0) {
            return;
        }

        emitter.Dispatch(
            TextureSources.BlueNoise(target, tile, emitter.Integer(nameof(Scale)), emitter.Integer(nameof(Seed)))
        );
    }
}

/// <summary>One of fifteen analytic patterns, on a grey.</summary>
/// <remarks>
///     <para>
///         <b>
///             The splatter's usual pattern input, which is why it is worth having as its own
///             node
///         </b>
///         rather than as a mode of something larger: <c>Tile Sampler</c> and
///         <c>Splatter</c> read a stamp, and a disc with a soft edge is the stamp most graphs want.
///     </para>
///     <para>
///         ⚠ <b><c>Falloff</c> is read by eight of the fifteen kinds and ignored by seven</b>, which is
///         the kernel's arrangement rather than this node's: <c>Paraboloid</c>, <c>Gaussian</c>,
///         <c>Cone</c>, <c>HalfBell</c>, <c>Gradation</c>, <c>Pyramid</c> and <c>Hemisphere</c> carry
///         their softness in their formula. Turning it on one of those does nothing, and there is no
///         way for a port to say so — a setting cannot hide a port. <c>Sides</c> and <c>Inner</c> have
///         the same shape: each is read by the kinds its summary names.
///     </para>
///     <para>
///         <b><c>Polygon</c> with a <c>Sides</c> port is Patina #168's most-used missing node</b>, and
///         it is a kind here rather than a node of its own because it is the same picture under the
///         same scale, rotation, falloff and centre as every other kind: a regular polygon of three
///         sides is exactly <c>Triangle</c>, which <c>TextureShapeFamilyDeviceTests</c> holds.
///     </para>
///     <para>
///         <b>Its rotation is in radians, and so is every other angle in this assembly.</b> It was
///         not: <c>Transform 2D</c>'s identical-looking number was a whole turn until
///         <a href="https://github.com/Rikarin/Vixen/issues/735">#735</a>. No node converts — a
///         number means the same thing in the graph and in the plan — so what makes the units agree
///         is that the kernels agree, and <c>TextureAngleUnitTests</c> is what keeps them agreeing.
///     </para>
/// </remarks>
[Node(
    "Source/Shape",
    Preview = true,
    Summary = "A disc, polygon, star, ring, capsule or crescent, or one of seven profiles."
)]
sealed partial class ShapeNode : TextureNode {
    /// <summary>
    ///     Which pattern: <c>Disc</c>, <c>Square</c>, <c>Triangle</c>, <c>Paraboloid</c>,
    ///     <c>Gaussian</c>, <c>Cone</c>, <c>HalfBell</c>, <c>Gradation</c>, <c>Polygon</c>, <c>Star</c>,
    ///     <c>Ring</c>, <c>Capsule</c>, <c>Crescent</c>, <c>Pyramid</c> or <c>Hemisphere</c>.
    /// </summary>
    [Setting(AcceptedFrom = typeof(TextureShapeKind))]
    public string Kind = "Disc";

    /// <summary>The shape's diameter, as a fraction of the image.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Scale = 1f;

    /// <summary>How far it turns, in radians, clockwise on screen.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Rotation = 0f;

    /// <summary>The width of the soft edge, in units of the radius. Read by the eight boundary kinds.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Falloff = 0.01f;

    /// <summary>How many sides a <c>Polygon</c> has, or points a <c>Star</c>.</summary>
    [Input(Minimum = 3f, Maximum = 64f, Summary = "A polygon's sides or a star's points.")]
    public Int Sides = 6;

    /// <summary>A star's valleys, a ring's hole, a capsule's half thickness or a crescent's cut, in radii.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 1f,
        Summary = "Star valleys, ring hole, capsule half thickness or crescent cut, in radii."
    )]
    public Scalar Inner = 0.5f;

    /// <summary>Where it sits along x, in 0..1 of the image.</summary>
    [Input(Name = "Centre X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreX = 0.5f;

    /// <summary>Where it sits along y.</summary>
    [Input(Name = "Centre Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreY = 0.5f;

    /// <summary>
    ///     Whether the shape keeps its own proportions on an image that is not square: it is drawn in a
    ///     square of the shorter side, repeated along the longer one, so a disc on 2:1 is round. Off, it
    ///     stretches with the image, which is what a face of the same shape undoes.
    /// </summary>
    [Input(Name = "Keep Aspect", Summary = "Draw in a square of the shorter side, repeated along the longer.")]
    public Bool KeepAspect = false;

    /// <summary>The pattern.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var kind = TextureSettings.Enum(emitter, nameof(Kind), TextureShapeKind.Disc);

        // Grey whatever the kind: every one of the fifteen writes one value into three lanes.
        var target = emitter.Write("Out", TextureChannels.Grey);

        emitter.Dispatch(
            TextureSources.Shape(
                target,
                kind,
                emitter.Number(nameof(Scale)),
                emitter.Number(nameof(Rotation)),
                emitter.Number(nameof(Falloff)),
                emitter.Number("Centre X"),
                emitter.Number("Centre Y"),
                emitter.Integer(nameof(Sides)),
                emitter.Number(nameof(Inner)),
                emitter.Number("Keep Aspect")
            )
        );
    }
}

/// <summary>A checkerboard.</summary>
/// <remarks>
///     <b>Two scales rather than one, because a brick is a checker with an aspect.</b> The pair is
///     also what makes the node resolution-independent for free: a count of cells across the image is
///     the same picture at every bake, so nothing here is a
///     <see cref="TextureParameterUnit.TexelsAtBase" />.
/// </remarks>
[Node("Source/Checker", Preview = true, Summary = "A checkerboard, with a scale per axis, a rotation and a shift.")]
sealed partial class CheckerNode : TextureNode {
    /// <summary>Cells across the image horizontally.</summary>
    [Input(Name = "Scale X")]
    public Scalar ScaleX = 8f;

    /// <summary>Cells across it vertically.</summary>
    [Input(Name = "Scale Y")]
    public Scalar ScaleY = 8f;

    /// <summary>How far the grid turns about the image centre, in radians.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Rotation = 0f;

    /// <summary>A shift along x, in cells.</summary>
    [Input(Name = "Offset X")]
    public Scalar OffsetX = 0f;

    /// <summary>A shift along y, in cells.</summary>
    [Input(Name = "Offset Y")]
    public Scalar OffsetY = 0f;

    /// <summary>The board.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var target = emitter.Write("Out", TextureChannels.Grey);

        emitter.Dispatch(
            TextureSources.Checker(
                target,
                emitter.Number("Scale X"),
                emitter.Number("Scale Y"),
                emitter.Number(nameof(Rotation)),
                emitter.Number("Offset X"),
                emitter.Number("Offset Y")
            )
        );
    }
}

/// <summary>Which colour space an imported picture's values are in.</summary>
/// <remarks>
///     ⚠
///     <b>
///         Doc 48 § 4.1: "an sRGB texture decoded as linear and then blended is the commonest
///         wrong-looking graph there is."
///     </b>
///     The decode happens once, in <c>Bitmap.rvn</c>, at the only
///     node that touches an asset — so every image <em>inside</em> a graph is linear by construction
///     and no other kernel has to ask.
/// </remarks>
enum TextureColourSpace {
    /// <summary>Already linear: a mask, a height, a normal map, a roughness.</summary>
    Linear = 0,

    /// <summary>Encoded, and decoded on the way in. What a colour map from a paint tool is.</summary>
    Srgb = 1
}

/// <summary>An imported image.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D8's one absolute size.</b> Every other image in a plan is a level offset from
///         the graph's base resolution; this one is whatever the picture is, and <c>Bitmap.rvn</c>
///         resamples it into the resolution of the image the op writes. That is why it is the first
///         node that allocates an <em>external</em> image — one the plan does not allocate, does not
///         pool and never writes.
///     </para>
///     <para>
///         ⚠ <b>The asset's name crosses and its pixels do not.</b> A compilation runs on every edit
///         and must not read an asset database, so what the compiler carries is the reference —
///         <c>TextureGraphCompiler.Externals</c> — and a host that has a database resolves it and
///         uploads it. <a href="https://github.com/Rikarin/Vixen/issues/732">#732</a> is the gap this
///         closes. <c>TextureProjectImages</c> is what walks that list here, for the CLI and the
///         editor, and a host outside this tree walks it the same way.
///     </para>
///     <para>
///         ⚠
///         <b>
///             No minification filter, which is the kernel's property and worth knowing before
///             importing a 4K stamp.
///         </b>
///         A bitmap larger than the image it is resampled into is
///         undersampled and will alias; <c>Space/Resample</c> is where that is answered, because only
///         there is the target's size the whole of the answer.
///     </para>
/// </remarks>
[Node("Source/Bitmap", Preview = true, Summary = "An imported image, resampled into the graph's resolution.")]
sealed partial class BitmapNode : TextureNode {
    /// <summary>The imported image this graph reads, as a host resolves it.</summary>
    [Setting(Name = "Source", Summary = "The imported image, by the reference a host resolves.")]
    public string Asset = string.Empty;

    /// <summary>Whether the asset's values need decoding: <c>Linear</c> or <c>Srgb</c>.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         <c>Linear</c> by default, which is the kernel's own default and is deliberately the
    ///         one that does nothing.
    ///     </b>
    ///     <c>Bitmap.rvn</c> calls this "the asset's declared space
    ///     arriving as a number" — it is a fact about the picture, and the node cannot see the
    ///     picture. Defaulting to <c>Srgb</c> would darken every imported mask by a curve nobody
    ///     asked for, which is as silent as the over-bright colour map it would fix; a host that has
    ///     resolved the asset is what can answer honestly.
    /// </remarks>
    [Setting(AcceptedFrom = typeof(TextureColourSpace))]
    public string Space = "Linear";

    /// <summary>How it is resampled: <c>Point</c> or <c>Bilinear</c>. ⚠ Not <c>Box</c>.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Listed rather than <c>AcceptedFrom = typeof(TextureFilter)</c>, because this node
    ///         refuses the enum's third member.
    ///     </b>
    ///     A picker generated off the whole enum would offer
    ///     <c>Box</c> and the compiler would then refuse it — which is the defect
    ///     <c>[Setting(Accepted = …)]</c> exists to remove, arriving from the other side.
    ///     <c>TextureSettingChoiceTests</c> holds the narrowing to a real refusal rather than to an
    ///     opinion, so this list cannot quietly drop a value the node would have accepted.
    /// </remarks>
    [Setting(Accepted = ["Point", "Bilinear"])]
    public string Filter = "Bilinear";

    /// <summary>The picture.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var space = TextureSettings.Enum(emitter, nameof(Space), TextureColourSpace.Linear);
        var filter = TextureSettings.Enum(emitter, nameof(Filter), TextureFilter.Bilinear);
        var asset = emitter.Text("Source").Trim();

        if (asset.Length == 0) {
            // ⚠ Refused rather than filled with black. An external image nothing supplies is an
            // exception at bake time — `ExternalViews` refuses it — and an empty reference is the one
            // case where a compiler can name the node instead.
            emitter.Report(
                TextureDiagnostics.NoImage,
                "This node has no image. A bitmap's pixels come from an imported asset, so a reference is what "
                + "fills it — there is nothing it could draw instead.",
                "Source"
            );

            return;
        }

        if (filter == TextureFilter.Box) {
            // `Bitmap.rvn` compares `filter` against 0 and interpolates for everything else, so a
            // `Box` here would be a bilinear read under the name of a box filter — `CropNode`'s
            // refusal, for the same reason.
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                $"'{nameof(Filter)}' is 'Box', and 'Bitmap' takes Point or Bilinear. A box needs a minification "
                + "ratio, which is Space/Resample's question rather than this node's.",
                nameof(Filter)
            );

            return;
        }

        // Rgba8 because that is what an imported colour map is, and because a plan may *read* a
        // format no kernel can write — see `TextureFormats.IsStorable`. Colour, because a picture
        // with an alpha is not a mask.
        var source = emitter.External(TextureFormat.Rgba8, TextureChannels.Colour, asset);
        var target = emitter.Write("Out", TextureChannels.Colour);

        emitter.Dispatch(
            TextureSources.Bitmap(target, source, space == TextureColourSpace.Srgb, filter == TextureFilter.Bilinear)
        );
    }
}

/// <summary>A sweep along a ramp: linear, radial, angular or reflected.</summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The ramp is an image and not a list of uniforms, which is what keeps this from being
///             a second opinion about what a gradient means.
///         </b>
///         <c>Vixen.Ui.Controls.Advanced</c>'s <c>Gradient</c> already decides that, including which
///         of three spaces the stops are mixed in — sRGB, linear and Oklab disagree visibly — so the
///         strip is baked from <em>that</em> evaluator by <see cref="TextureRamp.FromRamp" /> and the
///         kernel only decides where along it a texel falls.
///     </para>
///     <para>
///         <b>With no ramp named the strip is black to white</b>, and that is a real ramp through the
///         real path rather than a placeholder: a linear sweep over it at rotation zero is exactly
///         <c>(x + 0.5) / width</c>, which is the closed form <c>Gradient.rvn</c>'s remarks name.
///     </para>
/// </remarks>
[DoesNotTile("A sweep ends on a different value from the one it starts on, which is what a sweep is.")]
[Node("Source/Gradient", Preview = true, Summary = "A linear, radial, angular or reflected sweep along a ramp.")]
sealed partial class GradientNode : TextureNode {
    /// <summary>Which sweep: <c>Linear</c>, <c>Radial</c>, <c>Angular</c> or <c>Reflected</c>.</summary>
    [Setting(AcceptedFrom = typeof(TextureGradientKind))]
    public string Kind = "Linear";

    /// <summary>The gradient asset the strip is baked from, or empty for black to white.</summary>
    [Setting(Name = "Ramp", Summary = "The gradient asset a host resolves. Empty is black to white.")]
    public string RampAsset = string.Empty;

    /// <summary>The direction of a linear or reflected sweep, in radians, clockwise on screen.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Angle = 0f;

    /// <summary>Where it is centred along x, in 0..1 of the image.</summary>
    [Input(Name = "Centre X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreX = 0.5f;

    /// <summary>Where it is centred along y.</summary>
    [Input(Name = "Centre Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreY = 0.5f;

    /// <summary>How much of the image the sweep spans. 1 spans it; 0.5 spans the middle half.</summary>
    [Input(Minimum = 0f)]
    public Scalar Scale = 1f;

    /// <summary>The sweep.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var kind = TextureSettings.Enum(emitter, nameof(Kind), TextureGradientKind.Linear);
        var ramp = TextureTables.Ramp(emitter, "Ramp");

        if (ramp < 0) {
            return;
        }

        // Colour, because a ramp is RGBA whatever the sweep. A graph wanting a grey sweep puts a
        // Grayscale after it and pays one dispatch, which is `UniformNode`'s bargain.
        var target = emitter.Write("Out", TextureChannels.Colour);

        emitter.Dispatch(
            TextureSources.Gradient(
                target,
                ramp,
                kind,
                emitter.Number(nameof(Angle)),
                emitter.Number("Centre X"),
                emitter.Number("Centre Y"),
                emitter.Number(nameof(Scale))
            )
        );
    }
}
