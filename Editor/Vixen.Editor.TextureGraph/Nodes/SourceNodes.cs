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
