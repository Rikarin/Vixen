// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>OpenType features, from the CSS spelling down to the glyphs that come back.</summary>
/// <remarks>
///     ⚠ <b>The shaping cache's key is the thing this file exists for.</b> Everything else here is a
///     parser with an obvious right answer; the key is the part that would have shipped broken and
///     stayed broken, because a wrong one produces <i>correct</i> glyphs for the first paragraph
///     drawn and the same glyphs for every later one. Two labels with different features would agree
///     with each other, look plausible, and depend on draw order.
/// </remarks>
public class FontFeatureTests {
    /// <summary>The face whose <c>calt</c> turns the first <c>a</c> of <c>"a a"</c> into an alternate.</summary>
    /// <remarks>
    ///     ⚠ <b>A feature that is on by <i>default</i>, so the test can switch it off.</b> Every
    ///     numeric feature <c>font-variant-numeric</c> names — <c>tnum</c>, <c>onum</c>, <c>zero</c> —
    ///     is absent from all twenty-two embedded faces, so asking for one of those here would shape
    ///     identically with the array and without it, and the test would pass against a
    ///     <c>Shape(buffer, [])</c> that never changed. <c>calt</c> is the only feature in this
    ///     repository whose presence or absence is visible in a glyph id.
    /// </remarks>
    static FontFace Contextual => TestFonts.Load(TestFonts.ContextualLatin);

    const string Alternating = "a a";

    static ushort[] Glyphs(ShapedText shaped) => shaped.Placements().Select(p => p.GlyphId).ToArray();

    static FontFeatureSet Off(string tag) => FontFeatureSet.Of([new FontFeature(FontFeature.Pack(tag), 0u)]);

    [Theory]
    [InlineData("tnum")]
    [InlineData("ss01")]
    [InlineData("cv99")]
    public void A_tag_survives_being_packed(string tag) =>
        Assert.Equal(tag, FontFeature.Unpack(FontFeature.Pack(tag)));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcde")]
    [InlineData("abéd")]
    public void A_tag_that_is_not_four_ascii_characters_packs_to_nothing(string tag) =>
        Assert.Equal(0u, FontFeature.Pack(tag));

    [Theory]
    [InlineData("\"tnum\"", "tnum", 1u)]
    [InlineData("\"tnum\" 1", "tnum", 1u)]
    [InlineData("\"tnum\" 0", "tnum", 0u)]
    [InlineData("'liga' off", "liga", 0u)]
    [InlineData("  \"liga\"  ON  ", "liga", 1u)]
    [InlineData("\"cv01\" 3", "cv01", 3u)]
    public void A_feature_tag_value_parses(string text, string tag, uint value) {
        Assert.True(FontFeature.TryParse(text, out var feature));
        Assert.Equal(tag, FontFeature.Unpack(feature.Tag));
        Assert.Equal(value, feature.Value);
    }

    /// <summary>
    ///     ⚠ The quotes are CSS's grammar and not strictness: without them there is no telling the
    ///     four-character tag <c>calt</c> from the keyword <c>normal</c>, which means "no features".
    /// </summary>
    [Theory]
    [InlineData("tnum")]
    [InlineData("normal")]
    [InlineData("\"tnum")]
    [InlineData("\"toolong\" 1")]
    [InlineData("\"tnum\" maybe")]
    [InlineData("\"tnum\" -1")]
    public void An_ill_formed_feature_is_refused(string text) => Assert.False(FontFeature.TryParse(text, out _));

    [Fact]
    public void An_empty_set_is_the_shared_one() => Assert.Same(FontFeatureSet.None, FontFeatureSet.Of([]));

    /// <summary>
    ///     ⚠ <b>Sorting and deduplicating is what makes the cache key work</b>: two declarations that
    ///     ask for the same thing in a different order have to be one entry, or the cache holds two
    ///     copies of one shaping and the memory budget is a function of how people write CSS.
    /// </summary>
    [Fact]
    public void Two_sets_that_ask_for_the_same_thing_are_equal() {
        var one = FontFeatureSet.Of([
            new FontFeature(FontFeature.Pack("tnum"), 1u), new FontFeature(FontFeature.Pack("onum"), 1u)
        ]);

        var other = FontFeatureSet.Of([
            new FontFeature(FontFeature.Pack("onum"), 1u), new FontFeature(FontFeature.Pack("tnum"), 1u)
        ]);

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
    }

    /// <summary>A later entry for one tag wins, which is what lets the escape hatch override.</summary>
    [Fact]
    public void The_last_value_for_a_tag_is_the_one_kept() {
        var set = FontFeatureSet.Of([
            new FontFeature(FontFeature.Pack("tnum"), 1u), new FontFeature(FontFeature.Pack("tnum"), 0u)
        ]);

        Assert.Equal(0u, Assert.Single(set.Features).Value);
    }

    /// <summary>The face's own default, which is what every caller got before there was an array.</summary>
    [Fact]
    public void A_default_feature_applies_when_nothing_is_asked_for() {
        var plain = Glyphs(TextShaper.Shape(Contextual, Alternating));
        var explicitly = Glyphs(TextShaper.Shape(Contextual, Alternating, features: FontFeatureSet.None));

        Assert.Equal(plain, explicitly);
        Assert.NotEqual(plain[0], plain[2]);
    }

    /// <summary>And switching it off reaches HarfBuzz.</summary>
    /// <remarks>
    ///     Asserted as "the two <c>a</c>s became the same glyph" rather than as a glyph id, because an
    ///     id is a fact about the font's build and this is a fact about the feature: <c>calt</c> is
    ///     what makes the first one an alternate, and without it there is nothing to tell them apart.
    /// </remarks>
    [Fact]
    public void Switching_a_feature_off_changes_the_glyphs() {
        var suppressed = Glyphs(TextShaper.Shape(Contextual, Alternating, features: Off("calt")));

        Assert.Equal(suppressed[0], suppressed[2]);
        Assert.NotEqual(Glyphs(TextShaper.Shape(Contextual, Alternating)), suppressed);
    }

    /// <summary>
    ///     ⚠ <b>The failure that would have shipped: one cache entry for two feature sets.</b>
    /// </summary>
    /// <remarks>
    ///     The cache was keyed on the font and the string, which was exactly right for as long as
    ///     shaping was a function of those two. It stopped being one here. Without the set in the key
    ///     the second call is a <i>hit</i> — the assertion below on <c>Misses</c> is what says so —
    ///     and it comes back with the first call's glyphs, which is a correct-looking answer to the
    ///     wrong question and is invisible to anything that only looks at one label.
    /// </remarks>
    [Fact]
    public void The_cache_does_not_serve_one_feature_set_from_another() {
        var cache = new ShapingCache();

        var plain = Glyphs(cache.Shape(Contextual, Alternating));
        var suppressed = Glyphs(cache.Shape(Contextual, Alternating, features: Off("calt")));

        Assert.Equal(2, cache.Misses);
        Assert.NotEqual(plain, suppressed);

        // And asking for either of them again is a hit, so the key is not merely different every time.
        Assert.Equal(plain, Glyphs(cache.Shape(Contextual, Alternating)));
        Assert.Equal(suppressed, Glyphs(cache.Shape(Contextual, Alternating, features: Off("calt"))));
        Assert.Equal(2, cache.Misses);
        Assert.Equal(2, cache.Hits);
    }
}
