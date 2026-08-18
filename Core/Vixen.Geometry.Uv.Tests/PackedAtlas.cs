// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The atlas the placements describe, rasterized here so the measurements are of texels.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42's exit criterion 4 says "verified by rasterizing the atlas and measuring",
///         and this is that.</b> Reading the packer's own occupancy back would only prove the packer
///         agrees with itself; what has to hold is that the <i>coordinates</i> a caller gets — offset,
///         scale, quarter turn and tile, put through <see cref="UvPlacement.Apply" /> — land where the
///         margin says they should.
///     </para>
///     <para>
///         ⚠ <b>The overlap test here is a separating-axis test and not the packer's.</b> The packer
///         widens an edge function by a box bound, which over-covers slightly at a corner; this
///         projects the triangle and the texel's square onto five axes and is exact. Using the same
///         code on both sides would test that the code is consistent, which nobody doubted.
///     </para>
/// </remarks>
static class PackedAtlas {
    /// <summary>Rasterizes one tile of a packed atlas into a map of island indices.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="placements">Where they went.</param>
    /// <param name="resolution">The atlas's edge in texels.</param>
    /// <param name="tile">Which UDIM tile to draw.</param>
    /// <param name="overlaps">How many texels two different islands both claimed. Must be zero.</param>
    /// <returns>One entry per texel: an island index, or <c>-1</c>.</returns>
    public static int[] Rasterize(
        IReadOnlyList<UvIsland> islands,
        IReadOnlyList<UvPlacement> placements,
        int resolution,
        Int2 tile,
        out int overlaps
    ) {
        var map = new int[resolution * resolution];

        Array.Fill(map, -1);
        overlaps = 0;

        foreach (var placement in placements) {
            if (placement.Tile != tile) {
                continue;
            }

            var island = islands[placement.Island];
            var coordinates = island.Coordinates;
            var triangles = coordinates.Count / 3;

            for (var triangle = 0; triangle < triangles; triangle++) {
                var a = placement.Apply(island, coordinates[(triangle * 3) + 0]) * resolution;
                var b = placement.Apply(island, coordinates[(triangle * 3) + 1]) * resolution;
                var c = placement.Apply(island, coordinates[(triangle * 3) + 2]) * resolution;

                Fill(map, resolution, a, b, c, placement.Island, ref overlaps);
            }
        }

        return map;
    }

    /// <summary>The smallest number of empty texels between two different islands.</summary>
    /// <param name="map">The map.</param>
    /// <param name="resolution">Its edge.</param>
    /// <param name="probe">How far to look before giving up on a texel.</param>
    /// <returns>The gap, or <see cref="int.MaxValue" /> when nothing came within <paramref name="probe" />.</returns>
    /// <remarks>
    ///     Only boundary texels are probed, which is exact rather than a sample: the closest pair of
    ///     texels belonging to two different islands are both on their island's boundary, or there
    ///     would be a closer pair.
    /// </remarks>
    public static int MinimumGap(int[] map, int resolution, int probe) {
        var closest = int.MaxValue;

        for (var y = 0; y < resolution; y++) {
            for (var x = 0; x < resolution; x++) {
                var island = map[(y * resolution) + x];

                if (island < 0 || !IsBoundary(map, resolution, x, y)) {
                    continue;
                }

                for (var dy = -probe; dy <= probe; dy++) {
                    var ny = y + dy;

                    if (ny < 0 || ny >= resolution) {
                        continue;
                    }

                    for (var dx = -probe; dx <= probe; dx++) {
                        var nx = x + dx;

                        if (nx < 0 || nx >= resolution) {
                            continue;
                        }

                        var other = map[(ny * resolution) + nx];

                        if (other < 0 || other == island) {
                            continue;
                        }

                        var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

                        if (distance - 1 < closest) {
                            closest = distance - 1;
                        }
                    }
                }
            }
        }

        return closest;
    }

    /// <summary>The smallest number of empty texels between any island and the atlas's edge.</summary>
    /// <param name="map">The map.</param>
    /// <param name="resolution">Its edge.</param>
    /// <returns>The gap, or <see cref="int.MaxValue" /> when the atlas is empty.</returns>
    public static int MinimumBorder(int[] map, int resolution) {
        var closest = int.MaxValue;

        for (var y = 0; y < resolution; y++) {
            for (var x = 0; x < resolution; x++) {
                if (map[(y * resolution) + x] < 0) {
                    continue;
                }

                var distance = Math.Min(Math.Min(x, y), Math.Min(resolution - 1 - x, resolution - 1 - y));

                if (distance < closest) {
                    closest = distance;
                }
            }
        }

        return closest;
    }

    /// <summary>How many texels an island covers.</summary>
    /// <param name="map">The map.</param>
    /// <returns>The count.</returns>
    public static int Covered(int[] map) {
        var covered = 0;

        foreach (var island in map) {
            if (island >= 0) {
                covered++;
            }
        }

        return covered;
    }

    static bool IsBoundary(int[] map, int resolution, int x, int y) =>
        x == 0 || y == 0 || x == resolution - 1 || y == resolution - 1
        || map[(y * resolution) + x - 1] != map[(y * resolution) + x]
        || map[(y * resolution) + x + 1] != map[(y * resolution) + x]
        || map[((y - 1) * resolution) + x] != map[(y * resolution) + x]
        || map[((y + 1) * resolution) + x] != map[(y * resolution) + x];

    static void Fill(int[] map, int resolution, Vector2 a, Vector2 b, Vector2 c, int island, ref int overlaps) {
        var minimumX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, resolution - 1);
        var maximumX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, resolution - 1);
        var minimumY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, resolution - 1);
        var maximumY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, resolution - 1);

        for (var y = minimumY; y <= maximumY; y++) {
            for (var x = minimumX; x <= maximumX; x++) {
                if (!Meets(a, b, c, x, y)) {
                    continue;
                }

                ref var slot = ref map[(y * resolution) + x];

                if (slot >= 0 && slot != island) {
                    overlaps++;
                }

                slot = island;
            }
        }
    }

    /// <summary>How far into a texel a triangle has to reach before the texel counts as claimed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Zero is not a usable threshold here, and the reason is that this rasterizer and the
    ///         packer's reach the same geometry through different arithmetic.</b> "The triangle meets
    ///         the square" is a discontinuous predicate: an arbitrarily small perturbation of a
    ///         coordinate flips a texel in or out. <see cref="IslandMask.Rasterize" /> works in the
    ///         island's own frame, at <c>(coordinate − minimum) × texels</c>; this works in the atlas,
    ///         through <see cref="UvPlacement.Apply" /> and a multiply by the resolution. The two are
    ///         equal in exact arithmetic and differ in the last few bits of a <c>float</c>, so at a
    ///         penetration of a few parts in a hundred thousand of a texel they disagree — and a texel
    ///         one of them claims and the other does not is a phantom texel on an island's boundary.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That is not a hypothetical: it is the whole of a nightly failure.</b> CsCheck seed
    ///         <c>1XF0jvSmVt13</c> — 61 islands at 256² with a one-texel margin — packs so that island
    ///         32's own geometry enters texel <c>(107, 151)</c> by <b>1.4 × 10⁻⁵ of a texel</b>. The
    ///         packer's mask does not claim that texel; this rasterizer did. It sits diagonally against
    ///         a texel of island 26, so <see cref="MinimumGap" /> read <c>0</c> where the two islands
    ///         are <b>1.967 texels</b> apart — very nearly twice the margin they were asked for. Over
    ///         the same 61-island atlas that was the <i>only</i> texel of 35,613 on which the two
    ///         rasterizations disagreed at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A 256th of a texel, which is chosen from both ends rather than picked.</b> Below
    ///         it: the disagreement measured above is 1.4 × 10⁻⁵, and the float error in an edge
    ///         function over coordinates as large as a 4096² atlas bounds the two frames' disagreement
    ///         near 10⁻³ — so a 256th (3.9 × 10⁻³) clears the noise at every resolution these tests
    ///         sweep. Above it: a genuine margin violation is a texel the neighbour <i>covers</i>, and
    ///         this admits a corner clip of at most a 256th of a texel across, whose area is under one
    ///         part in a hundred thousand of the texel. A bleed that small does not exist; a bleed
    ///         worth a property test is two hundred and fifty times larger.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a tolerance on the measurement and not a licence for the packer.</b> It moves
    ///         only texels the island grazes — the ones neither rasterizer can be held to — and every
    ///         texel an island actually covers still counts. Both directions matter: dropping a grazed
    ///         texel widens a measured gap, so a tolerance far larger than this one could hide a real
    ///         shortfall, which is why it is stated in texels and kept three orders of magnitude under
    ///         one.
    ///     </para>
    /// </remarks>
    const float Grazing = 1f / 256f;

    /// <summary>Separating axes: the two box axes, then the triangle's three edge normals.</summary>
    /// <remarks>
    ///     ⚠ Every axis is tested against <see cref="Grazing" /> rather than against zero. See its
    ///     remarks: at zero this predicate is a coin toss between two rasterizers rather than a fact
    ///     about an atlas.
    /// </remarks>
    static bool Meets(Vector2 a, Vector2 b, Vector2 c, int x, int y) {
        // ⚠ Strict, so that a triangle whose edge runs exactly along a texel boundary does not claim
        // the texel on the far side of it. An island spanning [0, 16] covers texels 0 to 15; counting
        // texel 16 because the boundary line touches it would put a phantom column on every island and
        // make every measured gap one short.
        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) <= x + Grazing
            || MathF.Min(a.X, MathF.Min(b.X, c.X)) >= x + 1 - Grazing
            || MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) <= y + Grazing
            || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) >= y + 1 - Grazing) {
            return false;
        }

        return Separates(a, b, c, x, y) is false && Separates(b, c, a, x, y) is false
            && Separates(c, a, b, x, y) is false;
    }

    static bool Separates(Vector2 from, Vector2 to, Vector2 opposite, int x, int y) {
        var normal = new Vector2(-(to.Y - from.Y), to.X - from.X);
        var edge = (normal.X * from.X) + (normal.Y * from.Y);
        var third = (normal.X * opposite.X) + (normal.Y * opposite.Y);
        var inward = third >= edge ? 1f : -1f;

        // ⚠ Squared rather than a `Length()`, so the threshold costs a multiply instead of a square
        // root on a test that runs once per texel per triangle. `projected` is a distance scaled by
        // the normal's length, so the comparison is `projected / |normal| > Grazing` with both sides
        // squared — which is only equivalent while `projected` is positive, hence the first test.
        var floor = Grazing * Grazing * normal.LengthSquared();

        // The triangle is on the `inward` side of this edge. The square is separated when no corner of
        // it reaches more than `Grazing` texels onto the triangle's side.
        for (var corner = 0; corner < 4; corner++) {
            var px = x + (corner & 1);
            var py = y + ((corner >> 1) & 1);
            var projected = ((normal.X * px) + (normal.Y * py) - edge) * inward;

            if (projected > 0f && projected * projected > floor) {
                return false;
            }
        }

        return true;
    }
}
