// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Rendering.Sprites;
using Xunit;

namespace Tests;

/// <summary>
///     Sprites, sheets, clips and the quads they expand into — doc 06 § Geometry and materials.
/// </summary>
/// <remarks>
///     All of it without a device, because all of it is arithmetic: a sprite is texels and a pixel
///     density, and the expansion is a pure function of the two. What a golden image would add is
///     whether the shader agrees about which way up a V coordinate runs, and that is a separate gate.
/// </remarks>
public class SpriteTests {
    /// <summary>A 64-texel cell on a 128-texel sheet at 64 pixels per unit: one world unit square.</summary>
    static Sprite Square(NineSlice border = default) =>
        new() {
            Name = "square",
            Region = new(0f, 0f, 64f, 64f),
            TextureSize = new(128, 128),
            Border = border,
            PixelsPerUnit = 64f
        };

    static SpriteVertex[] Build(Sprite sprite, SpriteAppearance appearance = default) {
        var quads = SpriteGeometry.QuadsFor(sprite, appearance);
        var vertices = new SpriteVertex[quads * SpriteGeometry.VerticesPerQuad];

        // ⚠ Asserted rather than assumed: a caller sizes its buffer from QuadsFor and writes with
        // Build, so the two disagreeing by one leaves uninitialised vertices in the middle of the
        // frame's geometry — geometry that draws, from whatever the allocator left there.
        Assert.Equal(quads, SpriteGeometry.Build(sprite, appearance, vertices));

        return vertices;
    }

    [Fact]
    public void A_sprite_answers_its_own_size_and_uvs_from_texels() {
        var sprite = new Sprite {
            Region = new(32f, 16f, 64f, 32f),
            TextureSize = new(128, 64),
            PixelsPerUnit = 32f
        };

        Assert.Equal(new Vector2(2f, 1f), sprite.Size);
        Assert.Equal(new Rectangle(0.25f, 0.25f, 0.5f, 0.5f), sprite.Uv);
    }

    [Fact]
    public void A_sprite_with_no_texture_size_reads_the_whole_texture() {
        // An importer that has not filled the dimensions in yet should draw the picture it was
        // pointed at, not divide by zero and sample an infinity.
        var sprite = new Sprite { Region = new(0f, 0f, 64f, 64f) };

        Assert.Equal(new Rectangle(0f, 0f, 1f, 1f), sprite.Uv);
        Assert.True(sprite.UvBorder.IsEmpty);
    }

    [Fact]
    public void A_border_arrives_in_three_units_and_means_the_same_thing_in_all_of_them() {
        var sprite = Square(NineSlice.Uniform(16f));

        Assert.Equal(16f, sprite.Border.Left, 5);
        Assert.Equal(0.125f, sprite.UvBorder.Left, 5);
        Assert.Equal(0.25f, sprite.UnitBorder.Left, 5);
        Assert.True(sprite.IsSliced);
    }

    [Fact]
    public void A_plain_sprite_is_one_quad_around_its_pivot() {
        var vertices = Build(Square());

        Assert.Equal(SpriteGeometry.VerticesPerQuad, vertices.Length);

        // Counter-clockwise from the bottom left, one unit square centred on the pivot.
        Assert.Equal(new Vector3(-0.5f, -0.5f, 0f), vertices[0].Position);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0f), vertices[2].Position);

        // ⚠ V runs downward (Conventions.md), so the *bottom* of the quad samples the *bottom* of
        // the region, which is the larger V. Reading it the other way draws every sprite upside down
        // and consistently enough to look intentional.
        Assert.Equal(new Vector2(0f, 0.5f), vertices[0].Texture);
        Assert.Equal(new Vector2(0.5f, 0f), vertices[2].Texture);
    }

    [Fact]
    public void The_pivot_moves_the_quad_and_not_the_texture() {
        var vertices = Build(Square() with { Pivot = Vector2.Zero });

        // A pivot at the bottom left puts the whole quad up and to the right of the origin, which is
        // what a character standing on the ground wants.
        Assert.Equal(new Vector3(0f, 0f, 0f), vertices[0].Position);
        Assert.Equal(new Vector3(1f, 1f, 0f), vertices[2].Position);
        Assert.Equal(new Vector2(0f, 0.5f), vertices[0].Texture);
    }

    [Fact]
    public void An_overridden_size_stretches_the_quad() {
        var vertices = Build(Square(), new() { Size = new(4f, 2f) });

        Assert.Equal(new Vector3(-2f, -1f, 0f), vertices[0].Position);
        Assert.Equal(new Vector3(2f, 1f, 0f), vertices[2].Position);
    }

    [Fact]
    public void A_bordered_sprite_is_nine_quads_and_the_corners_keep_their_size() {
        var vertices = Build(Square(NineSlice.Uniform(16f)), new() { Size = new(4f, 2f) });

        Assert.Equal(9 * SpriteGeometry.VerticesPerQuad, vertices.Length);

        // Sixteen texels at sixty-four to the unit is a quarter of a unit, whatever the panel is.
        // Bottom-left corner of the top-left cell, and the top-right of it.
        Assert.Equal(new Vector3(-2f, 0.75f, 0f), vertices[0].Position);
        Assert.Equal(new Vector3(-1.75f, 1f, 0f), vertices[2].Position);

        // The middle cell carries the whole stretch: everything the two borders left over.
        var middle = 4 * SpriteGeometry.VerticesPerQuad;
        Assert.Equal(new Vector3(-1.75f, -0.75f, 0f), vertices[middle].Position);
        Assert.Equal(new Vector3(1.75f, 0.75f, 0f), vertices[middle + 2].Position);

        // And reads the middle of the region, not a corner of it.
        Assert.Equal(new Vector2(0.125f, 0.375f), vertices[middle].Texture);
        Assert.Equal(new Vector2(0.375f, 0.125f), vertices[middle + 2].Texture);
    }

    [Fact]
    public void A_hollow_centre_drops_one_cell_and_keeps_the_ring() {
        var appearance = new SpriteAppearance { Size = new(4f, 2f), HollowCentre = true };

        Assert.Equal(8, SpriteGeometry.QuadsFor(Square(NineSlice.Uniform(16f)), appearance));
        Assert.Equal(8 * SpriteGeometry.VerticesPerQuad, Build(Square(NineSlice.Uniform(16f)), appearance).Length);
    }

    [Fact]
    public void A_panel_smaller_than_its_own_corners_compresses_them() {
        // Two quarter-unit borders in a box a fifth of a unit wide: everything shrinks by 0.4, both
        // axes together, so the corners stay square. NineSlice.Fit is where that rule lives.
        var vertices = Build(Square(NineSlice.Uniform(16f)), new() { Size = new(0.2f, 2f) });

        Assert.Equal(-0.1f, vertices[0].Position.X, 4);
        Assert.Equal(0f, vertices[2].Position.X, 4);
        Assert.Equal(0.1f, vertices[2].Position.Y - vertices[0].Position.Y, 4);

        // ⚠ The source is not fitted with it: the corner shows the same texels, compressed.
        Assert.Equal(0.125f, vertices[2].Texture.X, 5);
    }

    [Fact]
    public void A_tiled_fill_repeats_the_middle_at_its_own_pixel_size() {
        var appearance = new SpriteAppearance { Size = new(4f, 2f), Fill = SpriteFill.Tile };
        var vertices = Build(Square(), appearance);

        // One unit of artwork over four by two units of floor: eight repeats, each a unit square.
        Assert.Equal(8, SpriteGeometry.QuadsFor(Square(), appearance));
        Assert.Equal(new Vector3(-2f, 0f, 0f), vertices[0].Position);
        Assert.Equal(new Vector3(-1f, 1f, 0f), vertices[2].Position);

        // Every repeat reads the whole region, which is what makes it a repeat rather than a stretch.
        Assert.Equal(new Vector2(0f, 0.5f), vertices[0].Texture);
        Assert.Equal(new Vector2(0.5f, 0f), vertices[2].Texture);
    }

    [Fact]
    public void The_last_repeat_of_a_tiled_fill_is_clipped_rather_than_squeezed() {
        var appearance = new SpriteAppearance { Size = new(2.5f, 1f), Fill = SpriteFill.Tile };
        var vertices = Build(Square(), appearance);

        Assert.Equal(3, SpriteGeometry.QuadsFor(Square(), appearance));

        // The third repeat has half a unit to live in, so it draws half the pattern and stops —
        // the way a tiled floor meets a wall. Squeezing it would make one tile in every row a
        // different size from its neighbours, which is more visible than the cut.
        var last = 2 * SpriteGeometry.VerticesPerQuad;
        Assert.Equal(1.25f, vertices[last + 2].Position.X, 4);
        Assert.Equal(0.25f, vertices[last + 2].Texture.X, 5);
    }

    [Fact]
    public void A_tiled_border_repeats_the_edges_and_never_the_corners() {
        var appearance = new SpriteAppearance { Size = new(4f, 2f), Fill = SpriteFill.Tile };
        var quads = SpriteGeometry.QuadsFor(Square(NineSlice.Uniform(16f)), appearance);

        // A quarter-unit border on a 4×2 panel. What the middle repeats is the middle of the
        // *region* — 64 texels less two 16-texel borders is 32, which is half a unit — so a 3.5 ×
        // 1.5 middle is 7 × 3 of it. The two horizontal edges tile 7 each, the two vertical ones 3
        // each, and the four corners are drawn once: a corner is the one part of a nine-slice that
        // is never stretched, so it has nothing to repeat.
        Assert.Equal(21 + (2 * 7) + (2 * 3) + 4, quads);
    }

    [Fact]
    public void A_tile_far_too_small_for_its_box_stretches_instead_of_exploding() {
        // ⚠ The ceiling in SpriteGeometry.TileLimit, and it is a real one. A one-unit pattern over a
        // hundred units is a hundred repeats each way — ten thousand quads for one object — and the
        // number is a property of how small somebody drew their artwork rather than of the scene.
        var appearance = new SpriteAppearance { Size = new(100f, 100f), Fill = SpriteFill.Tile };

        Assert.Equal(1, SpriteGeometry.QuadsFor(Square(), appearance));

        // And the limit is not a cliff nobody can see: one below it still tiles.
        var inside = new SpriteAppearance { Size = new(SpriteGeometry.TileLimit, 1f), Fill = SpriteFill.Tile };
        Assert.Equal(SpriteGeometry.TileLimit, SpriteGeometry.QuadsFor(Square(), inside));
    }

    [Fact]
    public void A_flip_mirrors_the_geometry_and_the_texture_together() {
        var sprite = Square() with { Pivot = Vector2.Zero };
        var vertices = Build(sprite, new() { Flip = SpriteFlip.Horizontal });

        // Mirrored about the pivot, so the quad now hangs to the left of the origin.
        Assert.Equal(-1f, vertices[0].Position.X, 5);
        Assert.Equal(0f, vertices[2].Position.X, 5);

        // ⚠ And the UVs go with it. Mirroring one without the other is the difference between a
        // flipped sprite and a sprite drawn in the wrong place — see SpriteFlip.
        Assert.Equal(0.5f, vertices[0].Texture.X, 5);
        Assert.Equal(0f, vertices[2].Texture.X, 5);
    }

    [Fact]
    public void A_flipped_nine_slice_moves_its_corners_rather_than_mirroring_each_in_place() {
        var sprite = Square(new(32f, 0f, 0f, 0f)) with { Pivot = Vector2.Zero };
        var vertices = Build(sprite, new() { Size = new(2f, 1f), Flip = SpriteFlip.Horizontal });

        // A border on the left only: two cells, and after the flip the border's cell is the one on
        // the *right*. Mirroring the UVs alone would leave it where it was.
        Assert.Equal(2 * SpriteGeometry.VerticesPerQuad, vertices.Length);
        Assert.Equal(-0.5f, vertices[0].Position.X, 5);
        Assert.Equal(0f, vertices[2].Position.X, 5);
    }

    [Fact]
    public void A_sprite_with_no_region_draws_nothing() =>
        Assert.Equal(0, SpriteGeometry.QuadsFor(new() { TextureSize = new(128, 128) }, default));

    [Fact]
    public void A_size_override_takes_both_axes_or_neither() {
        // The sentinel is "unspecified", and half of a size is not a size: a sprite nought units
        // wide is not a thing anybody asks for, so an override with a zero in it falls back to the
        // authored size rather than collapsing the quad to a line.
        var vertices = Build(Square(), new() { Size = new(0f, 4f) });

        Assert.Equal(new Vector3(-0.5f, -0.5f, 0f), vertices[0].Position);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0f), vertices[2].Position);
    }

    [Fact]
    public void An_unfilled_appearance_draws_the_sprite_in_white() {
        // ⚠ The transparent-black sentinel. A per-object array arrives zeroed, so the reading that
        // makes an appearance nobody set draw the sprite is the only useful one — and a tint that is
        // both black and fully transparent draws nothing whichever way it is read.
        var vertices = Build(Square());

        Assert.Equal(Vector4.One, vertices[0].Colour);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), Build(Square(), new() { Colour = new(1f, 0f, 0f, 1f) })[0].Colour);
    }

    [Fact]
    public void A_short_span_writes_as_many_whole_quads_as_fit() {
        var sprite = Square(NineSlice.Uniform(16f));
        var vertices = new SpriteVertex[4 * SpriteGeometry.VerticesPerQuad];

        // Four quads out of nine, and no half-written fifth: a partially written quad is three
        // corners of geometry and one of whatever was in the buffer.
        Assert.Equal(4, SpriteGeometry.Build(sprite, new() { Size = new(4f, 2f) }, vertices));
    }

    [Fact]
    public void The_index_pattern_is_two_triangles_a_quad_from_its_own_corners() {
        var indices = new uint[2 * SpriteGeometry.IndicesPerQuad];

        Assert.Equal(indices.Length, SpriteGeometry.WriteQuadIndices(indices, 2));
        Assert.Equal([0u, 1u, 2u, 0u, 2u, 3u, 4u, 5u, 6u, 4u, 6u, 7u], indices);
    }

    [Fact]
    public void A_grid_cuts_a_sheet_row_major_from_the_top_left() {
        var sheet = SpriteSheet.Grid("run", new(128, 64), new(32, 32), pixelsPerUnit: 32f);

        Assert.Equal(8, sheet.Count);
        Assert.Equal("run_5", sheet[5].Name);

        // Index five is the second row's second column, because a texture is laid out in rows and so
        // is every walk cycle ever drawn on one.
        Assert.Equal(new Rectangle(32f, 32f, 32f, 32f), sheet[5].Region);
        Assert.Equal(new Vector2(1f, 1f), sheet[5].Size);
    }

    [Fact]
    public void A_grid_leaves_a_partial_cell_at_the_edge_alone() {
        // 100 texels of a 32-texel cell is three cells and four texels left over. The four are a
        // mistake in the artwork or in the numbers, and drawing them as though they were a whole
        // frame is that mistake made invisible.
        var sheet = SpriteSheet.Grid("tiles", new(100, 32), new(32, 32));

        Assert.Equal(3, sheet.Count);
    }

    [Fact]
    public void A_grid_accounts_for_padding_without_needing_it_after_the_last_cell() {
        // Cells at 0, 34 and 68: the third ends at 100, so three fit in 128 — and a fourth would
        // start at 102 and end at 134. The off-by-one to avoid is charging padding for the last one.
        var sheet = SpriteSheet.Grid("tiles", new(102, 32), new(32, 32), padding: new(2, 0));

        Assert.Equal(3, sheet.Count);
        Assert.Equal(new Rectangle(68f, 0f, 32f, 32f), sheet[2].Region);
    }

    [Fact]
    public void A_sheet_looks_a_sprite_up_by_name() {
        var sheet = SpriteSheet.Grid("idle", new(64, 32), new(32, 32));

        Assert.Equal(1, sheet.IndexOf("idle_1"));
        Assert.Equal("idle_1", sheet.Find("idle_1")?.Name);

        // A name nobody cut is -1 and null rather than a throw: the name comes from a script, and a
        // frame that asks for a sprite that is not there should not take the frame down.
        Assert.Equal(-1, sheet.IndexOf("idle_9"));
        Assert.Null(sheet.Find("idle_9"));
    }

    [Fact]
    public void A_clip_holds_loops_or_ping_pongs() {
        var frames = new[] { 0, 1, 2, 3 };

        var once = new SpriteAnimation { Frames = frames, FrameRate = 10f, Wrap = SpriteWrap.Once };
        var loop = once with { Wrap = SpriteWrap.Loop };
        var pingPong = once with { Wrap = SpriteWrap.PingPong };

        // ⚠ Floor, not round: a frame is on screen from its own start until the next one's.
        Assert.Equal(0, loop.FrameAt(0f));
        Assert.Equal(1, loop.FrameAt(0.15f));
        Assert.Equal(0, loop.FrameAt(0.4f));

        Assert.Equal(3, once.FrameAt(10f));

        // Round trip without repeating either end: 0 1 2 3 2 1, then round again.
        Assert.Equal(2, pingPong.FrameAt(0.4f));
        Assert.Equal(1, pingPong.FrameAt(0.5f));
        Assert.Equal(0, pingPong.FrameAt(0.6f));

        Assert.Equal(0.4f, loop.Duration, 5);
    }

    [Fact]
    public void A_clip_run_backwards_stays_inside_its_frames() {
        // ⚠ C#'s remainder keeps its left operand's sign, so the naive modulo indexes backwards out
        // of the array — a clip started in the future, or rewound past zero.
        var loop = new SpriteAnimation { Frames = [0, 1, 2, 3], FrameRate = 10f };

        Assert.Equal(3, loop.FrameAt(-0.05f));
        Assert.Equal(0, loop.FrameAt(-0.4f));
        Assert.InRange(loop.FrameAt(-123.456f), 0, 3);
    }

    [Fact]
    public void A_clip_resolves_against_the_sheet_it_indexes_and_survives_a_recut() {
        var sheet = SpriteSheet.Grid("run", new(64, 32), new(32, 32));
        var clip = new SpriteAnimation { Frames = [0, 1], FrameRate = 10f };

        Assert.Equal("run_1", clip.SpriteAt(sheet, 0.1f)?.Name);

        // A frame past the end of a re-cut sheet draws nothing for a moment rather than throwing:
        // the clip and the sheet are separate content and either can be rebuilt without the other.
        Assert.Null(new SpriteAnimation { Frames = [7], FrameRate = 10f }.SpriteAt(sheet, 0f));
        Assert.Equal(-1, new SpriteAnimation().FrameAt(0f));
    }

    [Fact]
    public void A_sheet_survives_a_round_trip_through_the_binary_serializer() {
        // The claim that a sheet is *content* rather than a runtime convenience, checked. The
        // generator emits a serializer for every [DataContract] and nothing else would notice if the
        // one it emitted for a list behind an interface did not work.
        var sheet = SpriteSheet.Grid("run", new(128, 64), new(32, 32), border: NineSlice.Uniform(4f));
        var reread = Serializer.Read<SpriteSheet>(Serializer.ToBytes(sheet));

        Assert.Equal(sheet.Name, reread.Name);
        Assert.Equal(sheet.TextureSize, reread.TextureSize);
        Assert.Equal(sheet.Count, reread.Count);

        // A sprite is a record, so this compares the values rather than the references — which is
        // the whole of what a round trip has to preserve.
        Assert.Equal(sheet[5], reread[5]);

        // And the lookup is rebuilt on the far side rather than serialised, because it is a cache.
        Assert.Equal(5, reread.IndexOf("run_5"));
    }

    [Fact]
    public void A_clip_survives_a_round_trip_through_the_binary_serializer() {
        var clip = new SpriteAnimation { Name = "run", Frames = [0, 1, 2, 1], FrameRate = 8f, Wrap = SpriteWrap.PingPong };
        var reread = Serializer.Read<SpriteAnimation>(Serializer.ToBytes(clip));

        Assert.Equal(clip.Name, reread.Name);
        Assert.Equal(clip.Frames, reread.Frames);
        Assert.Equal(clip.FrameRate, reread.FrameRate);
        Assert.Equal(clip.Wrap, reread.Wrap);
    }
}
