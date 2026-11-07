// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>A stamp on a mesh, against shapes whose answers can be worked out by hand.</summary>
public class PaintMeshSurfaceTests {
    const int Side = 256;
    const uint Red = 0xFF0000FFu;

    /// <summary>64 texels to a unit, on both islands, so a distance on the mesh is one in the atlas.</summary>
    const float Density = 64f;

    /// <summary>Where the shared edge is, on each side of the atlas.</summary>
    const float LeftSeam = 89.6f;

    const float RightSeam = 153.6f;

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 16f, Flow = 1f, Falloff = 0f };

    /// <summary>A sheet two units wide, folded nowhere, cut down its middle into two islands far apart.</summary>
    static PaintMeshSurface Sheet() =>
        new(
            PaintProjection.Over(
                [
                    new(-1f, -1f, 0f), new(0f, -1f, 0f), new(0f, 1f, 0f), new(-1f, 1f, 0f),
                    new(0f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f)
                ],
                [
                    new(0.1f, 0.1f), new(0.35f, 0.1f), new(0.35f, 0.6f), new(0.1f, 0.6f),
                    new(0.6f, 0.1f), new(0.85f, 0.1f), new(0.85f, 0.6f), new(0.6f, 0.6f)
                ],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]
            ),
            Side,
            Side
        );

    /// <summary>A plate with a front and a back a hair apart, which touch nowhere.</summary>
    static PaintMeshSurface Wall() =>
        new(
            PaintProjection.Over(
                [
                    new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(-1f, 1f, 0f),
                    new(-1f, -1f, 0.02f), new(-1f, 1f, 0.02f), new(1f, 1f, 0.02f), new(1f, -1f, 0.02f)
                ],
                [
                    new(0f, 0f), new(0.4f, 0f), new(0.4f, 0.4f), new(0f, 0.4f),
                    new(0.5f, 0f), new(0.5f, 0.4f), new(0.9f, 0.4f), new(0.9f, 0f)
                ],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]
            ),
            Side,
            Side
        );

    /// <summary>Looks at the sheet face on from in front, four units of it filling the picture.</summary>
    static PaintView Facing(float distance, int pixels) =>
        new(
            new(0f, 0f, distance),
            new(1f, 0f, 0f),
            new(0f, 1f, 0f),
            new(0f, 0f, -1f),
            2f * MathF.Atan(2f / distance),
            pixels,
            pixels
        );

    static PaintStroke Stroke(PaintImage image, PaintMeshSurface surface, IPaintPlacement? placement, int gutter = 4) =>
        new(image, surface.Coverage, Hard, Red, gutter, placement: placement);

    static (int First, int Last) Columns(PaintImage image, int row, int from, int to) {
        var (first, last) = (int.MaxValue, int.MinValue);

        for (var x = from; x < to; x++) {
            if (image.At(x, row) != 0u) {
                first = Math.Min(first, x);
                last = Math.Max(last, x);
            }
        }

        return (first, last);
    }

    [Fact]
    public void A_cut_down_the_middle_is_two_charts_and_the_two_triangles_of_a_quad_are_one() {
        var surface = Sheet();

        Assert.Equal(2, surface.Charts);
        Assert.Equal(surface.ChartOf(0), surface.ChartOf(1));
        Assert.NotEqual(surface.ChartOf(0), surface.ChartOf(2));
    }

    /// <summary>
    ///     Ten texels short of the seam, a radius of sixteen leaves six to carry over. Both halves: with no mesh
    ///     the other island holds nothing, which is what a stamp did before.
    /// </summary>
    [Fact]
    public void A_stamp_in_the_atlas_carries_on_over_a_seam_by_what_was_left_of_it() {
        var surface = Sheet();
        PaintImage image = new(Side, Side);
        PaintImage alone = new(Side, Side);

        Stroke(image, surface, surface.AcrossSeams(), 0).MoveTo(new(LeftSeam - 10f, 64f));
        Stroke(alone, surface, null, 0).MoveTo(new(LeftSeam - 10f, 64f));

        var (first, last) = Columns(image, 64, 128, Side);

        Assert.Equal((int)RightSeam, first);
        Assert.InRange(last, (int)(RightSeam + 4f), (int)(RightSeam + 6f));
        Assert.Equal((int.MaxValue, int.MinValue), Columns(alone, 64, 128, Side));
    }

    [Fact]
    public void A_stamp_that_stops_short_of_a_seam_leaves_the_other_island_alone() {
        var surface = Sheet();
        PaintImage image = new(Side, Side);

        Stroke(image, surface, surface.AcrossSeams(), 0).MoveTo(new(LeftSeam - 24f, 64f));

        Assert.Equal((int.MaxValue, int.MinValue), Columns(image, 64, 128, Side));
    }

    /// <summary>The disc on the picture is a disc on a flat sheet, and half of it is on each island.</summary>
    [Fact]
    public void A_stamp_through_a_camera_covers_the_disc_it_makes_on_the_picture_whichever_island_that_is() {
        var surface = Sheet();
        var view = Facing(4f, 512);
        PaintImage image = new(Side, Side);

        // At the middle of the sheet, which is row 89 of both islands.
        Stroke(image, surface, surface.Through(view), 0).MoveTo(new(256f, 256f));

        var perPixel = view.PerPixel(4f);
        var radius = Hard.Radius * perPixel * Density;
        var left = Columns(image, 89, 0, 128);
        var right = Columns(image, 89, 128, Side);

        Assert.InRange(LeftSeam - left.First, radius - 1.5f, radius + 1.5f);
        Assert.InRange(right.Last - RightSeam, radius - 2f, radius + 1.5f);
        Assert.Equal((int)LeftSeam, left.Last);
        Assert.Equal((int)RightSeam, right.First);

        var area = 0;

        for (var at = 0; at < Side * Side; at++) {
            area += image[at] != 0u ? 1 : 0;
        }

        Assert.InRange(area, MathF.PI * radius * radius * 0.9f, MathF.PI * radius * radius * 1.1f);
    }

    /// <summary>
    ///     A stroke on an island laid out on a second UV tile paints nothing in the atlas: the texels it would reach are
    ///     past the edge, and none of them is clamped onto the edge of the first tile. The left island still takes its
    ///     half of the same stamp.
    /// </summary>
    [Fact]
    public void A_stamp_on_an_island_past_the_unit_square_paints_nothing_of_it() {
        PaintMeshSurface surface = new(
            PaintProjection.Over(
                [
                    new(-1f, -1f, 0f), new(0f, -1f, 0f), new(0f, 1f, 0f), new(-1f, 1f, 0f),
                    new(0f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f)
                ],
                [
                    new(0.1f, 0.1f), new(0.35f, 0.1f), new(0.35f, 0.6f), new(0.1f, 0.6f),
                    new(1.6f, 0.1f), new(1.85f, 0.1f), new(1.85f, 0.6f), new(1.6f, 0.6f)
                ],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]
            ),
            Side,
            Side
        );
        PaintImage image = new(Side, Side);

        Stroke(image, surface, surface.Through(Facing(4f, 512)), 0).MoveTo(new(256f, 256f));

        Assert.Equal((int)LeftSeam, Columns(image, 89, 0, 128).Last);
        Assert.Equal((int.MaxValue, int.MinValue), Columns(image, 89, 128, Side));
        for (var y = 0; y < Side; y++) {
            Assert.Equal(0u, image.At(Side - 1, y));
        }
    }

    /// <summary>
    ///     A wall seen nearly side on, with a big triangle: what a stamp looks at must be the texels the disc falls on,
    ///     as a count of work. Scanning a ball as deep as the slope cost this three thousand a stamp and more.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.97f)]
    public void A_stamp_on_a_face_seen_side_on_scans_what_it_covers(float across) {
        var surface = Sheet();
        var (sine, cosine) = MathF.SinCos(1.4f);
        PaintView side = new(
            new(sine * 4f, 0f, cosine * 4f),
            new(cosine, 0f, -sine),
            new(0f, 1f, 0f),
            new(-sine, 0f, -cosine),
            2f * MathF.Atan(0.5f),
            512,
            512
        );
        // The second place is the sheet's far edge, where part of the disc looks past the plane altogether.
        var aim = side.Project(new(across, 0f, 0f));
        PaintImage image = new(Side, Side);
        var stroke = Stroke(image, surface, surface.Through(side), 0);

        stroke.MoveTo(aim);

        var painted = 0;

        for (var at = 0; at < Side * Side; at++) {
            painted += image[at] != 0u ? 1 : 0;
        }

        Assert.True(painted > 0, "The stamp missed, so the count below says nothing.");
        Assert.InRange(stroke.WeightsEvaluated, painted, painted * 2L);
    }

    [Fact]
    public void A_camera_paints_the_side_it_can_see_and_not_the_back_of_a_thin_wall() {
        var surface = Wall();
        PaintImage image = new(Side, Side);

        Stroke(image, surface, surface.Through(new(new(0f, 0f, -4f), new(-1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f), 0.7f, 512, 512)), 0)
            .MoveTo(new(256f, 256f));

        var painted = Columns(image, 51, 0, Side);

        Assert.InRange(painted.Last, 0, (int)(0.4f * Side));
    }

    [Fact]
    public void A_stamp_in_the_atlas_does_not_jump_to_a_surface_it_is_only_near() {
        var surface = Wall();
        PaintImage image = new(Side, Side);

        Stroke(image, surface, surface.AcrossSeams(), 0).MoveTo(new(51f, 51f));

        Assert.InRange(Columns(image, 51, 0, Side).Last, 0, (int)(0.4f * Side));
    }

    [Fact]
    public void A_mirrored_stroke_paints_the_reflection_on_the_other_island() {
        var surface = Sheet();
        var view = Facing(4f, 512);
        PaintImage image = new(Side, Side);

        Stroke(image, surface, surface.Through(view, PaintSymmetry.X), 0).MoveTo(new(150f, 256f));

        var left = Columns(image, 89, 0, 128);
        var right = Columns(image, 89, 128, Side);

        Assert.InRange(left.Last - left.First, 1, 128);
        Assert.InRange(MathF.Abs((LeftSeam - left.Last) - (right.First - RightSeam)), 0f, 1.5f);
    }

    /// <summary>A stylus that eases off narrows a stroke, stamp by stamp along one move.</summary>
    [Fact]
    public void Pressure_takes_off_the_radius_it_was_given_a_share_of() {
        var surface = Sheet();
        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(image, surface.Coverage, Hard with { PressureSize = 1f }, Red, 0);

        stroke.MoveTo(new(40f, 64f), pressure: 1f);
        stroke.MoveTo(new(80f, 64f), pressure: 0f);

        int Tall(int column) {
            var tall = 0;

            for (var y = 0; y < Side; y++) {
                tall += image.At(column, y) != 0u ? 1 : 0;
            }

            return tall;
        }

        Assert.True(Tall(40) > Tall(60), "The stamp under full pressure should be wider than the one at half.");
        Assert.True(Tall(60) > Tall(76), "The stamp near no pressure should be narrower still.");
    }

    /// <summary>
    ///     Whatever a stroke does, nothing lands outside an island but the gutter, the gutter is no wider than
    ///     asked, and the same moves make the same bytes: a journal depends on all three.
    /// </summary>
    [Fact]
    [Trait("Category", "Property")]
    public void A_stroke_stays_on_the_surface_and_a_replay_makes_the_same_bytes() {
        var surface = Sheet();
        var view = Facing(4f, 512);

        Gen.Select(Gen.Float[60f, 450f], Gen.Float[150f, 360f])
            .Array[2, 8]
            .Sample(moves => {
                PaintImage whole = new(Side, Side);
                PaintImage parts = new(Side, Side);
                var first = Stroke(whole, surface, surface.Through(view));
                var second = Stroke(parts, surface, surface.Through(view));

                foreach (var (x, y) in moves) {
                    first.MoveTo(new(x, y));
                    second.MoveTo(new(x, y));
                }

                Assert.Equal(whole.Digest, parts.Digest);

                for (var y = 0; y < Side; y++) {
                    for (var x = 0; x < Side; x++) {
                        if (whole.At(x, y) != 0u) {
                            Assert.True(Near(surface.Coverage, x, y, 4), $"({x}, {y}) is painted and on nothing.");
                        }
                    }
                }
            }, iter: 12);
    }

    static bool Near(PaintCoverage coverage, int x, int y, int reach) {
        for (var dy = -reach; dy <= reach; dy++) {
            for (var dx = Math.Abs(dy) - reach; dx <= reach - Math.Abs(dy); dx++) {
                if (coverage.IsCovered(x + dx, y + dy)) {
                    return true;
                }
            }
        }

        return false;
    }
}
