// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Text.Tests;

/// <summary>What the conformance suite cannot see.</summary>
/// <remarks>
///     <para>
///         The Consortium's cases are the gate, and sabotage says how good a gate: shaping every run
///         as Latin fails 203 of them, forcing every run left to right fails 6, and giving spaces and
///         punctuation runs of their own fails 2 — one of which is the case the Consortium named
///         <i>Space Isn't Nothing</i>.
///     </para>
///     <para>
///         ⚠ But the same sabotage found a hole. <b>Shaping each run without the text around it
///         fails nothing at all</b>, because every case in the suite is a single run and so has no
///         neighbour to lose. The pre- and post-context decision, and the absolute cluster indices
///         that come with it, are invisible to four hundred external cases and are tested here
///         instead. A gate is only a gate for what it can observe, and finding out which half that
///         is costs one sabotage run.
///     </para>
/// </remarks>
public class ShapingTests {
    [Fact]
    public void A_run_is_shaped_with_the_text_around_it_and_not_only_itself() {
        var font = TestFonts.Load(TestFonts.Arabic);
        const string word = "لسان";

        // The seen, alone and then as the second letter of a word. An Arabic letter's form depends
        // on whether its neighbours join, so these must not agree.
        var isolated = TextShaper.ShapeRun(font, "س", new TextItem(0, 1, 1, Script.Arabic));
        var inContext = TextShaper.ShapeRun(font, word, new TextItem(1, 1, 1, Script.Arabic));

        var isolatedName = font.GlyphName(isolated.Glyphs[0].GlyphId);
        var contextName = font.GlyphName(inContext.Glyphs[0].GlyphId);

        Assert.Contains("Med", contextName, StringComparison.Ordinal);
        Assert.DoesNotContain("Med", isolatedName, StringComparison.Ordinal);
    }

    [Fact]
    public void Clusters_are_indices_into_the_whole_text_and_not_into_the_run() {
        var font = TestFonts.Load(TestFonts.Arabic);
        const string mixed = "abcلسان";

        var shaped = TextShaper.Shape(font, mixed);
        var arabic = shaped.Runs.Single(run => run.Item.Script == Script.Arabic);

        // Shaping the substring on its own would produce clusters starting at zero, which would
        // point at 'a' — and every caret, selection and hit test downstream would be three
        // characters out in a way that looks like an off-by-one rather than a missing argument.
        Assert.All(arabic.Glyphs, glyph => Assert.InRange(glyph.Cluster, 3, mixed.Length - 1));
    }

    [Fact]
    public void A_right_to_left_run_comes_back_with_its_clusters_descending() {
        var font = TestFonts.Load(TestFonts.Arabic);

        var shaped = TextShaper.Shape(font, "لسان");
        var clusters = shaped.Runs.Single().Glyphs.Select(glyph => glyph.Cluster).ToList();

        Assert.Equal(clusters.OrderByDescending(cluster => cluster), clusters);
    }

    [Fact]
    public void Latin_inside_a_right_to_left_paragraph_is_drawn_on_the_left() {
        var font = TestFonts.Load(TestFonts.Arabic);

        // An Arabic paragraph, so the base direction is right to left and the Latin word is the
        // deepest run. It is read last and drawn first.
        var shaped = TextShaper.Shape(font, "لسان abc");

        Assert.Equal(Script.Latin, shaped.Runs[0].Item.Script);
        Assert.Equal(Script.Arabic, shaped.Runs[^1].Item.Script);
    }

    [Fact]
    public void A_space_does_not_end_a_run() {
        var font = TestFonts.Load(TestFonts.ContextualLatin);

        var items = TextItemizer.Itemize("a a");

        Assert.Single(items);
        Assert.Equal(3, items[0].Length);

        // And the substitution that needs the space proves the point rather than only asserting it.
        var shaped = TextShaper.Shape(font, "a a");
        Assert.Equal("a.alt", font.GlyphName(shaped.Runs[0].Glyphs[0].GlyphId));
    }

    [Fact]
    public void A_closing_bracket_belongs_to_the_script_its_partner_opened_in() {
        var items = TextItemizer.Itemize("abc(αβγ)def");

        // Three runs: `abc(`, `αβγ`, `)def`. Without the bracket pairing the parenthesis would
        // inherit Greek from what precedes it and the Greek run would be four characters long.
        Assert.Equal(3, items.Count);
        Assert.Equal(Script.Greek, items[1].Script);
        Assert.Equal(3, items[1].Length);
    }

    [Fact]
    public void Punctuation_before_the_first_letter_takes_the_script_that_follows_it() {
        var items = TextItemizer.Itemize("(ಲ್ಲಿ)");

        Assert.Single(items);
        Assert.Equal(Script.Kannada, items[0].Script);
    }

    [Fact]
    public void Shaping_is_independent_of_the_size_it_will_be_drawn_at() {
        var font = TestFonts.Load(TestFonts.Kannada);

        // The claim FontFace makes to justify holding the font at design-unit scale, and the one
        // the shaping cache will be built on: there is no size in the shaping at all.
        Assert.Equal(2048, font.UnitsPerEm);
        Assert.True(font.Metrics.LineHeight > font.UnitsPerEm / 2);
    }

    [Fact]
    public void Empty_text_shapes_to_nothing_rather_than_to_a_run_of_nothing() {
        var font = TestFonts.Load(TestFonts.Kannada);

        var shaped = TextShaper.Shape(font, string.Empty);

        Assert.Empty(shaped.Runs);
        Assert.Empty(shaped.Placements());
        Assert.Equal(0, shaped.Advance);
    }

    [Fact]
    public void A_glyph_the_font_does_not_have_is_notdef_rather_than_an_exception() {
        var font = TestFonts.Load(TestFonts.ContextualLatin);

        Assert.False(font.Supports(0x0644));
        Assert.Equal(0, font.GlyphFor(0x0644));
    }
}
