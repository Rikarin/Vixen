// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Long, branching channels cut where water gathered from far uphill would run — Patina #283.</summary>
/// <remarks>
///     <para>
///         <b>Flow accumulation, not a simulation.</b> Each texel of a smaller copy of the height drains to its
///         steepest lower neighbour, and a stepped gather counts how many texels drain through each one: a ridge
///         counts one, a gully a few dozen, a river thousands. The channels are cut at the picture's own size, along
///         the line each coarse texel drains by, deeper and wider where more drains through.
///         <c>Surface/Hydraulic Erosion</c> wears and silts but moves water a texel a step from where it fell, so it
///         cannot gather a basin; this counts a whole one in a few hundred steps.
///     </para>
///     <para>
///         ⚠ <b>The count runs on a grid at most <see cref="Working" /> texels on a side</b>, halved down from the
///         picture, so the same steps reach as far at any size and a 4K bake costs its last cut and not four
///         thousand steps. That makes the op count a function of the bake, so the halvings carry
///         <see cref="TextureOp.EmittedForExtent" />; and the grid stands on the picture's own texels, so the node
///         tiles when its input does and does not roll by part of a coarse texel.
///     </para>
///     <para>
///         ⚠ <b>A pit is where water stops.</b> A picture that tiles has no edge for a basin to drain over, so no
///         basin is filled to an outlet the way a priority flood fills one. <see cref="Smoothing" /> smooths the counting
///         grid's copy instead, so the pits a noise is full of melt into the broad valleys around them and water runs
///         on along those; the channels are still cut into the height as it was.
///     </para>
///     <para>
///         ⚠ <b>Water takes one of eight directions</b>, so a long run on an even slope goes straight along a row or a
///         diagonal and neighbouring runs lie parallel. A noise under the height breaks them up, as it does in the
///         ground the node is for.
///     </para>
/// </remarks>
[Node(
    "Surface/Erosion Channels",
    Preview = true,
    Summary = "Cuts long, branching channels where rain gathered from far uphill would run: rivers, gullies and runnels."
)]
sealed partial class ErosionChannelsNode : TextureNode {
    /// <summary>The longest side of the grid the drainage is counted on.</summary>
    const int Working = 256;

    /// <summary>The most halvings, which takes a 16K picture under <see cref="Working" />.</summary>
    const int MostLevels = 7;

    /// <summary>The widest a channel's half-width goes, in texels of the counting grid.</summary>
    const float Widest = 4f;

    /// <summary>The height map.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How deep the strongest channel cuts.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f, Summary = "How deep the strongest channel cuts, as a height.")]
    public Scalar Depth = 0.15f;

    /// <summary>How wide the strongest channel is, as a share of the widest.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 1f,
        Summary = "How wide the strongest channel is: one is a sixty-fourth of the picture, and weaker ones are narrower."
    )]
    public Scalar Width = 0.5f;

    /// <summary>How much must drain through before a channel shows.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 1f,
        Summary = "How much water a channel needs before it shows. Higher keeps only the rivers, lower every runnel."
    )]
    public Scalar Threshold = 0.3f;

    /// <summary>How many counting steps run.</summary>
    [Input(
        Minimum = 1f,
        Maximum = 512f,
        Summary = "How far uphill water is gathered from, in steps of a grid at most 256 texels across."
    )]
    public Int Iterations = 192;

    /// <summary>How many steps smooth the ground water runs over.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 128f,
        Summary = "How much the ground is smoothed before water finds its way: more runs longer channels along the "
            + "broad valleys, nought follows every bump."
    )]
    public Int Smoothing = 64;

    /// <summary>The height with its channels cut.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <summary>The channels, white where the strongest runs, as a mask for wet ground, gravel or moss.</summary>
    [Output(Name = "Flow")]
    public Image Flow;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.ReadGrey("Input");
        var height = emitter.Write("Out", TextureChannels.Grey);
        var flow = emitter.Write("Flow", TextureChannels.Grey);

        if (source < 0) {
            return;
        }

        var (from, coarse) = Halved(emitter, source, emitter.SizeOf(height), emitter.LevelOf(height));
        var steps = Math.Clamp(emitter.Integer(nameof(Iterations)), 1, 512);

        for (var step = 0; step < Math.Clamp(emitter.Integer(nameof(Smoothing)), 0, 128); step++) {
            var filled = emitter.Scratch(TextureFormat.Rgba32Float, coarse);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.FlowAccumulate,
                    Output = filled,
                    Inputs = [from],
                    Parameters = [new("first", 0f), new("smooth", 1f)]
                }
            );
            from = filled;
        }

        for (var step = 0; step < steps; step++) {
            var into = emitter.Scratch(TextureFormat.Rgba32Float, coarse);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.FlowAccumulate,
                    Output = into,
                    Inputs = [from],
                    Parameters = [new("first", step == 0 ? 1f : 0f), new("smooth", 0f)]
                }
            );
            from = into;
        }

        emitter.Dispatch(Cut(emitter, height, source, from, 0));
        emitter.Dispatch(Cut(emitter, flow, source, from, 1));
    }

    /// <summary>The height halved until its longer side is at most <see cref="Working" />, and the level it ends on.</summary>
    static (int Image, int Level) Halved(TextureEmitter emitter, int source, Int2 size, int level) {
        var from = source;
        var at = level;

        for (var step = 1; step <= MostLevels && Math.Max(size.X >> (step - 1), size.Y >> (step - 1)) > Working; step++) {
            var smaller = emitter.Scratch(TextureFormat.Rgba32Float, level + step);
            var extent = emitter.SizeOf(smaller);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TextureColourKernels.Resample,
                    Output = smaller,
                    Inputs = [from],
                    ReadsOtherExtents = true,
                    EmittedForExtent = Math.Max(extent.X, extent.Y),
                    Parameters = [new("filter", (float)TextureFilter.Box)]
                }
            );
            from = smaller;
            at = level + step;
        }

        return (from, at);
    }

    static TextureOp Cut(TextureEmitter emitter, int into, int height, int counted, int read) =>
        new() {
            Kernel = TexturePhotoKernels.FlowCarve,
            Output = into,
            Inputs = [height, counted],
            ReadsOtherExtents = true,
            OtherExtentInputs = [counted],
            DependsOnEveryTexel = true,
            Parameters = [
                new("depth", emitter.Number(nameof(Depth))),
                new("reach", Math.Clamp(emitter.Number(nameof(Width)), 0f, 1f) * Widest),
                new("threshold", Math.Clamp(emitter.Number(nameof(Threshold)), 0f, 1f)),
                new("read", read)
            ]
        };
}
