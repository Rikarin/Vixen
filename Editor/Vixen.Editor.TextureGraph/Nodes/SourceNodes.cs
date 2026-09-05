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
///         ⚠ <b>Its rotation is in radians, where <c>Transform 2D</c>'s is in turns.</b> Both are the
///         kernel's own unit, read off <c>Shape.rvn</c> and <c>Transform2D.rvn</c>; a node that
///         converted one would be the only place in the assembly where a number changed meaning
///         between the graph and the plan —
///         <a href="https://github.com/Rikarin/Vixen/issues/735">#735</a>.
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
