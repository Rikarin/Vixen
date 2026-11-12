// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Which numbers <c>Analysis/Histogram</c> writes.</summary>
enum TextureHistogramData {
    /// <summary>Each bin's share times 256, so a flat histogram is one everywhere.</summary>
    Density = 0,

    /// <summary>The share at or below each position.</summary>
    Cumulative = 1
}

/// <summary>What the statistics nodes share: the counting and the table.</summary>
static class Statistics {
    /// <summary>Counts an image into a histogram table, and returns the table.</summary>
    /// <param name="emitter">Where to append.</param>
    /// <param name="source">The image counted, by its red channel.</param>
    /// <returns>The table's image.</returns>
    public static int Table(TextureEmitter emitter, int source) {
        var level = emitter.LevelOf(source) + TextureStatistics.TableLevel(emitter.SizeOf(source).Y);
        var counts = emitter.Scratch(TextureFormat.Rgba32Float, level);
        var table = emitter.Scratch(TextureFormat.Rgba32Float, level);
        var size = emitter.SizeOf(counts);

        emitter.Dispatch(TextureStatistics.Histogram(table, counts, source, Math.Max(size.X, size.Y)));

        return table;
    }
}

/// <summary>An image's histogram, as a strip of numbers: texel <c>x</c> is the bin under it.</summary>
/// <remarks>
///     <para>
///         <b>The data, for a graph and for a host.</b> Every row is the same: texel <c>x</c> of a strip
///         <c>w</c> wide holds bin <c>floor(256 · (x + ½) / w)</c> of the input's red channel — its share
///         times 256 under <c>Density</c>, so a flat histogram is one everywhere and the row's mean is
///         always one, or the share at or below <c>(x + ½) / w</c> under <c>Cumulative</c>. At a width of
///         256 each texel is one bin.
///     </para>
///     <para>
///         ⚠ <b>How a 2D view gets a histogram of any node (Patina #184)</b>: compile the node's graph with
///         this appended to the port being viewed, bake it, and read row 0 of the result. Nothing about
///         that needs the editor — the bins are pixels, and a host that already reads a node's preview
///         reads these the same way. <c>Analysis/Histogram Render</c> is the same table drawn as a chart.
///     </para>
///     <para>
///         ⚠ <b>The bin is <c>floor(256 v)</c>, clamped</b>, so a value of exactly one shares the top bin
///         and anything outside 0..1 is counted at the end it is past. Red, as <c>Auto Levels</c> reads
///         it: a grey is a decision <c>Grayscale</c> makes once.
///     </para>
/// </remarks>
[DoesNotTile("A strip's left edge is the darkest bin and its right edge the brightest, which is what a histogram is.")]
[Node(
    "Analysis/Histogram",
    Preview = true,
    Adjustment = false,
    Summary = "The input's histogram, one bin per column: densities or cumulative shares."
)]
sealed partial class HistogramNode : TextureNode {
    /// <summary>Which numbers: <c>Density</c> or <c>Cumulative</c>.</summary>
    [Setting(AcceptedFrom = typeof(TextureHistogramData))]
    public string Mode = "Density";

    /// <summary>What is counted, by its red channel.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The strip.</summary>
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

        var mode = TextureSettings.Enum(emitter, nameof(Mode), TextureHistogramData.Density);
        var table = Statistics.Table(emitter, source);

        emitter.Dispatch(
            TextureStatistics.Plot(
                target,
                table,
                mode == TextureHistogramData.Cumulative ? TextureHistogramPlot.Cumulative : TextureHistogramPlot.Density
            )
        );
    }
}

/// <summary>An image's histogram drawn as a chart: one bar per bin, scaled to the tallest.</summary>
/// <remarks>
///     A bar is one and the sky above it nought, drawn bottom up, each bar as tall as its bin's share over
///     the largest share — or, with <see cref="Cumulative" />, as tall as the share at or below it. The
///     numbers are <c>Analysis/Histogram</c>'s; this is the picture of them a person reads.
/// </remarks>
[DoesNotTile("A chart has a bottom and a top, and a left end that is the darkest bin.")]
[Node(
    "Analysis/Histogram Render",
    Preview = true,
    Adjustment = false,
    Summary = "The input's histogram drawn as bars."
)]
sealed partial class HistogramRenderNode : TextureNode {
    /// <summary>What is counted, by its red channel.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Whether each bar is the share at or below it rather than the share in it.</summary>
    [Input]
    public Bool Cumulative = false;

    /// <summary>The chart.</summary>
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

        var table = Statistics.Table(emitter, source);

        emitter.Dispatch(
            TextureStatistics.Plot(
                target,
                table,
                emitter.Flag(nameof(Cumulative)) ? TextureHistogramPlot.CumulativeBars : TextureHistogramPlot.Bars
            )
        );
    }
}

/// <summary>A grey spread evenly over 0..1 by its own histogram.</summary>
/// <remarks>
///     <para>
///         <b>Every texel becomes the share of the image at or below it</b>, read off the histogram's
///         distribution function and linear inside a bin, so an image whose values are spread evenly already
///         comes back as itself and one bunched in a corner of the range is stretched over all of it.
///     </para>
///     <para>
///         ⚠ <b>Not <c>Utility/Equalize</c></b>, which is a high-pass over a large blur — doc 40 § D2's
///         delighting, named the other way. This one is the image-processing sense of the word: a remap of
///         values, not of detail, and it touches no neighbour.
///     </para>
/// </remarks>
[Node("Colour/Histogram Equalise", Preview = true, Summary = "Spreads a grey evenly over 0..1 by its own histogram.")]
sealed partial class HistogramEqualiseNode : TextureNode {
    /// <summary>The grey to spread, by its red channel.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The spread grey.</summary>
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

        emitter.Dispatch(TextureStatistics.Equalise(target, source, Statistics.Table(emitter, source)));
    }
}

/// <summary>A summed-area table: each texel the mean of the rectangle from the top-left corner to it, times its share.</summary>
/// <remarks>
///     <para>
///         <b>
///             Texel <c>(x, y)</c> holds the sum of every texel in <c>[0, x] × [0, y]</c> divided by the
///             image's texel count
///         </b>
///         , per channel, so the bottom-right texel is the image's mean and the table is
///         the same picture at every bake. The mean of any rectangle is four reads of it — the sum at its
///         far corner, less the two beside it, plus the one diagonally before it, times the texel count over
///         the rectangle's area — which is what a host showing region statistics (Patina #184) wants, and
///         what an O(1) box filter of any size is made of.
///     </para>
///     <para>
///         ⚠ <b>Bake it at 32 bits when the table is read for sums.</b> Every pass is computed in 32-bit
///         float, but the node's output is at the graph's depth, and a 16-bit table keeps about three
///         significant digits — enough to look at, and not enough to subtract two corners of.
///     </para>
/// </remarks>
[DoesNotTile("A sum from one corner is a function of where that corner is: the far corner holds the whole image.")]
[Node(
    "Analysis/Summed Area Table",
    Preview = true,
    Adjustment = false,
    Summary = "Each texel the sum of the rectangle up to it, over the texel count."
)]
sealed partial class SummedAreaTableNode : TextureNode {
    /// <summary>What is summed, per channel.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The table.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        var size = emitter.SizeOf(source);
        var format = emitter.Resolved == TextureChannels.Grey ? TextureFormat.R32Float : TextureFormat.Rgba32Float;
        var scratch = ImmutableArray.CreateBuilder<int>();

        for (var pass = 1; pass < TextureStatistics.SummedAreaPasses(size.X, size.Y); pass++) {
            scratch.Add(emitter.Scratch(format));
        }

        emitter.Dispatch(TextureStatistics.SummedArea(target, source, scratch.ToImmutable(), size.X, size.Y));
    }
}
