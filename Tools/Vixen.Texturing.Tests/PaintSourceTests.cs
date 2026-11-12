// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     Polygon fills, and strokes whose colour comes from somewhere other than one colour, on shapes worked out by
///     hand.
/// </summary>
public class PaintSourceTests {
    const int Side = 128;
    const uint Red = 0xFF0000FFu;
    const uint Blue = 0xFFFF0000u;
    const uint Green = 0xFF00FF00u;

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 8f, Flow = 1f, Falloff = 0f };

    /// <summary>
    ///     A box's front: a flat square of two triangles on one island, and beside it, on the same island and joined
    ///     to it, a second square folded back by a right angle. A third square floats apart on an island of its own.
    /// </summary>
    static PaintProjection Folded(bool parts) {
        Vector3[] positions = [
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
            new(1f, 0f, -1f), new(1f, 1f, -1f),
            new(3f, 0f, 0f), new(4f, 0f, 0f), new(4f, 1f, 0f), new(3f, 1f, 0f)
        ];
        Vector2[] layout = [
            new(0.05f, 0.05f), new(0.3f, 0.05f), new(0.3f, 0.3f), new(0.05f, 0.3f),
            new(0.55f, 0.05f), new(0.55f, 0.3f),
            new(0.65f, 0.65f), new(0.9f, 0.65f), new(0.9f, 0.9f), new(0.65f, 0.9f)
        ];
        int[] front = [0, 1, 2, 0, 2, 3];
        int[] side = [1, 4, 5, 1, 5, 2];
        int[] apart = [6, 7, 8, 6, 8, 9];

        if (!parts) {
            return PaintProjection.Over(positions, layout, [..front, ..side, ..apart]);
        }

        // Two meshes: the box's two faces, and the square apart.
        MeshData box = new() { Positions = positions, TexCoords = layout, Indices = [..front, ..side] };
        MeshData other = new() { Positions = positions, TexCoords = layout, Indices = apart };

        return PaintProjection.Open([box, other], out _)!;
    }

    static int Painted(PaintImage image, int fromX, int toX, int fromY, int toY) {
        var count = 0;

        for (var y = fromY; y < toY; y++) {
            for (var x = fromX; x < toX; x++) {
                count += image.At(x, y) != 0u ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>
    ///     A click in the front square's lower triangle: a triangle fills half the square, a polygon the square and not
    ///     the face folded off it, a shell both faces of the island, and a part both faces of the box's mesh. None of
    ///     them reaches the square apart, and a click in the gutter fills nothing.
    /// </summary>
    [Theory]
    [InlineData(PaintFillMode.Triangle, 0.4f, 0.6f, false, false)]
    [InlineData(PaintFillMode.Polygon, 0.9f, 1.1f, false, false)]
    [InlineData(PaintFillMode.Shell, 0.9f, 1.1f, true, false)]
    [InlineData(PaintFillMode.Part, 0.9f, 1.1f, true, false)]
    public void A_click_fills_the_piece_of_the_mesh_its_mode_takes(
        PaintFillMode mode,
        float least,
        float most,
        bool side,
        bool apart
    ) {
        var surface = new PaintMeshSurface(Folded(mode == PaintFillMode.Part), Side, Side);
        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(image, surface.Coverage, Hard, Red, 0, placement: surface.Fill(mode));

        // The lower triangle is (0,0), (1,0), (1,1): below the diagonal, near the bottom right.
        stroke.Dab(new(0.25f * Side, 0.1f * Side));

        // The front square is 0.05 to 0.3 of the atlas: 32 texels square, 1,024 of them.
        var square = Painted(
            image,
            (int)(0.05f * Side),
            (int)(0.3f * Side) + 1,
            (int)(0.05f * Side),
            (int)(0.3f * Side) + 1
        );

        Assert.InRange(square, 1024 * least, 1024 * most);
        Assert.Equal(side, Painted(image, (int)(0.31f * Side), (int)(0.56f * Side), 0, (int)(0.31f * Side)) > 500);
        Assert.Equal(apart, Painted(image, (int)(0.6f * Side), Side, (int)(0.6f * Side), Side) > 0);

        PaintImage gutter = new(Side, Side);

        new PaintStroke(gutter, surface.Coverage, Hard, Red, 0, placement: surface.Fill(mode)).Dab(
            new(0.45f * Side, 0.5f * Side)
        );

        Assert.Equal(0, Painted(gutter, 0, Side, 0, Side));
    }

    /// <summary>A material's map is laid texel for texel, so a dab copies the map under it and nothing else.</summary>
    [Fact]
    public void A_material_lays_its_own_map_under_the_brush() {
        var surface = new PaintMeshSurface(Folded(false), Side, Side);
        PaintImage map = new(Side, Side);

        // Stripes: blue on even columns, green on odd.
        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                map.Put(x, y, x % 2 == 0 ? Blue : Green);
            }
        }

        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(image, surface.Coverage, Hard, Red, 0, source: new PaintAtlasSource(map, Side, Side));

        stroke.MoveTo(new(20f, 20f));

        Assert.Equal(Blue, image.At(20, 20));
        Assert.Equal(Green, image.At(21, 20));
        Assert.Equal(0u, image.At(40, 40));
    }

    /// <summary>
    ///     A clone reads the picture at its offset: a dab with the source ten texels to its left copies what is there,
    ///     and reading past the picture's edge lays nothing rather than wrapping round to the other side.
    /// </summary>
    [Fact]
    public void A_clone_copies_what_lies_at_its_offset_and_nothing_past_the_edge() {
        var surface = new PaintMeshSurface(Folded(false), Side, Side);
        PaintImage composite = new(Side, Side);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                composite.Put(x, y, x < 15 ? Blue : Green);
            }
        }

        PaintImage image = new(Side, Side);
        var clone = new PaintAtlasSource(composite, Side, Side, new(-10f, 0f));

        new PaintStroke(image, surface.Coverage, Hard, Red, 0, source: clone).MoveTo(new(22f, 20f));

        // Texel 22 reads texel 12, which is blue; texel 27 reads 17, green.
        Assert.Equal(Blue, image.At(22, 20));
        Assert.Equal(Green, image.At(27, 20));

        PaintImage edge = new(Side, Side);

        new PaintStroke(
            edge,
            surface.Coverage,
            Hard,
            Red,
            0,
            source: new PaintAtlasSource(composite, Side, Side, new(-25f, 0f))
        )
            .MoveTo(new(22f, 20f));

        Assert.Equal(0u, edge.At(22, 20));
        Assert.NotEqual(0u, edge.At(27, 20));
    }

    /// <summary>
    ///     A smear drags colour along the stroke: from a blue band into a green field, rightwards, the green just past
    ///     the band turns blue. A dab with no motion moves nothing, and undo puts the canvas back.
    /// </summary>
    [Fact]
    public void A_smudge_drags_colour_the_way_the_brush_moves() {
        var surface = new PaintMeshSurface(Folded(false), Side, Side);
        PaintImage canvas = new(Side, Side);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                canvas.Put(x, y, x < 16 ? Blue : Green);
            }
        }

        var digest = canvas.Digest;
        PaintStroke still = new(
            canvas,
            surface.Coverage,
            Hard,
            Red,
            0,
            source: new PaintSmudge(canvas, surface.Coverage)
        );

        still.MoveTo(new(16f, 20f));

        Assert.Equal(digest, canvas.Digest);

        PaintStroke stroke = new(
            canvas,
            surface.Coverage,
            Hard with { Spacing = 0.1f },
            Red,
            0,
            source: new PaintSmudge(canvas, surface.Coverage)
        );

        stroke.MoveTo(new(12f, 20f));
        stroke.MoveTo(new(26f, 20f));

        Assert.Equal(Blue, canvas.At(19, 20));
        Assert.Equal(Green, canvas.At(19, 34));

        stroke.Undo();

        Assert.Equal(digest, canvas.Digest);
    }

    /// <summary>
    ///     A grey stencil holds a stroke to its share however many stamps cross a texel: a share of each stamp alone was
    ///     built back up by the next, and a stroke that accumulates painted through grey as if it were white.
    /// </summary>
    [Fact]
    public void A_grey_stencil_holds_a_stroke_to_its_share_however_many_stamps_cross() {
        var surface = new PaintMeshSurface(Folded(false), Side, Side);
        PaintImage grey = new(1, 1);

        grey.Put(0, 0, PaintImage.Pack(0.5f, 0.5f, 0.5f, 1f));

        PaintImage image = new(Side, Side);
        PaintStroke stroke = new(
            image,
            surface.Coverage,
            Hard with { Spacing = 0.05f, Blend = PaintStampBlend.Accumulate },
            Red,
            0,
            stencil: new PaintProjectedPicture(grey, new(Vector2.Zero, 1f, 0f, true))
        );

        stroke.MoveTo(new(14f, 20f));
        stroke.MoveTo(new(30f, 20f));
        stroke.MoveTo(new(14f, 20f));

        Assert.InRange(PaintImage.Channel(image.At(22, 20), 0), 0.4f, 0.6f);
    }

    /// <summary>
    ///     A picture on the screen lands where each texel projects: through a camera facing the front square, the left
    ///     half of a two-colour picture paints the square's left half. As a stencil its black half holds the brush back.
    /// </summary>
    [Fact]
    public void A_projected_picture_lands_where_each_texel_projects_and_a_stencil_masks_by_it() {
        var surface = new PaintMeshSurface(Folded(false), Side, Side);
        PaintView view = new(new(0.5f, 0.5f, 3f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, -1f), 0.6f, 256, 256);
        PaintImage picture = new(2, 1);

        picture.Put(0, 0, Blue);
        picture.Put(1, 0, 0xFFFFFFFFu);

        // The picture spans the screen's middle: two pixels, each 64 units wide, centred on the square's middle.
        var frame = new PaintFrame(view.Project(new(0.5f, 0.5f, 0f)), 64f, 0f, false);
        var brush = Hard with { Radius = 60f };
        PaintImage image = new(Side, Side);

        new PaintStroke(
            image,
            surface.Coverage,
            brush,
            Red,
            0,
            placement: surface.Through(view),
            source: new PaintProjectedPicture(picture, frame)
        ).MoveTo(view.Project(new(0.5f, 0.5f, 0f)));

        // The square's left quarter and right quarter, at its middle row (atlas row ≈ 22).
        Assert.Equal(Blue, image.At(14, 22));
        Assert.Equal(0xFFFFFFFFu, image.At(34, 22));

        PaintImage masked = new(Side, Side);

        new PaintStroke(
            masked,
            surface.Coverage,
            brush,
            Red,
            0,
            placement: surface.Through(view),
            stencil: new PaintProjectedPicture(picture, frame)
        ).MoveTo(view.Project(new(0.5f, 0.5f, 0f)));

        // Blue is dark, so it lets little of the brush through; white lets it all.
        Assert.Equal(Red, masked.At(34, 22));
        Assert.True(
            PaintImage.Channel(masked.At(14, 22), 0) < 0.2f,
            "The dark half of the stencil let the brush through."
        );
    }
}
