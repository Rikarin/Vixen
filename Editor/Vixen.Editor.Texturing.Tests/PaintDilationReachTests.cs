// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>How far past an island edge a stroke's dilation may write, and what decides it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/868">#868</a>: the reach compounded
///         across stamps, so it was a function of the brush spacing rather than of the gutter.</b>
///         <c>PaintStroke.Dilate</c> grows outward in <c>gutter</c> four-neighbour rounds from
///         whatever it finds in <c>reached</c> — and <c>reached</c> is a <em>stroke-wide</em> record
///         that already holds the texels stamp N−1 dilated. So stamp N started its rounds from the
///         far edge of stamp N−1's halo and advanced a further <c>gutter</c>, until the footprint's
///         own grown rectangle stopped it at <c>radius + gutter</c> past the seam.
///     </para>
///     <para>
///         <b>The oracle is a distance and not a column number.</b> Every assertion here is against a
///         breadth-first distance from the coverage map, computed in this file, so nothing depends on
///         where the islands were put or on how many stamps the spacing produced — which is what the
///         old seam tests could not express, because each of them lays exactly one stamp and the
///         defect needs two.
///     </para>
///     <para>
///         ⚠ <b>And the reach the gutter is <em>for</em> is asserted in the same test.</b> A stroke
///         that dilated nothing satisfies "no texel is further than the gutter" perfectly, which is
///         the shape of predicate that cannot be false; the second half demands a texel at exactly
///         the gutter's distance, so a dilation that stopped early is red too.
///     </para>
/// </remarks>
public class PaintDilationReachTests {
    const uint Opaque = 0xFF0000FFu;

    const int Side = 64;

    /// <summary>Where the left island ends: it covers 0…19 and the right one starts at 44.</summary>
    /// <remarks>
    ///     A 24-texel channel between them, which is the point — <c>PaintStrokeTests.Islands</c>'s
    ///     gutter is four wide and bounded by coverage on both sides, so an over-reach there has
    ///     nowhere to go and every existing seam test is blind to it.
    /// </remarks>
    const int LeftEdge = 20;

    const int RightEdge = 44;

    /// <summary>A stroke of many stamps along an edge reaches exactly the gutter, and no further.</summary>
    /// <param name="gutter">The reach an author set, in texels.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void A_dilation_reaches_the_gutter_however_many_stamps_crossed_it(int gutter) {
        PaintImage image = new(Side, Side);
        var coverage = Channel();
        var brush = PaintStrokeTests.Hard(8f) with { Spacing = 0.2f };
        PaintStroke stroke = new(image, coverage, brush, Opaque, gutter);

        stroke.MoveTo(new(14f, 6f));
        stroke.MoveTo(new(14f, 58f));

        Assert.True(stroke.StampCount > 10, $"Only {stroke.StampCount} stamps, so nothing compounded.");

        var distance = Distances(coverage);
        var worst = 0;
        var at = (0, 0);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (coverage.IsCovered(x, y) || image.At(x, y) >> 24 == 0u) {
                    continue;
                }

                if (distance[(y * Side) + x] > worst) {
                    worst = distance[(y * Side) + x];
                    at = (x, y);
                }
            }
        }

        Assert.True(
            worst <= gutter,
            $"A texel at {at} is {worst} texels from the nearest covered one and the gutter is {gutter}. "
            + "The reach is compounding across stamps, so how far paint creeps past a seam depends on the "
            + "brush spacing rather than on anything an author set."
        );

        // The other half: the gutter is reached. Without this the assertion above is satisfied by a
        // dilation that never ran, which is how a gutter that does nothing ships.
        Assert.Equal(gutter, worst);
    }

    /// <summary>The reach is the same whatever the brush was, which is the property under the number.</summary>
    /// <remarks>
    ///     ⚠ <b>A differential, and the brush radius rather than the spacing is what moved it.</b>
    ///     Spacing seeds the compounding but <c>Dilate</c> is bounded by the stamp's own footprint
    ///     grown by the gutter — so the halo a compounding stroke actually produced was
    ///     <c>radius + gutter</c> wide, and a fatter brush over the same seam with the same gutter
    ///     left a wider halo. That is the reading a spacing differential would have missed: both
    ///     spacings hit the same cap.
    /// </remarks>
    [Fact]
    public void The_halo_is_the_same_size_whatever_the_brush_was() {
        var thin = Halo(4f);
        var fat = Halo(16f);

        Assert.Equal(thin.Reach, fat.Reach);

        // And the two strokes really were different strokes: the fat one paints far more texels.
        Assert.True(fat.Painted > thin.Painted * 2, $"{fat.Painted} against {thin.Painted}: the same brush.");
    }

    /// <summary>How far one stroke's dilation got, and how many texels it painted.</summary>
    static (int Reach, int Painted) Halo(float radius) {
        PaintImage image = new(Side, Side);
        var coverage = Channel();
        PaintStroke stroke = new(image, coverage, PaintStrokeTests.Hard(radius) with { Spacing = 0.2f }, Opaque, 4);

        // Close enough to the seam that even the thin brush covers the last covered column.
        stroke.MoveTo(new(18f, 6f));
        stroke.MoveTo(new(18f, 58f));

        var distance = Distances(coverage);
        var reach = 0;
        var painted = 0;

        for (var index = 0; index < Side * Side; index++) {
            if (image[index] >> 24 == 0u) {
                continue;
            }

            painted++;

            if (!coverage.IsCovered(index)) {
                reach = Math.Max(reach, distance[index]);
            }
        }

        return (reach, painted);
    }

    /// <summary>Two islands with a twenty-four-texel channel between them.</summary>
    static PaintCoverage Channel() {
        var raster = new bool[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                raster[(y * Side) + x] = x < LeftEdge || x >= RightEdge;
            }
        }

        return PaintCoverage.FromRaster(Side, Side, raster);
    }

    /// <summary>Four-neighbour distance from the nearest covered texel, zero on coverage itself.</summary>
    static int[] Distances(PaintCoverage coverage) {
        var distance = new int[Side * Side];
        Queue<int> frontier = new();

        for (var index = 0; index < distance.Length; index++) {
            if (coverage.IsCovered(index)) {
                frontier.Enqueue(index);
            } else {
                distance[index] = int.MaxValue;
            }
        }

        while (frontier.Count > 0) {
            var index = frontier.Dequeue();
            var x = index % Side;
            var y = index / Side;

            foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) }) {
                if (nx < 0 || ny < 0 || nx >= Side || ny >= Side) {
                    continue;
                }

                var neighbour = (ny * Side) + nx;

                if (distance[neighbour] != int.MaxValue) {
                    continue;
                }

                distance[neighbour] = distance[index] + 1;
                frontier.Enqueue(neighbour);
            }
        }

        return distance;
    }
}
