// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Numerics;
using Vixen.Ui.Text.Outlines;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     The atlas: what it holds, what it throws away, and what a caller may still believe afterwards.
/// </summary>
public class GlyphAtlasTests {
    [Fact]
    public void A_glyph_put_in_can_be_found_again() {
        var atlas = new GlyphAtlas(64, 64);
        var key = new GlyphKey(0, 7, 32);

        Assert.True(atlas.Add(key, Field(8, 8), out var written));
        Assert.True(atlas.TryGet(key, out var read));

        Assert.Equal(written, read);
        Assert.Equal(8, read.Width);
        Assert.Equal(1, atlas.Count);
    }

    [Fact]
    public void A_glyph_that_was_never_put_in_is_a_miss() {
        var atlas = new GlyphAtlas(64, 64);

        Assert.False(atlas.TryGet(new GlyphKey(0, 1, 32), out _));
        Assert.Equal(1, atlas.Misses);
        Assert.Equal(0, atlas.Hits);
    }

    [Fact]
    public void Adding_the_same_glyph_twice_keeps_the_first_placement() {
        var atlas = new GlyphAtlas(64, 64);
        var key = new GlyphKey(0, 7, 32);

        Assert.True(atlas.Add(key, Field(8, 8), out var first));
        Assert.True(atlas.Add(key, Field(8, 8), out var second));

        Assert.Equal(first, second);
        Assert.Equal(1, atlas.Count);
    }

    [Fact]
    public void A_field_larger_than_the_texture_is_refused_rather_than_evicting_everything() {
        var atlas = new GlyphAtlas(32, 32);
        Assert.True(atlas.Add(new GlyphKey(0, 1, 32), Field(8, 8), out _));

        Assert.False(atlas.Add(new GlyphKey(0, 2, 32), Field(64, 8), out _));
        Assert.Equal(1, atlas.Count);
        Assert.Equal(0, atlas.Evictions);
    }

    /// <summary>The glyph's own pixels land where the region says, and nowhere else.</summary>
    [Fact]
    public void The_field_is_written_at_the_region_it_reports() {
        var atlas = new GlyphAtlas(64, 64);
        Assert.True(atlas.Add(new GlyphKey(0, 1, 32), Field(4, 4, 0.25f), out var first));
        Assert.True(atlas.Add(new GlyphKey(0, 2, 32), Field(4, 4, 0.75f), out var second));

        Assert.Equal(0.25f, At(atlas, first.X + 2, first.Y + 2), 4);
        Assert.Equal(0.75f, At(atlas, second.X + 2, second.Y + 2), 4);
    }

    /// <summary>
    ///     ⚠ <b>And an entry on the second shelf lands on the second shelf.</b> Everything above
    ///     places on row zero, where dropping the region's y from the destination offset is
    ///     invisible — a sabotage doing exactly that broke nothing until this existed, and it would
    ///     have written every glyph over the top of the first row.
    /// </summary>
    [Fact]
    public void An_entry_below_the_first_shelf_is_written_below_the_first_shelf() {
        var atlas = new GlyphAtlas(16, 32, padding: 0);

        Assert.True(atlas.Add(new GlyphKey(0, 1, 32), Field(16, 8, 0.25f), out var first));
        Assert.True(atlas.Add(new GlyphKey(0, 2, 32), Field(16, 8, 0.75f), out var second));

        Assert.Equal(0, first.Y);
        Assert.Equal(8, second.Y);

        Assert.Equal(0.25f, At(atlas, 4, first.Y + 4), 4);
        Assert.Equal(0.75f, At(atlas, 4, second.Y + 4), 4);
    }

    /// <summary>
    ///     ⚠ Padding is why a filtered read of one glyph's edge cannot pick up its neighbour. Two
    ///     entries touching would bleed into each other at every size but the one they were stored at.
    /// </summary>
    [Fact]
    public void Two_entries_never_touch() {
        var atlas = new GlyphAtlas(64, 64, padding: 1);
        Assert.True(atlas.Add(new GlyphKey(0, 1, 32), Field(4, 4), out var first));
        Assert.True(atlas.Add(new GlyphKey(0, 2, 32), Field(4, 4), out var second));

        var gap = second.X - (first.X + first.Width);
        Assert.True(gap >= 1, $"the two entries are {gap} pixels apart");
    }

    // ------------------------------------------------------------ Eviction

    /// <summary>
    ///     ⚠ The coldest goes, not the oldest: reading a glyph is what keeps it. A cache that evicted
    ///     in insertion order would throw away the space character on a page made of them.
    /// </summary>
    [Fact]
    public void The_least_recently_used_glyph_is_the_one_that_goes() {
        var atlas = new GlyphAtlas(16, 16, padding: 0);

        Assert.True(atlas.Add(new GlyphKey(0, 1, 32), Field(8, 8), out _));
        Assert.True(atlas.Add(new GlyphKey(0, 2, 32), Field(8, 8), out _));
        Assert.True(atlas.Add(new GlyphKey(0, 3, 32), Field(8, 8), out _));
        Assert.True(atlas.Add(new GlyphKey(0, 4, 32), Field(8, 8), out _));

        // Touch the first, so the second becomes the coldest.
        Assert.True(atlas.TryGet(new GlyphKey(0, 1, 32), out _));

        Assert.True(atlas.Add(new GlyphKey(0, 5, 32), Field(8, 8), out _));

        Assert.True(atlas.TryGet(new GlyphKey(0, 1, 32), out _));
        Assert.False(atlas.TryGet(new GlyphKey(0, 2, 32), out _));
        Assert.Equal(1, atlas.Evictions);
    }

    /// <summary>
    ///     ⚠ <b>A freed slot is reused rather than left as a hole.</b> Without it, an interface
    ///     cycling through more glyphs than fit would evict one and then fail to place its
    ///     replacement, and the atlas would compact on every frame.
    /// </summary>
    [Fact]
    public void An_evicted_slot_is_reused_by_a_glyph_that_fits_it() {
        var atlas = new GlyphAtlas(16, 16, padding: 0);

        for (ushort glyph = 1; glyph <= 4; glyph++) {
            Assert.True(atlas.Add(new GlyphKey(0, glyph, 32), Field(8, 8), out _));
        }

        var before = atlas.Version;
        Assert.True(atlas.Add(new GlyphKey(0, 5, 32), Field(8, 8), out _));

        // One eviction, one reuse, and no repack — which is the thing being asserted.
        Assert.Equal(1, atlas.Evictions);
        Assert.Equal(before, atlas.Version);
        Assert.Equal(4, atlas.Count);
    }

    /// <summary>
    ///     ⚠ <b>And when no hole is the right shape, compaction is the fallback.</b> Every region
    ///     changes, so the version moves and a caller holding texture coordinates has to ask again —
    ///     which is the only reason the version exists.
    /// </summary>
    [Fact]
    public void A_glyph_that_fits_no_hole_forces_a_compaction_and_moves_the_version() {
        var atlas = new GlyphAtlas(16, 16, padding: 0);

        // Two shelves of narrow glyphs, filling the texture.
        for (ushort glyph = 1; glyph <= 4; glyph++) {
            Assert.True(atlas.Add(new GlyphKey(0, glyph, 32), Field(8, 8), out _));
        }

        var before = atlas.Version;

        // Wider than any single slot, so evicting cannot make a hole it fits.
        Assert.True(atlas.Add(new GlyphKey(0, 9, 32), Field(16, 8), out var region));

        Assert.NotEqual(before, atlas.Version);
        Assert.Equal(16, region.Width);
        Assert.True(atlas.TryGet(new GlyphKey(0, 9, 32), out _));
    }

    [Fact]
    public void Compaction_keeps_the_warmest_entries_when_not_all_of_them_fit() {
        var atlas = new GlyphAtlas(16, 16, padding: 0);

        for (ushort glyph = 1; glyph <= 4; glyph++) {
            Assert.True(atlas.Add(new GlyphKey(0, glyph, 32), Field(8, 8), out _));
        }

        // Make glyph 1 the warmest, then force a compaction that cannot hold everything.
        Assert.True(atlas.TryGet(new GlyphKey(0, 1, 32), out _));
        Assert.True(atlas.Add(new GlyphKey(0, 9, 32), Field(16, 8), out _));

        Assert.True(atlas.TryGet(new GlyphKey(0, 1, 32), out _));
    }

    [Fact]
    public void Clearing_forgets_everything_and_moves_the_version() {
        var atlas = new GlyphAtlas(64, 64);
        Assert.True(atlas.Add(new GlyphKey(0, 1, 32), Field(8, 8), out _));

        var before = atlas.Version;
        atlas.Clear();

        Assert.Equal(0, atlas.Count);
        Assert.NotEqual(before, atlas.Version);
        Assert.False(atlas.TryGet(new GlyphKey(0, 1, 32), out _));
    }

    // ------------------------------------------------------------ Upload

    [Fact]
    public void The_texture_is_dirty_until_a_renderer_says_it_has_uploaded_it() {
        var atlas = new GlyphAtlas(64, 64);
        atlas.Uploaded();
        Assert.False(atlas.Dirty);

        Assert.True(atlas.Add(new GlyphKey(0, 1, 32), Field(8, 8), out _));
        Assert.True(atlas.Dirty);

        atlas.Uploaded();
        Assert.False(atlas.Dirty);

        // ⚠ A hit changes nothing about the texture, so it must not ask for an upload — every frame
        // of a static interface is hits, and re-uploading a megabyte for each of them is the bug
        // this flag exists to prevent.
        Assert.True(atlas.TryGet(new GlyphKey(0, 1, 32), out _));
        Assert.False(atlas.Dirty);
    }

    // ------------------------------------------------------------ End to end

    /// <summary>
    ///     A real font, a real field, into the atlas and back out with the pixels intact.
    /// </summary>
    [Fact]
    public void A_real_glyph_survives_the_round_trip_into_the_texture() {
        var face = TestFonts.Load(TestFonts.Kannada);
        var glyph = face.GlyphFor('ಕ');
        var outline = face.GetOutline(glyph);
        var bounds = outline.Bounds();

        var scale = 32f / Math.Max(bounds.Width, bounds.Height);
        var width = (int)Math.Ceiling(bounds.Width * scale) + 8;
        var height = (int)Math.Ceiling(bounds.Height * scale) + 8;
        var origin = new Vector2(bounds.MinX - (4 / scale), bounds.MinY - (4 / scale));

        var field = DistanceField.Generate(outline, width, height, scale, origin);
        var atlas = new GlyphAtlas(256, 256);

        Assert.True(atlas.Add(new GlyphKey(1, glyph, 32), field, out var region));
        Assert.Equal(width, region.Width);
        Assert.Equal(height, region.Height);

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                Assert.Equal(field[x, y].R, At(atlas, region.X + x, region.Y + y), 5);
            }
        }
    }

    // ------------------------------------------------------------ Helpers

    static float At(GlyphAtlas atlas, int x, int y) => atlas.Pixels[(((y * atlas.Width) + x) * 3) + 0];

    /// <summary>A field of a given size, filled with one value, so a copy can be told apart.</summary>
    static DistanceFieldBitmap Field(int width, int height, float value = 0.5f) {
        var channels = new float[width * height * 3];
        Array.Fill(channels, value);
        return new DistanceFieldBitmap(width, height, 4f, channels);
    }

    static GlyphOutline Path(params OutlineSegment[] segments) => new(ImmutableArray.Create(segments));
}
