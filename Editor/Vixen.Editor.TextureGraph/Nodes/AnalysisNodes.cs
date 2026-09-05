// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

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
    [Input]
    public Scalar Width = 1f;

    /// <summary>Magnitudes at or below this are black.</summary>
    [Input]
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

/// <summary>The islands of a mask, as one of five pictures.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Iterations</c> is a budget rather than a quality dial, and nothing in this
///         compilation can know whether it was enough.</b> Bounds travel one texel per dispatch along
///         an island, so a disc of diameter <c>d</c> settles in about <c>d</c> iterations and a
///         spiral of the same area takes its whole arc length — the settling time is a property of
///         the <em>shape</em> of the mask, which is a picture nobody has drawn yet at compile time. A
///         budget too small does not fail: it produces islands whose boxes are the parts of them that
///         had time to meet, which reads as a flood fill of a different mask.
///     </para>
///     <para>
///         ⚠ <b>Doc 48 § 4.5 answers that with a truncation report and this node cannot emit
///         one.</b> The report is a <c>FloodResidual</c> over the last two records, a
///         <c>MinMaxReduce</c> chain down to one texel, and a read of it — and a reduction needs
///         images at descending level offsets, which is the one thing <c>TextureEmitter.Scratch</c>
///         does not offer. Every image a node can allocate is at the plan's base level —
///         <a href="https://github.com/Rikarin/Vixen/issues/733">#733</a>. Until that changes, the
///         budget is the artist's and this paragraph is the warning.
///     </para>
///     <para>
///         <b>The output's channels follow the picture and not the mask</b>, which is
///         <c>Noise</c>'s <c>Worley</c> case again: <c>Random</c> is one value splatted and every
///         other kind packs coordinates into separate lanes, so calling them all grey would throw
///         most of the answer away at the first thing that read it.
///     </para>
///     <para>
///         ⚠ <b>Two islands that settle to the same bounding box are one island to this node.</b>
///         That is the price of settling labels and bounds in one chain, and it is real — an L-shape
///         and a small square tucked into its corner can share a box.
///     </para>
/// </remarks>
[Node("Analysis/Flood Fill", Preview = true, Summary = "The islands of a mask, as an id, a random value, a local UV, a box or a size.")]
sealed partial class FloodFillNode : TextureNode {
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

    /// <summary>Which picture: <c>Id</c>, <c>Random</c>, <c>LocalUv</c>, <c>BoundingBox</c> or <c>Size</c>.</summary>
    [Setting]
    public string Kind = "Random";

    /// <summary>The mask whose islands are found. A single channel.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>How many propagation iterations to run. ⚠ A budget: see the node's remarks.</summary>
    [Input]
    public Int Iterations = 32;

    /// <summary>What counts as inside the mask.</summary>
    [Input]
    public Scalar Threshold = 0.5f;

    /// <summary>Whether diagonally touching texels are one island.</summary>
    [Input]
    public Bool Diagonal = false;

    /// <summary>The picture.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var kind = TextureSettings.Enum(emitter, nameof(Kind), TextureFloodOutput.Random);
        var mask = emitter.ReadGrey("Mask");

        // Random is one value in three lanes; every other kind packs coordinates into red, green,
        // blue and alpha separately.
        var target = emitter.Write(
            "Out",
            kind == TextureFloodOutput.Random ? TextureChannels.Grey : TextureChannels.Colour
        );

        if (mask < 0) {
            return;
        }

        var iterations = emitter.Integer(nameof(Iterations));

        if (iterations is < 1 or > MaxIterations) {
            emitter.Report(
                "TG0012",
                $"'{nameof(Iterations)}' is {iterations}, and this node runs between 1 and {MaxIterations}. Each one "
                + "is a dispatch over the whole image and an image in the plan, and bounds travel one texel per "
                + "iteration — so the number an island needs is about its longest dimension in texels.",
                nameof(Iterations)
            );

            return;
        }

        var scratch = ImmutableArray.CreateBuilder<int>(iterations);

        for (var pass = 0; pass < iterations; pass++) {
            // ⚠ Rgba16Float and not the node's own channels: a bounds record is a pair of texel
            // coordinates, not a picture, and a grey scratch would hold one lane of one corner.
            scratch.Add(emitter.Scratch(TextureFormat.Rgba16Float));
        }

        try {
            emitter.Dispatch(
                TextureAnalysis.FloodFill(
                    target,
                    mask,
                    scratch.ToImmutable(),
                    emitter.Width,
                    emitter.Height,
                    kind,
                    emitter.Flag(nameof(Diagonal)),
                    emitter.Number(nameof(Threshold))
                )
            );
        } catch (ArgumentException refusal) {
            // ⚠ Caught rather than pre-checked, exactly as `DistanceNode` catches its own: the
            // builder's message names both numbers — a half-float's record is exact on the integers
            // only to 2048, and this bake's extent — and repeating that comparison here would be two
            // ceilings that have to agree. What the node adds is a diagnostic an author can select,
            // rather than an exception three frames away in a background bake.
            emitter.Report("TG0011", refusal.Message, "Mask");
        }
    }
}
