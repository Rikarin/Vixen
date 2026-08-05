// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>
///     A field's coverage and surface range, reduced so a quadtree descent can ask about a whole node.
/// </summary>
/// <remarks>
///     <para>
///         <b>§ D4's coverage predicate, made cheap enough to be a predicate</b>
///         ([35 § D4](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source)
///         — "a coarse mip of the info texture's alpha, tested per node during descent"). Water covers
///         a small fraction of most zones, and descending into open ground to draw nothing is most of
///         the tree; but answering "is any of this 128-metre square wet" by scanning its texels costs
///         more than the descent saves.
///     </para>
///     <para>
///         <b>Three reductions, not one.</b> Coverage reduces by <em>maximum</em>, because the question
///         is whether any of the node is wet. The surface height reduces by minimum <em>and</em>
///         maximum, because a node's bounding box needs both and a box that does not contain the
///         surface is a node culled away with water still in it.
///     </para>
///     <para>
///         ⚠ <b>Conservative in both directions, deliberately.</b> A query rounds its rectangle outward
///         to whole texels of whatever level answers it, so it can say "wet" about a node that is dry
///         and never "dry" about one that is wet. The first costs a patch that draws nothing; the
///         second is a lake with a straight edge through the middle of it.
///     </para>
///     <para>
///         ⚠ <b>The base level is the field's own resolution, which is odd.</b> A field's samples
///         include both edges — 257 over 512 m — so the reduction rounds up rather than halving, and
///         the last column of an odd level is reduced from one texel rather than two. Assuming a power
///         of two here would drop the field's far edge, which is the shoreline furthest from the
///         camera and the one a flythrough shows.
///     </para>
/// </remarks>
public sealed class WaterFieldPyramid {
    readonly List<float[]> coverage = [];
    readonly List<float[]> minimum = [];
    readonly List<float[]> maximum = [];
    readonly List<int> widths = [];

    /// <summary>Builds an empty pyramid over a field's shape.</summary>
    /// <param name="resolution">How many texels the field has along each axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is below two.</exception>
    public WaterFieldPyramid(int resolution) {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 2);

        for (var width = resolution; ; width = (width + 1) / 2) {
            widths.Add(width);
            coverage.Add(new float[width * width]);
            minimum.Add(new float[width * width]);
            maximum.Add(new float[width * width]);

            if (width == 1) {
                break;
            }
        }
    }

    /// <summary>How many levels there are, the base included.</summary>
    public int LevelCount => widths.Count;

    /// <summary>How many texels the base level has along each axis.</summary>
    public int Resolution => widths[0];

    /// <summary>The window the last <see cref="Build" /> covered.</summary>
    public WaterFieldDescription Description { get; private set; }

    /// <summary>Reduces a field into the pyramid.</summary>
    /// <param name="field">The field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field" /> is null.</exception>
    /// <exception cref="ArgumentException">Its resolution is not the one this was built for.</exception>
    public void Build(WaterField field) {
        ArgumentNullException.ThrowIfNull(field);

        if (field.Description.Resolution != Resolution) {
            throw new ArgumentException(
                $"The pyramid reduces {Resolution}² texels and the field has "
                + $"{field.Description.Resolution}². A field that changed resolution is a new field, "
                + "which is Move's rule as well.",
                nameof(field)
            );
        }

        Description = field.Description;

        var baseCoverage = coverage[0];
        var baseMinimum = minimum[0];
        var baseMaximum = maximum[0];

        for (var z = 0; z < Resolution; z++) {
            for (var x = 0; x < Resolution; x++) {
                var index = (z * Resolution) + x;
                var sample = field.At(x, z);

                baseCoverage[index] = sample.Coverage;
                baseMinimum[index] = sample.SurfaceHeight;
                baseMaximum[index] = sample.SurfaceHeight;
            }
        }

        for (var level = 1; level < widths.Count; level++) {
            Reduce(level);
        }
    }

    /// <summary>Whether any part of a world-space rectangle has water in it.</summary>
    /// <param name="low">The rectangle's low corner, on the ground plane.</param>
    /// <param name="high">Its high corner.</param>
    /// <param name="threshold">How much coverage counts as water.</param>
    /// <returns>True if any texel touching the rectangle is at least that covered.</returns>
    /// <remarks>
    ///     ⚠ <b>Rounded outward, so this over-reports rather than under-reports.</b> The descent prunes
    ///     on a false answer, before the children are visited — so a rectangle answered from its centre
    ///     would remove a shoreline running through a corner, and the water would end in a straight
    ///     edge halfway across a tile.
    /// </remarks>
    public bool AnyCoverage(Vector2 low, Vector2 high, float threshold = 0f) {
        var found = 0f;

        Query(low, high, coverage, static (a, b) => MathF.Max(a, b), ref found);

        return found > threshold;
    }

    /// <summary>The lowest and highest the surface gets over a world-space rectangle.</summary>
    /// <param name="low">The rectangle's low corner, on the ground plane.</param>
    /// <param name="high">Its high corner.</param>
    /// <returns>The range. Empty — high below low — when the rectangle misses the window entirely.</returns>
    public (float Minimum, float Maximum) SurfaceRange(Vector2 low, Vector2 high) {
        var least = float.PositiveInfinity;
        var most = float.NegativeInfinity;

        Query(low, high, minimum, static (a, b) => MathF.Min(a, b), ref least);
        Query(low, high, maximum, static (a, b) => MathF.Max(a, b), ref most);

        return (least, most);
    }

    void Reduce(int level) {
        var width = widths[level];
        var finerWidth = widths[level - 1];

        var finerCoverage = coverage[level - 1];
        var finerMinimum = minimum[level - 1];
        var finerMaximum = maximum[level - 1];

        var thisCoverage = coverage[level];
        var thisMinimum = minimum[level];
        var thisMaximum = maximum[level];

        for (var z = 0; z < width; z++) {
            for (var x = 0; x < width; x++) {
                var index = (z * width) + x;

                var most = float.NegativeInfinity;
                var least = float.PositiveInfinity;
                var tallest = float.NegativeInfinity;

                // ⚠ Clamped rather than assumed to exist: an odd level's last row and column reduce
                // from one texel, not two, and reading past them would fold the far edge of the
                // window onto its near one.
                for (var dz = 0; dz < 2; dz++) {
                    for (var dx = 0; dx < 2; dx++) {
                        var fx = Math.Min((x * 2) + dx, finerWidth - 1);
                        var fz = Math.Min((z * 2) + dz, finerWidth - 1);
                        var finer = (fz * finerWidth) + fx;

                        most = MathF.Max(most, finerCoverage[finer]);
                        least = MathF.Min(least, finerMinimum[finer]);
                        tallest = MathF.Max(tallest, finerMaximum[finer]);
                    }
                }

                thisCoverage[index] = most;
                thisMinimum[index] = least;
                thisMaximum[index] = tallest;
            }
        }
    }

    /// <summary>Folds one channel over the texels a world rectangle touches.</summary>
    /// <remarks>
    ///     The level is chosen so the rectangle spans at most two texels of it along each axis, which
    ///     bounds the fold at nine lookups whatever the node's size — that bound is the whole reason
    ///     the pyramid exists rather than a scan.
    /// </remarks>
    void Query(Vector2 low, Vector2 high, List<float[]> channel, Func<float, float, float> fold, ref float into) {
        var step = Description.MetresPerTexel;

        if (!(step > 0f)) {
            return;
        }

        var localLow = (low - Description.Origin) / step;
        var localHigh = (high - Description.Origin) / step;

        var x0 = (int)MathF.Floor(MathF.Min(localLow.X, localHigh.X));
        var z0 = (int)MathF.Floor(MathF.Min(localLow.Y, localHigh.Y));
        var x1 = (int)MathF.Ceiling(MathF.Max(localLow.X, localHigh.X));
        var z1 = (int)MathF.Ceiling(MathF.Max(localLow.Y, localHigh.Y));

        // Entirely outside the window. Left at the caller's identity rather than clamped onto the
        // nearest edge: a node beyond the window has no answer here, and borrowing the shoreline
        // next to it would make the far mesh inherit a lake it is nowhere near.
        if (x1 < 0 || z1 < 0 || x0 > Resolution - 1 || z0 > Resolution - 1) {
            return;
        }

        x0 = Math.Clamp(x0, 0, Resolution - 1);
        z0 = Math.Clamp(z0, 0, Resolution - 1);
        x1 = Math.Clamp(x1, 0, Resolution - 1);
        z1 = Math.Clamp(z1, 0, Resolution - 1);

        var span = Math.Max(x1 - x0, z1 - z0);
        var level = 0;

        while (level < widths.Count - 1 && span > 1) {
            span >>= 1;
            level++;
        }

        var width = widths[level];
        var values = channel[level];

        var lx0 = Math.Clamp(x0 >> level, 0, width - 1);
        var lz0 = Math.Clamp(z0 >> level, 0, width - 1);
        var lx1 = Math.Clamp(x1 >> level, 0, width - 1);
        var lz1 = Math.Clamp(z1 >> level, 0, width - 1);

        for (var z = lz0; z <= lz1; z++) {
            for (var x = lx0; x <= lx1; x++) {
                into = fold(into, values[(z * width) + x]);
            }
        }
    }
}
