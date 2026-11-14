// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.TextureGraph;

/// <summary>Which number of a texel a host's histogram counts.</summary>
/// <remarks>The numbers are the contract with <c>Shaders/HistogramCount.rvn</c>'s <c>lane</c>.</remarks>
public enum TextureHistogramLane {
    /// <summary>The red channel, which is what every node counts.</summary>
    Red = 0,

    /// <summary>The green channel.</summary>
    Green = 1,

    /// <summary>The blue channel.</summary>
    Blue = 2,

    /// <summary>The alpha channel.</summary>
    Alpha = 3,

    /// <summary>Rec. 709 luma of the stored red, green and blue, unnormalised weights summing to one.</summary>
    Luma = 4
}

/// <summary>A plan with histogram tables on its end, and which table is whose.</summary>
/// <param name="Plan">The plan, whose outputs are the tables.</param>
/// <param name="Tables">One table image per lane asked for, in the order asked.</param>
public sealed record TextureHistogramPlan(TexturePlan Plan, ImmutableArray<int> Tables);

/// <summary>
///     A histogram of any image a plan computes, for a host that wants the numbers rather than a node's picture of
///     them — Patina #172, a 2D view's histogram counted on the device at the size the graph computes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The same ops the statistics nodes are</b>, <c>HistogramCount</c> then <c>HistogramTable</c>, appended
///         after the plan's own ops so a result cache that holds the image runs only these. Each lane is its own
///         count, told by the count's <c>lane</c>, so nothing narrows the source on the way.
///     </para>
///     <para>
///         ⚠ <b>A one-channel image is a grey</b>, which every reader of it shows on all three colour channels with an
///         alpha of one (<c>TexturePixels.ToBitmap</c>). A texture read leaves its green and blue nought, so green, blue
///         and luma count its red instead, which is what they are of the picture a person sees.
///     </para>
///     <para>
///         ⚠ <b>The table is read where it lies</b>: column 0, rows 0 to 63, four bins to a row, each a share of the
///         image's texels (<c>Shaders/HistogramTable.rvn</c>). That is 1 KB a lane, not a strip drawn at some
///         width, so the bins come back exactly whatever the image's aspect is.
///     </para>
/// </remarks>
public static class TextureHistograms {
    /// <summary>How many bins a histogram has: bin <c>b</c> holds <c>floor(256 v) = b</c>, clamped.</summary>
    public const int Bins = 256;

    /// <summary>Appends a histogram of each lane of one image.</summary>
    /// <param name="plan">The plan that computes the image.</param>
    /// <param name="image">The image counted; one an op of the plan writes.</param>
    /// <param name="lanes">What is counted, a table each.</param>
    /// <returns>The longer plan, whose only outputs are the tables.</returns>
    /// <exception cref="ArgumentException">No op of the plan writes the image.</exception>
    public static TextureHistogramPlan Append(TexturePlan plan, int image, IReadOnlyList<TextureHistogramLane> lanes) {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(lanes);

        if (image < 0 || image >= plan.Images.Length || plan.Ops.All(op => op.Output != image)) {
            throw new ArgumentException($"No op of the plan writes image {image}, so it has nothing to count.");
        }

        var level = plan.Images[image].LevelOffset + TextureStatistics.TableLevel(plan.SizeOf(image).Y);
        var images = plan.Images.ToBuilder();
        var tables = ImmutableArray.CreateBuilder<(int Counts, int Table)>(lanes.Count);

        foreach (var _ in lanes) {
            images.Add(new(TextureFormat.Rgba32Float, level));
            images.Add(new(TextureFormat.Rgba32Float, level));
            tables.Add((images.Count - 2, images.Count - 1));
        }

        var sized = Copy(plan, images.ToImmutable(), plan.Ops, []);
        var ops = plan.Ops.ToBuilder();

        var grey = plan.Images[image].Format is TextureFormat.R8 or TextureFormat.R16Float or TextureFormat.R32Float;

        for (var at = 0; at < lanes.Count; at++) {
            var (counts, table) = tables[at];
            var size = sized.SizeOf(counts);
            var lane = grey && lanes[at] is not TextureHistogramLane.Alpha ? TextureHistogramLane.Red : lanes[at];

            ops.AddRange(TextureStatistics.Histogram(table, counts, image, Math.Max(size.X, size.Y), lane));
        }

        var appended = Copy(plan, sized.Images, ops.ToImmutable(), [..tables.Select(static pair => pair.Table)]);
        return new(appended, [..tables.Select(static pair => pair.Table)]);
    }

    /// <summary>Reads one table back as each bin's share of the image's texels.</summary>
    /// <param name="bake">A bake of the plan <see cref="Append" /> made.</param>
    /// <param name="table">One of its tables.</param>
    /// <returns><see cref="Bins" /> shares, summing to one.</returns>
    public static float[] Read(TextureBake bake, int table) {
        ArgumentNullException.ThrowIfNull(bake);

        var bytes = bake.ReadTexels(table, [new(0, 0, 1, TextureStatistics.Slots)])[0];
        var shares = new float[Bins];
        Buffer.BlockCopy(bytes, 0, shares, 0, Bins * sizeof(float));

        return shares;
    }

    static TexturePlan Copy(
        TexturePlan plan,
        ImmutableArray<TextureImage> images,
        ImmutableArray<TextureOp> ops,
        ImmutableArray<int> outputs
    ) =>
        new() {
            BaseWidth = plan.BaseWidth,
            BaseHeight = plan.BaseHeight,
            BakeLevelOffset = plan.BakeLevelOffset,
            Images = images,
            Ops = ops,
            Outputs = outputs,
            Seed = plan.Seed,
            WrapX = plan.WrapX,
            WrapY = plan.WrapY,
            Kernels = plan.Kernels
        };
}
