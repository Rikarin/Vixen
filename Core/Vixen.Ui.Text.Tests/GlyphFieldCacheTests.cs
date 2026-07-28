// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>
///     The one question a renderer asks about a glyph: where is it, and where do I put it.
/// </summary>
public class GlyphFieldCacheTests {
    [Fact]
    public void A_glyph_is_encoded_once_and_found_thereafter() {
        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var face = TestFonts.Load(TestFonts.Kannada);
        var glyph = face.GlyphFor('ಕ');

        Assert.True(cache.TryGet(face, 0, glyph, out var first));
        Assert.True(cache.TryGet(face, 0, glyph, out var second));

        Assert.Equal(first, second);
        Assert.Equal(1, cache.Generated);
        Assert.Equal(1, cache.Atlas.Count);
    }

    [Fact]
    public void A_glyph_that_draws_nothing_is_reported_as_such_and_not_encoded_twice() {
        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var face = TestFonts.Load(TestFonts.Kannada);
        var space = face.GlyphFor(' ');

        Assert.False(cache.TryGet(face, 0, space, out _));
        Assert.False(cache.TryGet(face, 0, space, out _));

        Assert.Equal(0, cache.Generated);
        Assert.Equal(0, cache.Atlas.Count);

        // ⚠ And the second answer came from the cache, not from the font. Nothing about the atlas
        // can show that — a glyph that draws nothing never reaches it — so the read is counted.
        // A sabotage that forgot to remember the empty result passed everything else.
        Assert.Equal(1, cache.Reads);
    }

    /// <summary>
    ///     ⚠ <b>The placement is in ems, so one entry serves every size.</b> A placement in pixels
    ///     would be right for one font size and wrong for the next, which is exactly the failure the
    ///     size-free atlas key exists to avoid — and it would be invisible until somebody drew the
    ///     same word twice at two sizes.
    /// </summary>
    [Fact]
    public void The_placement_is_the_same_whatever_size_it_will_be_drawn_at() {
        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var face = TestFonts.Load(TestFonts.Kannada);

        Assert.True(cache.TryGet(face, 0, face.GlyphFor('ಕ'), out var placement));

        // A glyph's box is a fraction of an em, so the numbers are small and the top is above the
        // baseline. Anything in pixels would be tens or hundreds.
        Assert.InRange(placement.Top, 0.1f, 2f);
        Assert.True(placement.Right > placement.Left);
        Assert.True(placement.Top > placement.Bottom);
    }

    /// <summary>
    ///     ⚠ The quad covers the padded cell, not the glyph's silhouette. A glyph drawn with an
    ///     outline or a glow reads past its own edge, and a cell cropped to the silhouette has
    ///     nothing there to read.
    /// </summary>
    [Fact]
    public void The_quad_is_larger_than_the_glyph_because_the_field_is_padded() {
        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256), resolution: 32, range: 4f);
        var face = TestFonts.Load(TestFonts.Kannada);
        var glyph = face.GlyphFor('ಕ');

        Assert.True(cache.TryGet(face, 0, glyph, out var placement));

        var bounds = face.GetOutline(glyph).Bounds();
        var em = (float)face.UnitsPerEm;

        Assert.True(placement.Left < bounds.MinX / em, "the quad starts inside the glyph");
        Assert.True(placement.Right > bounds.MaxX / em, "the quad ends inside the glyph");
    }

    /// <summary>
    ///     ⚠ <b>Eviction takes the pixels and not the placement.</b> Where a glyph sits relative to
    ///     the pen came from the font and cannot have changed, so re-reading the outline to learn it
    ///     again would be work for an answer already held.
    /// </summary>
    [Fact]
    public void A_glyph_evicted_and_asked_for_again_keeps_where_it_sits() {
        var cache = new GlyphFieldCache(new GlyphAtlas(64, 64), resolution: 16);
        var face = TestFonts.Load(TestFonts.Kannada);
        var glyph = face.GlyphFor('ಕ');

        Assert.True(cache.TryGet(face, 0, glyph, out var before));

        // Push it out with other glyphs until the atlas has to evict.
        for (ushort other = 1; other < 60 && cache.Atlas.Evictions == 0; other++) {
            cache.TryGet(face, 0, other, out _);
        }

        Assert.True(cache.Atlas.Evictions > 0, "nothing was evicted, so this proves nothing");
        Assert.True(cache.TryGet(face, 0, glyph, out var after));

        Assert.Equal(before.Left, after.Left, 5);
        Assert.Equal(before.Top, after.Top, 5);
        Assert.Equal(before.Right, after.Right, 5);
        Assert.Equal(before.Bottom, after.Bottom, 5);

        // ⚠ And the pixels are back, which the placement alone cannot say. Reporting a remembered
        // placement beside a region the atlas no longer holds would have every glyph sampling
        // whatever else has since been packed at the origin — found by a sabotage doing exactly
        // that and passing.
        Assert.False(after.IsEmpty);
        Assert.True(cache.Atlas.TryGet(new GlyphKey(0, glyph, cache.Resolution), out var region));
        Assert.Equal(region, after.Region);
    }

    [Fact]
    public void Two_fonts_with_the_same_glyph_id_are_different_glyphs() {
        var cache = new GlyphFieldCache(new GlyphAtlas(256, 256));
        var kannada = TestFonts.Load(TestFonts.Kannada);
        var balinese = TestFonts.Load("NotoSansBalinese-Regular.ttf");

        Assert.True(cache.TryGet(kannada, 0, 10, out var first));
        Assert.True(cache.TryGet(balinese, 1, 10, out var second));

        Assert.NotEqual(first.Region, second.Region);
        Assert.Equal(2, cache.Generated);
    }

    /// <summary>
    ///     ⚠ The range a shader thresholds against scales with the size the glyph is drawn at, so it
    ///     is reported per em like everything else. A constant would be right at one size only, and
    ///     text would blur as it grew and alias as it shrank.
    /// </summary>
    [Fact]
    public void The_screen_pixel_range_is_reported_per_em() {
        var atlas = new GlyphAtlas(512, 512);
        var coarse = new GlyphFieldCache(atlas, resolution: 16, range: 4f);
        var fine = new GlyphFieldCache(new GlyphAtlas(512, 512), resolution: 64, range: 4f);
        var face = TestFonts.Load(TestFonts.Kannada);
        var glyph = face.GlyphFor('ಕ');

        Assert.True(coarse.TryGet(face, 0, glyph, out var low));
        Assert.True(fine.TryGet(face, 0, glyph, out var high));

        // Four times the resolution over the same range is four times the pixels per unit.
        Assert.True(high.ScreenPixelRange > low.ScreenPixelRange * 3.5f);
    }
}
