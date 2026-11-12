// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.TextureGraph;

/// <summary>The histogram and summed-area kernels Patina's audit found missing, by name — Patina #172.</summary>
/// <remarks>
///     <para>
///         <b>Every one reads every texel of its input before any of its answers means anything</b>, so each
///         op of these chains says <see cref="TextureOp.DependsOnEveryTexel" />, and the ops whose number or
///         size is chosen from the bake's extent carry <see cref="TextureOp.EmittedForExtent" />, as Auto
///         Levels' ladder does.
///     </para>
///     <para>
///         ⚠ <b>The histogram is a gather and not a scatter</b>: a plan binds images and nothing else, so
///         there is no buffer to add into, and <see cref="HistogramCount" /> reads each source texel once
///         per four bins instead. <c>Shaders/HistogramCount.rvn</c> carries the arithmetic of why that is
///         affordable.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureStatisticsKernels {
    /// <summary>Blocks of the source counted into 256 bins, four to a texel.</summary>
    public const string HistogramCount = "HistogramCount";

    /// <summary>The counts summed into one column of shares, cumulative shares and the largest share.</summary>
    public const string HistogramTable = "HistogramTable";

    /// <summary>The table read along x, as a strip of densities or cumulative shares, or as bars.</summary>
    public const string HistogramPlot = "HistogramPlot";

    /// <summary>Every texel replaced by the share of the image at or below it.</summary>
    public const string HistogramEqualise = "HistogramEqualise";

    /// <summary>One pass of a base-eight prefix sum along one axis.</summary>
    public const string SummedArea = "SummedArea";

    /// <summary>Every one of them.</summary>
    public static IReadOnlyList<string> All { get; } = [
        HistogramCount,
        HistogramEqualise,
        HistogramPlot,
        HistogramTable,
        SummedArea
    ];
}

/// <summary>Which picture <c>HistogramPlot</c> draws of a histogram.</summary>
/// <remarks>The numbers are the contract with the kernel's <c>mode</c>, which compares against them literally.</remarks>
internal enum TextureHistogramPlot {
    /// <summary>Each bin's share times the bin count: a probability density, one everywhere for a flat histogram.</summary>
    Density = 0,

    /// <summary>The share at or below each position: the distribution function.</summary>
    Cumulative = 1,

    /// <summary>A bar per bin, as tall as its share over the largest share.</summary>
    Bars = 2,

    /// <summary>A bar per position, as tall as the distribution function there.</summary>
    CumulativeBars = 3
}

/// <summary>The ops the statistics nodes are, and the sizes their middles are allocated at.</summary>
static class TextureStatistics {
    /// <summary>How many rows of four-bin slots a histogram has.</summary>
    /// <remarks>The contract with the kernels' <c>Slots</c>; 256 bins, four to a texel.</remarks>
    public const int Slots = 64;

    /// <summary>How many terms one summed-area pass adds, which is the base of the scan.</summary>
    /// <remarks>The contract with <c>Shaders/SummedArea.rvn</c>'s <c>Terms</c>.</remarks>
    public const int Terms = 8;

    /// <summary>
    ///     The level offset, from the source's own, of the counts and the table: the one whose height is
    ///     between 256 and 511 rows.
    /// </summary>
    /// <param name="height">The source's height at this bake.</param>
    /// <returns>A level offset to add to the source's; negative for a source shorter than 256 rows.</returns>
    /// <remarks>
    ///     <b>Four bands of sixty-four slots at least, and the 129 rows the table needs</b>, at a size that
    ///     does not grow with the source: a 4K source counts into 256 rows and a 64-texel one into 256 too.
    ///     The width follows the level, and the counts' columns partition the source's whatever it is.
    /// </remarks>
    public static int TableLevel(int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var level = 0;

        while ((height >> (level + 1)) >= 4 * Slots) {
            level++;
        }

        while (Extent(height, level) < 4 * Slots) {
            level--;
        }

        return level;
    }

    /// <summary>The counting and the table, ready to append.</summary>
    /// <param name="table">The table to write, at <see cref="TableLevel" /> and in 32-bit float.</param>
    /// <param name="counts">The counts, at the same level and format as the table.</param>
    /// <param name="source">The image whose red channel is counted.</param>
    /// <param name="countsExtent">The longer side of <paramref name="counts" /> at this bake.</param>
    /// <returns>The two ops, in evaluation order.</returns>
    public static ImmutableArray<TextureOp> Histogram(int table, int counts, int source, int countsExtent) => [
        new() {
            Kernel = TextureStatisticsKernels.HistogramCount,
            Output = counts,
            Inputs = [source],

            // The counts are a quarter of a 4K source's size, or four times a small one's.
            ReadsOtherExtents = true,
            EmittedForExtent = countsExtent,
            DependsOnEveryTexel = true
        },
        new() {
            Kernel = TextureStatisticsKernels.HistogramTable,
            Output = table,
            Inputs = [counts],
            EmittedForExtent = countsExtent,
            DependsOnEveryTexel = true
        }
    ];

    /// <summary>The table drawn as a picture.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="table">The table <see cref="Histogram" /> wrote.</param>
    /// <param name="plot">Which picture.</param>
    /// <returns>The op.</returns>
    public static TextureOp Plot(int output, int table, TextureHistogramPlot plot) =>
        new() {
            Kernel = TextureStatisticsKernels.HistogramPlot,
            Output = output,
            Inputs = [table],
            ReadsOtherExtents = true,
            OtherExtentInputs = [table],
            DependsOnEveryTexel = true,
            Parameters = [new("mode", (float)plot)]
        };

    /// <summary>The source mapped through the table's distribution function.</summary>
    /// <param name="output">The image to write, at the source's size.</param>
    /// <param name="source">The image the table was counted from.</param>
    /// <param name="table">The table <see cref="Histogram" /> wrote.</param>
    /// <returns>The op.</returns>
    public static TextureOp Equalise(int output, int source, int table) =>
        new() {
            Kernel = TextureStatisticsKernels.HistogramEqualise,
            Output = output,
            Inputs = [source, table],
            ReadsOtherExtents = true,
            OtherExtentInputs = [table],
            DependsOnEveryTexel = true
        };

    /// <summary>The strides of a summed-area table's passes along one axis.</summary>
    /// <param name="extent">The axis's length at this bake.</param>
    /// <returns>One, eight, sixty-four… until a pass's reach covers the axis; at least one pass.</returns>
    public static ImmutableArray<int> Strides(int extent) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(extent);

        var strides = ImmutableArray.CreateBuilder<int>();

        for (var stride = 1; strides.Count == 0 || stride < extent; stride *= Terms) {
            strides.Add(stride);
        }

        return strides.ToImmutable();
    }

    /// <summary>A summed-area table: the passes along x, then along y.</summary>
    /// <param name="output">The table, at the source's size.</param>
    /// <param name="source">The image summed.</param>
    /// <param name="scratch">
    ///     One 32-bit image per pass but the last, at the source's size: as many as
    ///     <see cref="Strides" /> gives for both axes, less one.
    /// </param>
    /// <param name="width">The source's width at this bake.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The ops, in evaluation order.</returns>
    /// <exception cref="ArgumentException">The scratch list is the wrong length.</exception>
    public static ImmutableArray<TextureOp> SummedArea(
        int output,
        int source,
        ImmutableArray<int> scratch,
        int width,
        int height
    ) {
        var passes = Strides(width)
            .Select(static stride => (Axis: 0, Stride: stride))
            .Concat(Strides(height).Select(static stride => (Axis: 1, Stride: stride)))
            .ToArray();

        if (scratch.Length != passes.Length - 1) {
            throw new ArgumentException(
                $"A {width}×{height} summed-area table is {passes.Length} passes, and so {passes.Length - 1} scratch "
                + $"images; this names {scratch.Length}.",
                nameof(scratch)
            );
        }

        var ops = ImmutableArray.CreateBuilder<TextureOp>(passes.Length);

        for (var at = 0; at < passes.Length; at++) {
            ops.Add(
                new() {
                    Kernel = TextureStatisticsKernels.SummedArea,
                    Output = at == passes.Length - 1 ? output : scratch[at],
                    Inputs = [at == 0 ? source : scratch[at - 1]],

                    // How many passes there are is a function of the bake's extent.
                    EmittedForExtent = Math.Max(width, height),
                    DependsOnEveryTexel = true,
                    Parameters = [
                        new("axis", passes[at].Axis),
                        new("stride", passes[at].Stride),
                        new("first", at == 0 ? 1f : 0f)
                    ]
                }
            );
        }

        return ops.ToImmutable();
    }

    /// <summary>How many passes a summed-area table of this size is.</summary>
    /// <param name="width">The width at this bake.</param>
    /// <param name="height">The height.</param>
    /// <returns>The pass count; one fewer scratch images are needed.</returns>
    public static int SummedAreaPasses(int width, int height) => Strides(width).Length + Strides(height).Length;

    /// <summary>One axis at a level offset, on the plan's arithmetic — a floored halving, or a doubling.</summary>
    static int Extent(int extent, int level) =>
        level >= 0 ? Math.Max(1, extent >> Math.Min(level, 31)) : extent << Math.Min(-level, 16);
}
