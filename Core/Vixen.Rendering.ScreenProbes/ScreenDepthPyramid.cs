// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>The depth's nearest-per-cell pyramid — what a hierarchical screen march skips with.</summary>
/// <remarks>
///     <para>
///         <b>The <i>other</i> reduction.</b> <c>HiZReduce</c> keeps the farthest texel per cell,
///         because occlusion culling asks "could anything behind this cell be visible"; a screen
///         march asks the opposite — "could anything in this cell stop my ray" — and the answer is
///         no whenever the ray passes nearer than the cell's <i>nearest</i> surface. Depth is
///         reversed, so nearest is the <b>maximum</b> device depth, and a cell of pure sky reduces
///         to zero, which stops nothing.
///     </para>
///     <para>
///         <b>Level zero is the depth itself; each level above it halves the way a mip chain
///         halves</b> — flooring, to a minimum of one — because the device pyramid <i>is</i> a mip
///         chain and the two must hold the same texels for the two marches to skip the same cells.
///         Each cell reduces a clamped three-by-three block of the level below, exactly
///         <c>NearestReduce.rvn</c>'s taps: floor-halving leaves a trailing row and column a
///         two-by-two block never reads, and a missed surface would <i>lower</i> a cell's nearest,
///         which is the direction that lets a ray skip through a wall. The extra ring only ever
///         raises it, which merely descends a level sooner — the trade every conservative
///         hierarchy makes, in this pyramid's own direction.
///     </para>
/// </remarks>
public sealed class ScreenDepthPyramid {
    readonly float[][] levels;
    readonly Int2[] sizes;

    /// <summary>Builds an empty pyramid over one viewport.</summary>
    /// <param name="viewport">The depth's size, in texels.</param>
    /// <exception cref="ArgumentOutOfRangeException">An empty viewport.</exception>
    public ScreenDepthPyramid(Int2 viewport) {
        ArgumentOutOfRangeException.ThrowIfLessThan(viewport.X, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(viewport.Y, 1);

        Viewport = viewport;

        var count = 1;
        var size = viewport;

        while (size.X > 1 || size.Y > 1) {
            size = new(Math.Max(1, size.X / 2), Math.Max(1, size.Y / 2));
            count++;
        }

        levels = new float[count][];
        sizes = new Int2[count];
        size = viewport;

        for (var level = 0; level < count; level++) {
            sizes[level] = size;
            levels[level] = new float[size.X * size.Y];
            size = new(Math.Max(1, size.X / 2), Math.Max(1, size.Y / 2));
        }
    }

    /// <summary>The level-zero size.</summary>
    public Int2 Viewport { get; }

    /// <summary>How many levels the pyramid has, down to one-by-one.</summary>
    public int Levels => levels.Length;

    /// <summary>One level's size.</summary>
    public Int2 SizeOf(int level) => sizes[level];

    /// <summary>The nearest surface in one cell — the maximum device depth, zero for pure sky.</summary>
    public float Nearest(int level, Int2 cell) {
        var size = sizes[level];

        return levels[level][(Math.Clamp(cell.Y, 0, size.Y - 1) * size.X) + Math.Clamp(cell.X, 0, size.X - 1)];
    }

    /// <summary>Reduces a depth buffer into every level.</summary>
    /// <param name="depth">The frame's depth, row-major over <see cref="Viewport" />.</param>
    /// <exception cref="ArgumentException">The span is not the viewport's size.</exception>
    public void Build(ReadOnlySpan<float> depth) {
        if (depth.Length != Viewport.X * Viewport.Y) {
            throw new ArgumentException(
                $"the depth holds {depth.Length} texels and the viewport {Viewport.X * Viewport.Y}",
                nameof(depth)
            );
        }

        depth.CopyTo(levels[0]);

        for (var level = 1; level < levels.Length; level++) {
            var coarse = sizes[level];
            var fine = sizes[level - 1];

            for (var y = 0; y < coarse.Y; y++) {
                for (var x = 0; x < coarse.X; x++) {
                    var nearest = 0f;

                    for (var dy = 0; dy < 3; dy++) {
                        for (var dx = 0; dx < 3; dx++) {
                            var fx = Math.Min((x * 2) + dx, fine.X - 1);
                            var fy = Math.Min((y * 2) + dy, fine.Y - 1);

                            nearest = MathF.Max(nearest, levels[level - 1][(fy * fine.X) + fx]);
                        }
                    }

                    levels[level][(y * coarse.X) + x] = nearest;
                }
            }
        }
    }
}
