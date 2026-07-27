// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>The shaping cache, judged against shaping without one.</summary>
/// <remarks>
///     A cache is only ever wrong in one way — by answering differently from the thing it stands in
///     for — so that is what is checked, over random sequences of lookups rather than over cases
///     somebody chose. The same shape of gate as the incremental restyle pass in
///     <c>Vixen.Ui.Styling</c>, and for the same reason: an oracle that shares no code with its
///     subject.
/// </remarks>
public class ShapingCacheTests {
    static readonly string[] Words = [
        "a a", "abc", "hello world", "", " ",
        "لسان", "فن خطاطی", "abcلسان", "لسان abc",
        "ಲ್ಲಿ", "ಖ್ಯೆ ಫ್ರಿ", "ಲ್ಲಿabc"
    ];

    [Fact]
    public void A_cached_paragraph_is_the_paragraph_that_would_have_been_shaped() {
        var font = TestFonts.Load(TestFonts.Arabic);

        Gen.Int[0, Words.Length - 1].Array[1, 60]
            .Sample(indices => {
                // Deliberately smaller than the number of distinct strings, so the run evicts and
                // re-shapes rather than only ever filling.
                var cache = new ShapingCache(4);

                foreach (var index in indices) {
                    var cached = cache.Shape(font, Words[index]);
                    var cold = TextShaper.Shape(font, Words[index]);

                    Assert.Equal(Describe(cold), Describe(cached));
                }

                Assert.True(cache.Count <= 4, $"the cache holds {cache.Count} entries with a capacity of 4");
            }, iter: 200);
    }

    [Fact]
    public void The_least_recently_used_paragraph_is_the_one_that_goes() {
        var font = TestFonts.Load(TestFonts.Arabic);
        var cache = new ShapingCache(2);

        cache.Shape(font, "abc");
        cache.Shape(font, "لسان");
        cache.Shape(font, "abc");

        // `abc` was used most recently, so the Arabic is what a third paragraph displaces.
        cache.Shape(font, "ಲ್ಲಿ");

        var before = cache.Hits;
        cache.Shape(font, "abc");
        Assert.Equal(before + 1, cache.Hits);

        var misses = cache.Misses;
        cache.Shape(font, "لسان");
        Assert.Equal(misses + 1, cache.Misses);
    }

    [Fact]
    public void The_size_a_paragraph_will_be_drawn_at_is_not_part_of_the_key() {
        var font = TestFonts.Load(TestFonts.Kannada);
        var cache = new ShapingCache();

        // There is no size to pass, and that is the claim rather than an omission: shaping happens
        // in design units, so one entry serves every size and every DPI scale the label is drawn
        // at. A cache keyed on size would miss on every frame of a growing label.
        var first = cache.Shape(font, "ಖ್ಯೆ");
        var second = cache.Shape(font, "ಖ್ಯೆ");

        Assert.Same(first, second);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void Two_fonts_with_the_same_text_are_two_entries() {
        var arabic = TestFonts.Load(TestFonts.Arabic);
        var kannada = TestFonts.Load(TestFonts.Kannada);
        var cache = new ShapingCache();

        cache.Shape(arabic, "abc");
        cache.Shape(kannada, "abc");

        // The font is part of the key by reference, so this cannot collide however the two faces
        // happen to be named — and a glyph id from the wrong font is a silently wrong picture
        // rather than a crash.
        Assert.Equal(2, cache.Count);
        Assert.Equal(2, cache.Misses);
    }

    [Fact]
    public void A_forced_direction_is_a_different_entry_from_the_one_that_was_worked_out() {
        var font = TestFonts.Load(TestFonts.Arabic);
        var cache = new ShapingCache();

        cache.Shape(font, "abcلسان");
        cache.Shape(font, "abcلسان", ParagraphDirection.RightToLeft);

        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Clearing_forgets_everything() {
        var font = TestFonts.Load(TestFonts.Arabic);
        var cache = new ShapingCache();

        cache.Shape(font, "abc");
        cache.Clear();

        Assert.Equal(0, cache.Count);

        cache.Shape(font, "abc");
        Assert.Equal(2, cache.Misses);
    }

    static string Describe(ShapedText shaped) =>
        string.Join(
            " ",
            shaped.Placements().Select(placement => $"{placement.GlyphId}@{placement.X:0.##},{placement.Y:0.##}#{placement.Cluster}")
        );
}
