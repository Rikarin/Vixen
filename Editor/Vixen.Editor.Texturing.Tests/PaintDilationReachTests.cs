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

    /// <summary>⚠ A later stamp offering a shorter path lowers the distance, at equal reach.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The mirror of the test above, and the half the <c>reached</c> guard hid —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/896">#896</a>.</b>
    ///         <c>Dilate</c>'s scan skipped a texel whose recorded reach was already at least what
    ///         this round offered, and the distance was written in the commit loop the skip jumped
    ///         over. So a texel reached the long way round by an earlier stamp kept the long
    ///         distance — and for a uniform opaque stroke, where every stamp contributes exactly
    ///         reach 1, the skip is not the exception but the rule.
    ///     </para>
    ///     <para>
    ///         <b>What that costs is under-reach, not over-reach.</b> <c>Neighbour</c> only lets a
    ///         texel at distance <c>r</c> seed round <c>r</c>, so a stale 2 where the truth is 1
    ///         breaks the chain: the texels beyond it are never offered a source in the round that
    ///         would have filled them, and the gutter stops short on exactly the rows where two
    ///         stamps met.
    ///     </para>
    ///     <para>
    ///         <b>The oracle is the algorithm's own question asked from outside.</b> A
    ///         breadth-first distance from the <em>painted</em> covered texels, walked through
    ///         uncovered ones only — which is what a dilation can chain through — and then every
    ///         uncovered texel within the gutter of one must be painted. Nothing in it mentions
    ///         where the stamps were put. The count of such texels is asserted first, because "every
    ///         texel in an empty set is painted" is the shape of predicate that cannot be false.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_second_stamp_that_offers_a_shorter_path_lets_the_gutter_reach_its_full_distance() {
        const int Gutter = 4;

        PaintImage image = new(Side, Side);
        var coverage = Wall();

        // Radius × Spacing is the stamp distance, so 3.5 × 2 puts exactly one further stamp seven
        // texels along — two stamps and not a line of them, which is what the defect needs.
        var brush = PaintStrokeTests.Hard(3.5f) with { Spacing = 2f };
        PaintStroke stroke = new(image, coverage, brush, Opaque, Gutter);

        stroke.MoveTo(new(30f, 12f));
        stroke.MoveTo(new(30f, 19f));

        Assert.Equal(2, stroke.StampCount);

        var distance = FromPainted(coverage, image);
        var owed = 0;
        var missed = new List<(int X, int Y, int Distance)>();

        for (var index = 0; index < Side * Side; index++) {
            if (coverage.IsCovered(index) || distance[index] > Gutter) {
                continue;
            }

            owed++;

            if (image[index] >> 24 == 0u) {
                missed.Add((index % Side, index / Side, distance[index]));
            }
        }

        Assert.True(owed > Gutter, $"Only {owed} texels are within the gutter of painted coverage.");

        Assert.True(
            missed.Count == 0,
            $"{missed.Count} of {owed} texels within {Gutter} of a painted covered texel are unpainted, "
            + $"the nearest at {missed.FirstOrDefault()}. A texel an earlier stamp reached the long way "
            + "round is keeping its stale distance, so it seeds the wrong round and the chain past it "
            + "never runs."
        );
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

        // ⚠ The value, and then the equality. Two reaches of zero are equal, so the differential on
        // its own is satisfied by a dilation that produced nothing — the same vacuity this file's
        // sibling test names and guards against, one test along. `Halo` builds its stroke with a
        // gutter of 4, so 4 is the number both strokes must reach.
        Assert.Equal(4, thin.Reach);
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

    /// <summary>One island filling the left half, so the whole right half is gutter.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <see cref="Channel" />.</b> That map's gutter is bounded on both
    ///     sides, so a dilation that stopped short still meets a covered column and the shortfall
    ///     hides. An unbounded gutter is what lets a missing round be counted.
    /// </remarks>
    static PaintCoverage Wall() {
        var raster = new bool[Side * Side];

        for (var index = 0; index < raster.Length; index++) {
            raster[index] = index % Side < 32;
        }

        return PaintCoverage.FromRaster(Side, Side, raster);
    }

    /// <summary>
    ///     Four-neighbour distance from the nearest <em>painted</em> covered texel, through uncovered
    ///     texels only.
    /// </summary>
    /// <param name="coverage">Which texels an island covers.</param>
    /// <param name="image">What the stroke left.</param>
    /// <returns>The distance per texel, <see cref="int.MaxValue" /> where there is none.</returns>
    /// <remarks>
    ///     <b>Both restrictions are the dilation's own.</b> Round zero reads a covered neighbour's
    ///     <c>reached</c> entry, so a covered texel no stamp painted is not a source; and a covered
    ///     texel is never written, so a path cannot pass through one. A BFS from all coverage would
    ///     therefore owe the dilation texels it has no route to.
    /// </remarks>
    static int[] FromPainted(PaintCoverage coverage, PaintImage image) {
        var distance = new int[Side * Side];
        Queue<int> frontier = new();

        for (var index = 0; index < distance.Length; index++) {
            var painted = image[index] >> 24 != 0u;

            distance[index] = coverage.IsCovered(index) && painted ? 0 : int.MaxValue;

            if (distance[index] == 0) {
                frontier.Enqueue(index);
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

                if (coverage.IsCovered(neighbour) || distance[neighbour] != int.MaxValue) {
                    continue;
                }

                distance[neighbour] = distance[index] + 1;
                frontier.Enqueue(neighbour);
            }
        }

        return distance;
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
