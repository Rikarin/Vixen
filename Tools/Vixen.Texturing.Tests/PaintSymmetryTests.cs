// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     Symmetry copies on a plate that is not centred on its own origin, whose atlas is the plate itself: a place on
///     the mesh and a place in the atlas are one linear map apart, so where each copy lands is worked out by hand.
/// </summary>
public class PaintSymmetryTests {
    const int Side = 256;
    const uint Red = 0xFF0000FFu;

    /// <summary>The plate runs from two to six on x and y, so its centre is (4, 4) and its origin is off it.</summary>
    const float Low = 2f;

    const float Size = 4f;

    static readonly Vector3 Centre = new(4f, 4f, 0f);

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 8f, Flow = 1f, Falloff = 0f };

    /// <summary>One island, its coordinate the plate's position scaled into the unit square.</summary>
    static PaintMeshSurface Plate() =>
        new(
            PaintProjection.Over(
                [
                    new(Low, Low, 0f), new(Low + Size, Low, 0f), new(Low + Size, Low + Size, 0f),
                    new(Low, Low + Size, 0f)
                ],
                [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)],
                [0, 1, 2, 0, 2, 3]
            ),
            Side,
            Side
        );

    /// <summary>Straight down at the plate's centre, from far enough that the whole plate is in the picture.</summary>
    static PaintView Above() =>
        new(Centre + new Vector3(0f, 0f, 8f), Vector3.UnitX, Vector3.UnitY, -Vector3.UnitZ, 0.6f, 512, 512);

    /// <summary>Where a point of the plate is in the atlas.</summary>
    static Vector2 Texel(float x, float y) => new((x - Low) / Size * Side, (y - Low) / Size * Side);

    /// <summary>The middle of what is painted within a square round a place, or null for nothing there.</summary>
    static Vector2? Blob(PaintImage image, Vector2 around, int half = 24) {
        var (sum, count) = (Vector2.Zero, 0);

        for (var y = (int)around.Y - half; y < (int)around.Y + half; y++) {
            for (var x = (int)around.X - half; x < (int)around.X + half; x++) {
                if (x is >= 0 and < Side && y is >= 0 and < Side && image.At(x, y) != 0u) {
                    sum += new Vector2(x + 0.5f, y + 0.5f);
                    count++;
                }
            }
        }

        return count == 0 ? null : sum / count;
    }

    static int Painted(PaintImage image) {
        var painted = 0;

        for (var at = 0; at < Side * Side; at++) {
            painted += image[at] != 0u ? 1 : 0;
        }

        return painted;
    }

    static void Near(Vector2 expected, Vector2? found, float tolerance = 1.5f) {
        Assert.True(found.HasValue, $"Nothing was painted near {expected}.");
        Assert.InRange(Vector2.Distance(expected, found!.Value), 0f, tolerance);
    }

    /// <summary>
    ///     Four copies round the plate's centre land a quarter turn apart. Through the origin they land off the plate
    ///     altogether, which is what a model off its origin got before the axis could move.
    /// </summary>
    [Fact]
    public void A_radial_symmetry_round_the_model_centre_paints_each_copy_a_turn_apart() {
        var surface = Plate();
        var view = Above();
        PaintImage round = new(Side, Side);
        PaintImage origin = new(Side, Side);
        var aim = view.Project(new(5f, 4f, 0f));

        new PaintStroke(
            round,
            surface.Coverage,
            Hard,
            Red,
            0,
            placement: surface.Through(view, PaintSymmetryGroup.Radial(Vector3.UnitZ, Centre, 4))
        ).MoveTo(aim);
        new PaintStroke(
            origin,
            surface.Coverage,
            Hard,
            Red,
            0,
            placement: surface.Through(view, PaintSymmetryGroup.Radial(Vector3.UnitZ, Vector3.Zero, 4))
        ).MoveTo(aim);

        Near(Texel(5f, 4f), Blob(round, Texel(5f, 4f)));
        Near(Texel(4f, 5f), Blob(round, Texel(4f, 5f)));
        Near(Texel(3f, 4f), Blob(round, Texel(3f, 4f)));
        Near(Texel(4f, 3f), Blob(round, Texel(4f, 3f)));
        Assert.InRange(Painted(round), Painted(origin) * 3.8f, Painted(origin) * 4.2f);

        Near(Texel(5f, 4f), Blob(origin, Texel(5f, 4f)));
        Assert.Null(Blob(origin, Texel(4f, 5f)));
        Assert.Null(Blob(origin, Texel(3f, 4f)));
    }

    [Fact]
    public void A_mirror_through_the_model_centre_paints_the_reflection_and_through_the_origin_paints_none() {
        var surface = Plate();
        var view = Above();
        PaintImage centred = new(Side, Side);
        PaintImage origin = new(Side, Side);
        var aim = view.Project(new(5f, 4.5f, 0f));

        new PaintStroke(
            centred,
            surface.Coverage,
            Hard,
            Red,
            0,
            placement: surface.Through(view, PaintSymmetryGroup.Mirror(PaintSymmetry.Across(Vector3.UnitX, Centre)))
        ).MoveTo(aim);
        new PaintStroke(
            origin,
            surface.Coverage,
            Hard,
            Red,
            0,
            placement: surface.Through(view, PaintSymmetryGroup.Mirror(PaintSymmetry.X))
        ).MoveTo(aim);

        Near(Texel(3f, 4.5f), Blob(centred, Texel(3f, 4.5f)));
        Assert.Null(Blob(origin, Texel(3f, 4.5f)));
    }

    /// <summary>
    ///     A stroke in the atlas copies itself on the mesh too: its place on the surface goes round the centre, and the
    ///     copy is painted wherever on the atlas that place is.
    /// </summary>
    [Fact]
    public void A_stroke_in_the_atlas_paints_its_copies_where_the_mesh_has_them() {
        var surface = Plate();
        PaintImage image = new(Side, Side);
        PaintImage alone = new(Side, Side);

        new PaintStroke(
            image,
            surface.Coverage,
            Hard,
            Red,
            0,
            placement: surface.AcrossSeams(PaintSymmetryGroup.Radial(Vector3.UnitZ, Centre, 3))
        ).MoveTo(Texel(5f, 4f));
        new PaintStroke(alone, surface.Coverage, Hard, Red, 0, placement: surface.AcrossSeams()).MoveTo(Texel(5f, 4f));

        var (sin, cos) = MathF.SinCos(MathF.Tau / 3f);

        Near(Texel(5f, 4f), Blob(image, Texel(5f, 4f)));
        Near(Texel(4f + cos, 4f + sin), Blob(image, Texel(4f + cos, 4f + sin)));
        Near(Texel(4f + cos, 4f - sin), Blob(image, Texel(4f + cos, 4f - sin)));
        Assert.InRange(Painted(image), Painted(alone) * 2.8f, Painted(alone) * 3.2f);
    }

    /// <summary>A copy whose place is off the mesh lands nowhere, rather than on whatever surface is nearest.</summary>
    [Fact]
    public void A_copy_of_an_atlas_stroke_with_no_surface_under_it_paints_nothing() {
        var surface = Plate();
        PaintImage image = new(Side, Side);
        PaintImage alone = new(Side, Side);

        new PaintStroke(
            image,
            surface.Coverage,
            Hard,
            Red,
            0,
            placement: surface.AcrossSeams(PaintSymmetryGroup.Mirror(PaintSymmetry.X))
        ).MoveTo(Texel(5f, 4f));
        new PaintStroke(alone, surface.Coverage, Hard, Red, 0, placement: surface.AcrossSeams()).MoveTo(Texel(5f, 4f));

        Assert.Equal(alone.Digest, image.Digest);
    }

    /// <summary>
    ///     A stamp four times longer than wide reaches twice its radius along its long axis, on the picture as in the
    ///     atlas. Clipped to a square a radius across, it came out no longer than a disc.
    /// </summary>
    [Fact]
    public void A_stretched_stamp_through_a_camera_is_as_long_as_its_stretch_says() {
        var surface = Plate();
        var view = Above();
        PaintImage image = new(Side, Side);
        var brush = Hard with { Radius = 10f, Aspect = 4f, AspectAngle = 0f };

        new PaintStroke(image, surface.Coverage, brush, Red, 0, placement: surface.Through(view))
            .MoveTo(view.Project(Centre));

        var texelsPerPixel = view.PerPixel(8f) * Side / Size;
        var (left, right, top, bottom) = (Side, 0, Side, 0);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (image.At(x, y) != 0u) {
                    (left, right, top, bottom) = (Math.Min(left, x), Math.Max(right, x), Math.Min(top, y),
                        Math.Max(bottom, y));
                }
            }
        }

        Assert.InRange(right - left + 1, 4f * 10f * texelsPerPixel - 3f, 4f * 10f * texelsPerPixel + 3f);
        Assert.InRange(bottom - top + 1, 10f * texelsPerPixel - 3f, 10f * texelsPerPixel + 3f);
    }

    [Fact]
    public void A_radius_in_texels_or_in_the_model_is_measured_on_the_picture_where_the_stroke_starts() {
        var surface = Plate();
        var view = Above();
        var perPixel = view.PerPixel(8f);
        var texelsPerUnit = Side / Size;

        Assert.Equal(7f, surface.PictureRadius(view, new(1f, 2f), 7f, PaintSizeSpace.Picture));
        Assert.Equal(
            32f / texelsPerUnit / perPixel,
            surface.PictureRadius(view, view.Project(Centre), 32f, PaintSizeSpace.Texels),
            3
        );
        Assert.Equal(0.5f / perPixel, surface.PictureRadius(view, view.Project(Centre), 0.5f, PaintSizeSpace.Model), 3);

        // Off the plate: the centre's depth and the mean density, which for one flat island are the same answers.
        Assert.Equal(0.5f / perPixel, surface.PictureRadius(view, new(1f, 1f), 0.5f, PaintSizeSpace.Model), 3);
        Assert.Equal(
            32f / texelsPerUnit / perPixel,
            surface.PictureRadius(view, new(1f, 1f), 32f, PaintSizeSpace.Texels),
            3
        );

        Assert.Equal(0.5f * texelsPerUnit, surface.AtlasRadius(new(128f, 128f), 0.5f, PaintSizeSpace.Model), 3);
        Assert.Equal(12f, surface.AtlasRadius(new(128f, 128f), 12f, PaintSizeSpace.Texels));
    }

    /// <summary>
    ///     Two halves of a plate, the right one laid out at half the density: a radius in texels is measured on the
    ///     triangle under the stroke, not on the mesh's mean.
    /// </summary>
    [Fact]
    public void A_radius_in_texels_is_measured_on_the_triangle_under_the_stroke() {
        PaintMeshSurface surface = new(
            PaintProjection.Over(
                [
                    new(2f, 2f, 0f), new(4f, 2f, 0f), new(4f, 6f, 0f), new(2f, 6f, 0f),
                    new(4f, 2f, 0f), new(6f, 2f, 0f), new(6f, 6f, 0f), new(4f, 6f, 0f)
                ],
                [
                    new(0f, 0f), new(0.5f, 0f), new(0.5f, 1f), new(0f, 1f),
                    new(0.5f, 0f), new(0.75f, 0f), new(0.75f, 0.5f), new(0.5f, 0.5f)
                ],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]
            ),
            Side,
            Side
        );
        var view = Above();
        var perPixel = view.PerPixel(8f);

        Assert.Equal(
            32f / 64f / perPixel,
            surface.PictureRadius(view, view.Project(new(3f, 4f, 0f)), 32f, PaintSizeSpace.Texels),
            3
        );
        Assert.Equal(
            32f / 32f / perPixel,
            surface.PictureRadius(view, view.Project(new(5f, 4f, 0f)), 32f, PaintSizeSpace.Texels),
            3
        );
        Assert.Equal(16f, surface.AtlasRadius(new(160f, 32f), 0.5f, PaintSizeSpace.Model), 3);
    }

    [Fact]
    public void A_turn_and_a_reflection_keep_lengths_and_move_points_as_their_shapes_say() {
        var quarter = PaintIsometry.Rotation(Vector3.UnitZ, Centre, MathF.PI / 2f);
        var mirror = PaintIsometry.Reflection(PaintSymmetry.Across(Vector3.UnitX, Centre));

        Assert.True(Vector3.Distance(new(4f, 5f, 0f), quarter.Point(new(5f, 4f, 0f))) < 1e-5f);
        Assert.True(Vector3.Distance(new(3f, 4f, 7f), mirror.Point(new(5f, 4f, 7f))) < 1e-5f);
        Assert.True(Vector3.Distance(-Vector3.UnitX, mirror.Direction(Vector3.UnitX)) < 1e-6f);
        Assert.Throws<ArgumentOutOfRangeException>(static () => PaintSymmetryGroup.Radial(Vector3.UnitZ, Centre, 1));
        Assert.Throws<ArgumentOutOfRangeException>(static () => PaintSymmetryGroup.Radial(
                Vector3.UnitZ,
                Centre,
                PaintSymmetryGroup.MaximumCount + 1
            )
        );
        Assert.Throws<ArgumentException>(static () => PaintSymmetryGroup.Radial(Vector3.Zero, Centre, 3));
        Assert.Equal(5, PaintSymmetryGroup.Radial(Vector3.UnitZ, Centre, 6).Copies.Count);
    }
}
