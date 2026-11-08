// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>The per-channel median of a square window.</summary>
/// <remarks>
///     Removes specks without moving an edge: a straight step comes through exactly where it was, and an
///     isolated texel unlike its neighbours is gone. The window is at most five taps a side, spread out
///     for a wider radius.
/// </remarks>
[Node("Filters/Median", Preview = true, Summary = "The median of a square window, per channel.")]
sealed partial class MedianNode : TextureNode {
    /// <summary>What to filter.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The window's half-width, in texels at the base resolution.</summary>
    [Input(Minimum = 0f, SoftMaximum = 16f)]
    public Scalar Radius = 1f;

    /// <summary>The filtered image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(emitter, (target, source) => TextureSmoothingFilters.MedianOp(target, source, emitter.Number(nameof(Radius))));
}

/// <summary>A blur that does not cross a colour edge.</summary>
[Node("Filters/Edge-Preserving Blur", Preview = true, Summary = "A gaussian blur that stops at colour edges.")]
sealed partial class EdgePreservingBlurNode : TextureNode {
    /// <summary>What to smooth.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How wide the blur is: the spatial gaussian's sigma, in texels at the base resolution.</summary>
    [Input(Minimum = 0f, SoftMaximum = 16f)]
    public Scalar Sigma = 2f;

    /// <summary>
    ///     How different two colours may be and still be blurred together. Small keeps more edges; large is
    ///     closer to a plain blur.
    /// </summary>
    [Input(Name = "Edge Tolerance", Minimum = 0f, SoftMaximum = 1f)]
    public Scalar EdgeTolerance = 0.1f;

    /// <summary>The smoothed image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(
            emitter,
            (target, source) => TextureSmoothingFilters.BilateralOp(
                target,
                source,
                emitter.Number(nameof(Sigma)),
                emitter.Number("Edge Tolerance")
            )
        );
}

/// <summary>A painterly smooth: regions flatten into strokes and edges stay sharp.</summary>
[Node("Filters/Painterly Smooth", Preview = true, Summary = "Flattens regions into strokes while keeping edges sharp.")]
sealed partial class PainterlySmoothNode : TextureNode {
    /// <summary>What to smooth.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The size of a stroke: each quadrant's side, in texels at the base resolution.</summary>
    [Input(Minimum = 0f, SoftMaximum = 16f)]
    public Scalar Radius = 4f;

    /// <summary>The smoothed image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(emitter, (target, source) => TextureSmoothingFilters.KuwaharaOp(target, source, emitter.Number(nameof(Radius))));
}

/// <summary>A smear along the circle about a centre.</summary>
[DoesNotTile(
    "Its arcs turn about one centre, so they run opposite ways at the two sides of the image, and a "
    + "field of arcs continuous across the wrap would have to vanish at the edge."
)]
[Node("Filters/Spin Blur", Preview = true, Summary = "A smear along circles about a centre.")]
sealed partial class SpinBlurNode : TextureNode {
    /// <summary>What to smear.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The whole arc each texel is smeared over, in radians.</summary>
    [Input(SoftMinimum = 0f, SoftMaximum = 1.5f)]
    public Scalar Angle = 0.2f;

    /// <summary>The centre, as a fraction of the width.</summary>
    [Input(Name = "Centre X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreX = 0.5f;

    /// <summary>The centre, as a fraction of the height.</summary>
    [Input(Name = "Centre Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreY = 0.5f;

    /// <summary>How many reads along each arc.</summary>
    [Input(Minimum = 1f, Maximum = 64f)]
    public Int Samples = 16;

    /// <summary>The smeared image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(
            emitter,
            (target, source) => TextureSmoothingFilters.SpinBlurOp(
                target,
                source,
                emitter.Number(nameof(Angle)),
                emitter.Number("Centre X"),
                emitter.Number("Centre Y"),
                emitter.Integer(nameof(Samples))
            )
        );
}

/// <summary>Staircase edges smoothed along their own direction.</summary>
/// <remarks>
///     A straight edge along an axis is left exactly as it was, because it is constant along itself; a
///     diagonal staircase is averaged into a ramp. Flat regions and soft gradients are untouched.
/// </remarks>
[Node("Filters/Anti-Alias", Preview = true, Summary = "Smooths jagged edges along their own direction.")]
sealed partial class AntiAliasNode : TextureNode {
    /// <summary>What to smooth.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The edge contrast, from nought to one, below which a texel is left alone.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Threshold = 0.1f;

    /// <summary>The smoothed image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(emitter, (target, source) => TextureSmoothingFilters.AntiAliasOp(target, source, emitter.Number(nameof(Threshold))));
}

/// <summary>What the one-input filters share.</summary>
static class Smoothing {
    /// <summary>One op over the node's <c>Input</c>, written to its <c>Out</c>.</summary>
    /// <param name="emitter">The node's emitter.</param>
    /// <param name="op">Builds the op from the target and source images.</param>
    public static void Filter(TextureEmitter emitter, Func<int, int, TextureOp> op) {
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(op);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        emitter.Dispatch(op(target, source));
    }
}
