// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Nine-slice images: doc 06 § Geometry and materials, the row that says sprites share the UI
///     batcher.
/// </summary>
/// <remarks>
///     Two claims, and the second is the one worth a test suite. The first is that the nine cells are
///     cut and paired correctly — corners at their own size, edges and middle carrying the stretch.
///     The second is that none of it is visible to the renderer: a nine-sliced panel is the same
///     <c>Image</c> command kind, the same batch and the same descriptor set as the icon beside it,
///     so nine quads cost one draw rather than a pipeline of their own.
/// </remarks>
public class NineSliceImageTests {
    const ulong Atlas = 7;
    const ulong Other = 9;

    static readonly Rectangle Viewport = new(0, 0, 800, 600);

    /// <summary>A 128-pixel sheet with a 16-pixel border, expressed the way the command wants it.</summary>
    static readonly NineSlice Source = NineSlice.Uniform(16f / 128f);

    [Fact]
    public void An_image_with_no_slice_is_still_one_quad() {
        var geometry = Build(list => list.Add(Image(0, 0, 200, 100)));

        Assert.Equal(4, geometry.Vertices.Count);
        Assert.Equal(6, geometry.Indices.Count);
        Assert.Equal(BatchKind.Image, Assert.Single(geometry.Draws).Kind);
    }

    [Fact]
    public void A_nine_slice_is_nine_quads_in_one_draw() {
        var geometry = Build(list => list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f))));

        Assert.Equal(9 * 4, geometry.Vertices.Count);
        Assert.Equal(9 * 6, geometry.Indices.Count);

        // The point of the whole arrangement: nine quads, one draw, one texture binding.
        var draw = Assert.Single(geometry.Draws);
        Assert.Equal(BatchKind.Image, draw.Kind);
        Assert.Equal(Atlas, draw.Image);
        Assert.Equal(9 * 6, draw.Count);
    }

    [Fact]
    public void The_corners_keep_their_size_and_the_middle_takes_the_stretch() {
        var geometry = Build(list => list.Add(Sliced(10, 20, 200, 100, NineSlice.Uniform(16f))));

        // Top-left corner: sixteen by sixteen wherever the box goes, drawn from the sheet's own
        // sixteen-pixel corner.
        Assert.Equal(new Vector2(10, 20), geometry.Vertices[0].Position);
        Assert.Equal(new Vector2(26, 36), geometry.Vertices[2].Position);
        Assert.Equal(new Vector2(0f, 0f), geometry.Vertices[0].Texture);
        Assert.Equal(0.125f, geometry.Vertices[2].Texture.X, 5);

        // Bottom-right corner, at the far end of a box four times as wide as it is tall in border
        // terms — still sixteen by sixteen.
        var last = geometry.Vertices.Count - 4;
        Assert.Equal(new Vector2(194, 104), geometry.Vertices[last].Position);
        Assert.Equal(new Vector2(210, 120), geometry.Vertices[last + 2].Position);
        Assert.Equal(1f, geometry.Vertices[last + 2].Texture.X, 5);
    }

    [Fact]
    public void Every_destination_cell_is_paired_with_its_own_source_cell() {
        var geometry = Build(list => list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f))));

        // The middle cell is the fifth quad, and it reads the middle of the sheet. Reading a corner
        // there is the classic nine-slice fault and it looks like a texture offset rather than like
        // a pairing mistake, so it is asserted rather than eyeballed.
        var middle = 4 * 4;

        Assert.Equal(new Vector2(16, 16), geometry.Vertices[middle].Position);
        Assert.Equal(new Vector2(184, 84), geometry.Vertices[middle + 2].Position);
        Assert.Equal(0.125f, geometry.Vertices[middle].Texture.X, 5);
        Assert.Equal(0.875f, geometry.Vertices[middle + 2].Texture.X, 5);
    }

    [Fact]
    public void A_hollow_centre_leaves_out_the_middle_and_nothing_else() {
        var geometry = Build(
            list => list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f)) with { HollowCentre = true })
        );

        Assert.Equal(8 * 4, geometry.Vertices.Count);

        // The eight that remain are the ring, so the fifth quad is now the right-hand edge rather
        // than the middle — which is why the cells are skipped at emission and not compacted in the
        // split.
        Assert.Equal(new Vector2(184, 16), geometry.Vertices[4 * 4].Position);
    }

    [Fact]
    public void A_box_narrower_than_its_own_corners_compresses_them_without_moving_the_texels() {
        // 16 + 16 of border in a box 20 wide: both corners scale by 20/32, and the vertical borders
        // scale by the same factor even though they fit. See NineSlice.Fit — one factor is what
        // keeps a corner from being squashed into a different shape than it was drawn at.
        var geometry = Build(list => list.Add(Sliced(0, 0, 20, 100, NineSlice.Uniform(16f))));

        Assert.Equal(10f, geometry.Vertices[2].Position.X, 4);
        Assert.Equal(10f, geometry.Vertices[2].Position.Y, 4);

        // ⚠ And the source is not fitted with it. The corner shows the same texels, compressed —
        // which is what "the box got small" looks like, rather than "the artwork changed".
        Assert.Equal(0.125f, geometry.Vertices[2].Texture.X, 5);
        Assert.Equal(0.125f, geometry.Vertices[2].Texture.Y, 5);
    }

    [Fact]
    public void Empty_cells_are_skipped_rather_than_emitted_flat() {
        // No top border: the first row is zero-high, so three of the nine enclose no pixels at all.
        var geometry = Build(list => list.Add(Sliced(0, 0, 200, 100, new(16f, 0f, 16f, 16f))));

        Assert.Equal(6 * 4, geometry.Vertices.Count);
        Assert.Equal(new Vector2(0, 0), geometry.Vertices[0].Position);
        Assert.Equal(new Vector2(16, 84), geometry.Vertices[2].Position);
    }

    [Fact]
    public void An_inset_on_one_side_only_still_draws_one_quad_per_surviving_cell() {
        var geometry = Build(
            list => list.Add(
                Sliced(0, 0, 200, 100, new(16f, 0f, 0f, 0f)) with { SourceSlice = new(0.125f, 0f, 0f, 0f) }
            )
        );

        // Two columns and one row: the left border and everything to the right of it.
        Assert.Equal(2 * 4, geometry.Vertices.Count);
        Assert.Equal(new Vector2(16, 0), geometry.Vertices[4].Position);
        Assert.Equal(0.125f, geometry.Vertices[4].Texture.X, 5);
    }

    [Fact]
    public void A_slice_with_no_source_border_falls_back_to_one_stretched_quad() {
        // ⚠ Not eight zero-width strips smeared along the edges, which is what cutting a source with
        // no border to preserve would produce. A caller who set one inset and forgot the other gets
        // the ordinary image rather than a panel with streaks down it.
        var geometry = Build(
            list => list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f)) with { SourceSlice = default })
        );

        Assert.Equal(4, geometry.Vertices.Count);
        Assert.Equal(new Vector2(200, 100), geometry.Vertices[2].Position);
    }

    [Fact]
    public void A_nine_slice_batches_with_the_images_around_it() {
        // The claim in the roadmap row, checked: slicing is geometry and not state, so a panel and
        // the icon on top of it are one draw as long as they come from the same sheet.
        var geometry = Build(
            list => {
                list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f)));
                list.Add(Image(20, 20, 32, 32));
            }
        );

        var draw = Assert.Single(geometry.Draws);
        Assert.Equal(10 * 6, draw.Count);
        Assert.Equal(Atlas, draw.Image);
    }

    [Fact]
    public void A_different_texture_still_breaks_the_batch() {
        // What slicing does not change: a texture is a descriptor set, so two sheets are two draws
        // however adjacent they are.
        var geometry = Build(
            list => {
                list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f)));
                list.Add(Image(20, 20, 32, 32) with { Image = Other });
            }
        );

        Assert.Equal(2, geometry.Draws.Count);
        Assert.Equal(Atlas, geometry.Draws[0].Image);
        Assert.Equal(Other, geometry.Draws[1].Image);
    }

    [Fact]
    public void Slicing_is_part_of_what_the_frame_diff_compares() {
        // The command is a value and the diff is a comparison of values, so a panel whose border
        // changed is a frame that changed. Worth asserting because the insets arrived as init-only
        // properties after the diff was written, and a field left out of equality would leave the
        // renderer drawing last frame's panel.
        var list = new DrawList();

        list.BeginFrame();
        list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f)));
        list.EndFrame();

        var version = list.Version;

        list.BeginFrame();
        list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(16f)));
        list.EndFrame();

        Assert.Equal(version, list.Version);

        list.BeginFrame();
        list.Add(Sliced(0, 0, 200, 100, NineSlice.Uniform(24f)));
        list.EndFrame();

        Assert.NotEqual(version, list.Version);
    }

    static DrawCommand Image(float x, float y, float width, float height) =>
        new(DrawCommandKind.Image, x, y, width, height, Color4.White, 0, 0) { Image = Atlas };

    static DrawCommand Sliced(float x, float y, float width, float height, NineSlice border) =>
        Image(x, y, width, height) with { Slice = border, SourceSlice = Source };

    static UiGeometry Build(Action<DrawList> paint) {
        var list = new DrawList();
        list.BeginFrame();
        paint(list);
        list.EndFrame();

        return new UiGeometryBuilder().Build(list, new GlyphFieldCache(new GlyphAtlas(512, 512)), Viewport);
    }
}
