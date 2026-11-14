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
///         on along those; the channels are still cut into the height as it was. With <see cref="Outlets" /> wired,
///         the grid is filled first as Planchon and Darboux fill it, each hollow up to where it spills toward an
///         outlet (Patina #290), so water crosses a hollow and runs on to where it leaves.
///     </para>
///     <para>
///         ⚠ <b>Water takes one of eight directions</b>, so a long run on an even slope went straight along a row or
///         a diagonal and neighbouring runs lay parallel. <see cref="Meander" /> lets each texel favour a gentler way
///         down by a hash of its place on the counting grid (Patina #290), so runs bend between the directions either
///         side of the fall line and meet. ⚠ So with any meander the node no longer rolls with its input, as a noise
///         does not; it still tiles, because the grid stands on the picture's own texels.
///     </para>
/// </remarks>
[Node(
    "Surface/Erosion Channels",
    Preview = true,
    Summary =
        "Cuts long, branching channels where rain gathered from far uphill would run: rivers, gullies and runnels."
)]
sealed partial class ErosionChannelsNode : TextureNode {
    /// <summary>The longest side of the grid the drainage is counted on.</summary>
    const int Working = 256;

    /// <summary>The most halvings, which takes a 16K picture under <see cref="Working" />.</summary>
    const int MostLevels = 7;

    /// <summary>How many steps fill the hollows toward the outlets: a walk across the widest counting grid.</summary>
    const int FillSteps = Working;

    /// <summary>The widest a channel's half-width goes, in texels of the counting grid.</summary>
    const float Widest = 4f;

    /// <summary>The height map.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Where water leaves the picture, white on black; unwired, water stops in every hollow.</summary>
    [Input(
        Name = "Outlets",
        Summary = "Where water leaves, white on black. Wired, every hollow fills until it spills toward one, so "
            + "water runs on across it; unwired, water stops in every hollow."
    )]
    public Image Outlets;

    /// <summary>How deep the strongest channel cuts.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f, Summary = "How deep the strongest channel cuts, as a height.")]
    public Scalar Depth = 0.15f;

    /// <summary>How wide the strongest channel is, as a share of the widest.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 1f,
        Summary =
            "How wide the strongest channel is: one is a sixty-fourth of the picture, and weaker ones are narrower."
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

    /// <summary>How far water may favour a gentler way down over the steepest.</summary>
    [Input(
        Minimum = 0f,
        Maximum = 1f,
        Summary = "How far water wanders from the steepest way down: nought runs straight along the fall line, more "
            + "lets long runs on an even slope bend and meet."
    )]
    public Scalar Meander = 0.5f;

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
        var meander = Math.Clamp(emitter.Number(nameof(Meander)), 0f, 1f);

        for (var step = 0; step < Math.Clamp(emitter.Integer(nameof(Smoothing)), 0, 128); step++) {
            var filled = emitter.Scratch(TextureFormat.Rgba32Float, coarse);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.FlowAccumulate,
                    Output = filled,
                    Inputs = [from],
                    Parameters = [new("first", 0f), new("smooth", 1f), new("meander", 0f)]
                }
            );
            from = filled;
        }

        from = Filled(emitter, from, coarse, emitter.SizeOf(height), emitter.LevelOf(height));

        for (var step = 0; step < steps; step++) {
            var into = emitter.Scratch(TextureFormat.Rgba32Float, coarse);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.FlowAccumulate,
                    Output = into,
                    Inputs = [from],
                    Parameters = [new("first", step == 0 ? 1f : 0f), new("smooth", 0f), new("meander", meander)]
                }
            );
            from = into;
        }

        emitter.Dispatch(Cut(emitter, height, source, from, 0));
        emitter.Dispatch(Cut(emitter, flow, source, from, 1));
    }

    /// <summary>
    ///     The counting grid's height with its hollows filled to where they spill toward the outlets, or as it was when
    ///     none are wired.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A step lowers a level by one texel's way</b>, so <see cref="FillSteps" /> reaches as far from an outlet as
    ///     a walk across the grid; a hollow a winding path further away keeps part of its fill.
    /// </remarks>
    int Filled(TextureEmitter emitter, int from, int coarse, Int2 size, int level) {
        if (!Binding.IsConnected("Outlets")) {
            return from;
        }

        var outlets = emitter.ReadGrey("Outlets");

        if (outlets < 0) {
            return from;
        }

        var (grid, _) = Halved(emitter, outlets, size, level);

        for (var step = 0; step < FillSteps; step++) {
            var filled = emitter.Scratch(TextureFormat.Rgba32Float, coarse);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.FlowFill,
                    Output = filled,
                    Inputs = [from, grid],
                    DependsOnEveryTexel = true,
                    Parameters = [new("first", step == 0 ? 1f : 0f)]
                }
            );
            from = filled;
        }

        return from;
    }

    /// <summary>The height halved until its longer side is at most <see cref="Working" />, and the level it ends on.</summary>
    static (int Image, int Level) Halved(TextureEmitter emitter, int source, Int2 size, int level) {
        var from = source;
        var at = level;

        for (var step = 1;
             step <= MostLevels && Math.Max(size.X >> (step - 1), size.Y >> (step - 1)) > Working;
             step++) {
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
