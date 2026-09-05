// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

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
///         ⚠ <b>Its output is grey for three of the four bases and colour for the fourth</b>, which is
///         the cheapest demonstration that a node's channels are its own decision rather than its
///         input's: <c>Worley</c> writes F1, F2 and a cell index into red, green and blue, so calling
///         it grey would silently throw two thirds of the answer away at the first thing that read it.
///     </para>
///     <para>
///         <b>The seed is not a port and not a setting.</b> Doc 48 § D5 — a noise whose output changes
///         between runs is not a source asset — and the plan already answers it:
///         <c>TexturePlan.SeedFor</c> mixes the plan's seed with the op's own index, so two Noise
///         nodes in one graph differ and the same graph is the same picture on every machine.
///     </para>
/// </remarks>
[Node("Source/Noise", Preview = true, Summary = "Value, gradient, Worley or white noise.")]
sealed partial class NoiseNode : TextureNode {
    /// <summary>Which lattice: <c>Value</c>, <c>Gradient</c>, <c>Worley</c> or <c>White</c>.</summary>
    [Setting]
    public string Basis = "Value";

    /// <summary>How many cells across the image at the first octave.</summary>
    [Input]
    public Scalar Scale = 8f;

    /// <summary>How many are summed. Ignored by <c>Worley</c>.</summary>
    [Input]
    public Int Octaves = 1;

    /// <summary>The frequency multiplier between octaves.</summary>
    [Input]
    public Scalar Lacunarity = 2f;

    /// <summary>The amplitude multiplier between octaves.</summary>
    [Input]
    public Scalar Gain = 0.5f;

    /// <summary>Whether the lattice wraps, so the picture tiles.</summary>
    [Input]
    public Bool Tiling = false;

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
                emitter.Flag(nameof(Tiling))
            )
        );
    }
}

/// <summary>One of eight analytic patterns, on a grey.</summary>
/// <remarks>
///     <para>
///         <b>The splatter's usual pattern input, which is why it is worth having as its own
///         node</b> rather than as a mode of something larger: <c>Tile Sampler</c> and
///         <c>Splatter</c> read a stamp, and a disc with a soft edge is the stamp most graphs want.
///     </para>
///     <para>
///         ⚠ <b><c>Falloff</c> is read by three of the eight kinds and ignored by five</b>, which is
///         the kernel's arrangement rather than this node's: <c>Paraboloid</c>, <c>Gaussian</c>,
///         <c>Cone</c>, <c>HalfBell</c> and <c>Gradation</c> carry their softness in their formula.
///         Turning it on one of those does nothing, and there is no way for a port to say so — a
///         setting cannot hide a port.
///     </para>
///     <para>
///         <b>Its rotation is in radians, and so is every other angle in this assembly.</b> It was
///         not: <c>Transform 2D</c>'s identical-looking number was a whole turn until
///         <a href="https://github.com/Rikarin/Vixen/issues/735">#735</a>. No node converts — a
///         number means the same thing in the graph and in the plan — so what makes the units agree
///         is that the kernels agree, and <c>TextureAngleUnitTests</c> is what keeps them agreeing.
///     </para>
/// </remarks>
[Node("Source/Shape", Preview = true, Summary = "A disc, square, triangle or one of five falloff profiles.")]
sealed partial class ShapeNode : TextureNode {
    /// <summary>
    ///     Which pattern: <c>Disc</c>, <c>Square</c>, <c>Triangle</c>, <c>Paraboloid</c>,
    ///     <c>Gaussian</c>, <c>Cone</c>, <c>HalfBell</c> or <c>Gradation</c>.
    /// </summary>
    [Setting]
    public string Kind = "Disc";

    /// <summary>The shape's diameter, as a fraction of the image.</summary>
    [Input]
    public Scalar Scale = 1f;

    /// <summary>How far it turns, in radians, clockwise on screen.</summary>
    [Input]
    public Scalar Rotation = 0f;

    /// <summary>The width of the soft edge, in units of the radius. Read by three kinds of eight.</summary>
    [Input]
    public Scalar Falloff = 0.01f;

    /// <summary>Where it sits along x, in 0..1 of the image.</summary>
    [Input(Name = "Centre X")]
    public Scalar CentreX = 0.5f;

    /// <summary>Where it sits along y.</summary>
    [Input(Name = "Centre Y")]
    public Scalar CentreY = 0.5f;

    /// <summary>The pattern.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var kind = TextureSettings.Enum(emitter, nameof(Kind), TextureShapeKind.Disc);

        // Grey whatever the kind: every one of the eight writes one value into three lanes.
        var target = emitter.Write("Out", TextureChannels.Grey);

        emitter.Dispatch(
            TextureSources.Shape(
                target,
                kind,
                emitter.Number(nameof(Scale)),
                emitter.Number(nameof(Rotation)),
                emitter.Number(nameof(Falloff)),
                emitter.Number("Centre X"),
                emitter.Number("Centre Y")
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
    [Input]
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
///     ⚠ <b>Doc 48 § 4.1: "an sRGB texture decoded as linear and then blended is the commonest
///     wrong-looking graph there is."</b> The decode happens once, in <c>Bitmap.rvn</c>, at the only
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
///         closes; ⚠ what it does <em>not</em> close is that no host in this tree walks that list for
///         an asset yet, so a graph containing a Bitmap compiles and does not bake.
///     </para>
///     <para>
///         ⚠ <b>No minification filter, which is the kernel's property and worth knowing before
///         importing a 4K stamp.</b> A bitmap larger than the image it is resampled into is
///         undersampled and will alias; <c>Space/Resample</c> is where that is answered, because only
///         there is the target's size the whole of the answer.
///     </para>
/// </remarks>
[Node("Source/Bitmap", Preview = true, Summary = "An imported image, resampled into the graph's resolution.")]
sealed partial class BitmapNode : TextureNode {
    /// <summary>The imported image this graph reads, as a host resolves it.</summary>
    [Setting(Name = "Source", Summary = "The imported image, by the reference a host resolves.")]
    public string Asset = "";

    /// <summary>Whether the asset's values need decoding: <c>Linear</c> or <c>Srgb</c>.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Linear</c> by default, which is the kernel's own default and is deliberately the
    ///     one that does nothing.</b> <c>Bitmap.rvn</c> calls this "the asset's declared space
    ///     arriving as a number" — it is a fact about the picture, and the node cannot see the
    ///     picture. Defaulting to <c>Srgb</c> would darken every imported mask by a curve nobody
    ///     asked for, which is as silent as the over-bright colour map it would fix; a host that has
    ///     resolved the asset is what can answer honestly.
    /// </remarks>
    [Setting]
    public string Space = "Linear";

    /// <summary>How it is resampled: <c>Point</c> or <c>Bilinear</c>. ⚠ Not <c>Box</c>.</summary>
    [Setting]
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
///         ⚠ <b>The ramp is an image and not a list of uniforms, which is what keeps this from being
///         a second opinion about what a gradient means.</b>
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
[Node("Source/Gradient", Preview = true, Summary = "A linear, radial, angular or reflected sweep along a ramp.")]
sealed partial class GradientNode : TextureNode {
    /// <summary>Which sweep: <c>Linear</c>, <c>Radial</c>, <c>Angular</c> or <c>Reflected</c>.</summary>
    [Setting]
    public string Kind = "Linear";

    /// <summary>The gradient asset the strip is baked from, or empty for black to white.</summary>
    [Setting(Name = "Ramp", Summary = "The gradient asset a host resolves. Empty is black to white.")]
    public string RampAsset = "";

    /// <summary>The direction of a linear or reflected sweep, in radians, clockwise on screen.</summary>
    [Input]
    public Scalar Angle = 0f;

    /// <summary>Where it is centred along x, in 0..1 of the image.</summary>
    [Input(Name = "Centre X")]
    public Scalar CentreX = 0.5f;

    /// <summary>Where it is centred along y.</summary>
    [Input(Name = "Centre Y")]
    public Scalar CentreY = 0.5f;

    /// <summary>How much of the image the sweep spans. 1 spans it; 0.5 spans the middle half.</summary>
    [Input]
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
