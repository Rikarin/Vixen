// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>A Sobel magnitude with a tap spacing and a soft threshold.</summary>
/// <remarks>
///     ⚠ <b>Its input is measured rather than composited</b>, for <c>Distance</c>'s reason: the
///     kernel reads one channel, and there is no luminance a colour and a mask agree on. A graph that
///     wants the edges of a colour image puts a <c>Grayscale</c> in front and says which luminance it
///     means. ⚠ <c>Threshold</c> at 1 is black everywhere, which is a node that looks like it never
///     ran.
/// </remarks>
[Node("Analysis/Edge Detect", Preview = true, Summary = "A Sobel magnitude, with a tap spacing and a soft threshold.")]
sealed partial class EdgeDetectNode : TextureNode {
    /// <summary>The picture to find edges in. A single channel.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The operator's tap spacing, in texels at the base resolution.</summary>
    [Input(Minimum = 1f, Maximum = 256f)]
    public Scalar Width = 1f;

    /// <summary>Magnitudes at or below this are black.</summary>
    [Input(SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar Threshold = 0f;

    /// <summary>The edges.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.ReadGrey("Input");
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            TextureAnalysis.EdgeDetect(target, source, emitter.Number(nameof(Width)), emitter.Number(nameof(Threshold)))
        );
    }
}

/// <summary>The islands of a mask, as one of seven pictures.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Iterations</c> is a budget rather than a quality dial, and nothing in this
///         compilation can know whether it was enough.</b> One iteration is one sweep along a line of
///         the image, which crosses a whole run at any width, so a rectangle settles in two and an
///         island takes about as many as a walk across it has corners. That is a property of the
///         <em>shape</em>: it does not change with the resolution, and it cannot be known before the
///         mask is drawn.
///     </para>
///     <para>
///         <b>So a budget that ran out says so.</b> The node ends in a <c>FloodResidual</c> and a
///         <c>MinMaxReduce</c> ladder down to one texel, and a <see cref="TextureGraphCheck" /> asks
///         the host to read it. ⚠ What it reports is the node, not which islands: truncated, they are
///         pieces of themselves, and a piece looks like a smaller island.
///     </para>
///     <para>
///         <b>The output's channels follow the picture and not the mask</b>, which is
///         <c>Noise</c>'s <c>Worley</c> case again: <c>Random</c> and <c>Gradient</c> are one value
///         splatted, <c>Colour</c> is three, and every other kind packs coordinates into separate
///         lanes, so calling them all grey would throw most of the answer away at the first thing
///         that read it.
///     </para>
///     <para>
///         <b><c>Colour</c> and <c>Gradient</c> are the two an artist asks for by name</b> (Patina
///         #174): a random colour per island, and a ramp across each island's box along
///         <c>Angle</c>, turned per island by up to <c>Angle Variation</c>. Neither needs a second
///         flood — every kind is one read of the same settled record.
///     </para>
///     <para>
///         ⚠ <b>Two islands that settle to the same bounding box are one island to this node.</b>
///         That is the price of settling labels and bounds in one chain, and it is real — an L-shape
///         and a small square tucked into its corner can share a box.
///     </para>
/// </remarks>
[Node(
    "Analysis/Flood Fill",
    Preview = true,
    Summary = "The islands of a mask, as an id, a random value or colour, a local UV, a box, a size or a ramp."
)]
sealed partial class FloodFillNode : TextureNode {
    /// <summary>The fewest iterations one node may ask for: one round of every axis.</summary>
    /// <remarks>
    ///     Fewer and there is no round to compare with the one before it, so the check that says
    ///     whether the budget was enough would have nothing to read.
    /// </remarks>
    public const int MinIterations = TextureAnalysis.FloodAxes;

    /// <summary>The most iterations one node may ask for.</summary>
    /// <remarks>
    ///     ⚠ <b>A refusal rather than a clamp, for <c>TexturePlacement</c>'s reason.</b> Every
    ///     iteration is a dispatch over the whole image and a scratch image in the plan, so a budget
    ///     typed with an extra digit is a bake that appears to hang; a clamp would instead be a
    ///     truncated flood drawn without a word, which is the failure this node's whole design is
    ///     about. The number is this library's, not a kernel's — <c>FloodBounds.rvn</c> has no
    ///     ceiling, because one iteration knows nothing about how many there are.
    /// </remarks>
    public const int MaxIterations = 256;

    /// <summary>The budget a node starts with.</summary>
    /// <remarks>
    ///     Measured on noise cut at a threshold, the mask this node most often sees: at scale 64 and
    ///     0.5 it takes between 32 and 64 sweeps, the same at 1024, 2048 and 4096. Only noise past
    ///     scale 100 cut right at its own middle wanted more, and that reports it.
    /// </remarks>
    public const int DefaultIterations = 64;

    /// <summary>
    ///     Which picture: <c>Id</c>, <c>Random</c>, <c>LocalUv</c>, <c>BoundingBox</c>, <c>Size</c>,
    ///     <c>Colour</c> or <c>Gradient</c>.
    /// </summary>
    [Setting(AcceptedFrom = typeof(TextureFloodOutput))]
    public string Kind = "Random";

    /// <summary>The mask whose islands are found. A single channel.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>How many sweeps to run. ⚠ A budget: see the node's remarks.</summary>
    [Input(Minimum = MinIterations, Maximum = MaxIterations)]
    public Int Iterations = DefaultIterations;

    /// <summary>What counts as inside the mask.</summary>
    [Input(SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar Threshold = 0.5f;

    /// <summary>Whether diagonally touching texels are one island.</summary>
    [Input]
    public Bool Diagonal = false;

    /// <summary><c>Gradient</c>'s direction in radians: 0 rises to the right, π/2 rises downwards.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Angle = 0f;

    /// <summary>How far each island's ramp may turn from <c>Angle</c>, in radians, half either way. At 2π it is free.</summary>
    [Input(Name = "Angle Variation", Minimum = 0f, SoftMaximum = MathF.Tau)]
    public Scalar AngleVariation = 0f;

    /// <summary>The picture.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var kind = TextureSettings.Enum(emitter, nameof(Kind), TextureFloodOutput.Random);
        var mask = emitter.ReadGrey("Mask");

        // Random and Gradient are one value in three lanes; every other kind packs coordinates or a
        // colour into red, green, blue and alpha separately.
        var target = emitter.Write(
            "Out",
            kind is TextureFloodOutput.Random or TextureFloodOutput.Gradient ? TextureChannels.Grey : TextureChannels.Colour
        );

        if (mask < 0) {
            return;
        }

        var iterations = emitter.Integer(nameof(Iterations));

        if (iterations is < MinIterations or > MaxIterations) {
            // ⚠ `SettingNotAccepted` and not the id this line used to carry. It was TG0012, which
            // `TextureGraphExpressions` already reported a refused expression under — two unrelated
            // sentences, both errors, both reachable from one graph, so a host filtering on the id
            // hid the wrong half. #804. The sentence this refusal wants was already written down as
            // TG0010: a setting holding a value the node does not accept, with a range here where
            // the other sites have a set.
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                $"'{nameof(Iterations)}' is {iterations}, and this node runs between {MinIterations} and "
                + $"{MaxIterations}. Each one is a dispatch over the whole image and an image in the plan, and an "
                + "island needs about as many as a walk across it has corners.",
                nameof(Iterations)
            );

            return;
        }

        var scratch = ImmutableArray.CreateBuilder<int>(iterations);

        for (var pass = 0; pass < iterations; pass++) {
            // ⚠ Four lanes and not the node's own channels: a bounds record is a pair of texel
            // coordinates, not a picture. And 32 bits whatever the graph's depth, because a half
            // stops being exact on the integers at 2048.
            scratch.Add(emitter.Scratch(TextureFormat.Rgba32Float));
        }

        var records = scratch.ToImmutable();
        var (width, height) = (emitter.Width, emitter.Height);

        emitter.Dispatch(
            TextureAnalysis.FloodFill(
                target,
                mask,
                records,
                width,
                height,
                kind,
                emitter.Flag(nameof(Diagonal)),
                emitter.Number(nameof(Threshold)),
                emitter.Number(nameof(Angle)),
                emitter.Number("Angle Variation")
            )
        );

        var residual = emitter.Scratch(emitter.FormatOf(TextureChannels.Colour));
        var levels = TextureAdjust.ReductionLevels(width, height);
        var ladder = ImmutableArray.CreateBuilder<int>(levels.Length);

        foreach (var rung in levels) {
            ladder.Add(emitter.Scratch(emitter.FormatOf(TextureChannels.Colour), emitter.Level + rung));
        }

        emitter.Dispatch(TextureAnalysis.Settled(residual, records, ladder.ToImmutable(), width, height));
        emitter.Check(
            ladder[^1],
            TextureDiagnostics.FloodDidNotSettle,
            $"This Flood Fill had not settled after {iterations} iterations, so some islands are still in pieces. "
            + $"Raise {nameof(Iterations)}.",
            nameof(Iterations)
        );
    }
}
