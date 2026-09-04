// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>tab-size</c>: where the stops are, what lands on them, and what the caret says.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracle throughout is a string of spaces, and it is closed form in the sense this
///         repo means.</b> CSS Text 3 § 6.1 defines the <c>&lt;number&gt;</c> form as that many
///         advances of the element's own space, so <c>"\tx"</c> under <c>tab-8</c> must draw the
///         same picture as eight spaces and an <c>x</c> — a comparison that knows nothing about
///         <c>NextStop</c>, about the pen array, or about which pass measured what. An
///         implementation that snapped to the wrong grid, or measured the tab as a glyph, fails it
///         without anybody having written down the number it was supposed to produce.
///     </para>
///     <para>
///         ⚠ <b>These run at all only because this engine does not collapse white space.</b> In a
///         browser <c>tab-size</c> is invisible outside <c>pre</c>, because a tab under
///         <c>white-space: normal</c> has already become a space by the time it is measured. Vixen's
///         <c>white-space</c> answers the wrapping question and no other — see
///         <c>UiDocument.WrapsOf</c>, which says so — so a literal tab reaches the shaper in every
///         label, and before this property had a reader it drew whatever the face had for U+0009.
///         That is what <see cref="A_tab_draws_no_glyph" /> is really about.
///     </para>
/// </remarks>
public class TabSizeTests {
    const float Tolerance = 0.05f;
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    // ⚠ Wide enough that nothing in this file wraps unless a test asks it to. A tab is eight spaces
    // by default and several of these strings hold two of them, so a 400px box at 16px would not do.
    static UiDocument Documented(string label = "") {
        var document = new UiDocument(2000f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root { width: 2000px; height: 300px; align-items: flex-start; }
              label { font-family: Test; font-size: 16px; {{label}} }
              """
        );

        return document;
    }

    static TextLine Line(string text, string label = "", int at = 0) {
        var document = Documented(label);
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        var block = element.Block();
        Assert.NotNull(block);

        return block.Lines[at];
    }

    static float Width(string text, string label = "") => Line(text, label).Width;

    /// <summary>Where the glyphs of a line actually are.</summary>
    static List<PositionedGlyph> Glyphs(TextLine line) {
        var glyphs = new List<PositionedGlyph>();
        line.Place(glyphs);

        return glyphs;
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The grid
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The initial stop is eight spaces, so a leading tab is eight spaces of room.</summary>
    /// <remarks>
    ///     ⚠ The whole oracle in one line, and it is deliberately the *default* rather than a
    ///     declared value: an implementation that read the property but resolved the initial value
    ///     to zero, or to the glyph's advance, would leave every unstyled label wrong and every
    ///     styled one right.
    /// </remarks>
    [Fact]
    public void A_tab_is_eight_spaces_by_default() =>
        Assert.Equal(Width("        x"), Width("\tx"), Tolerance);

    /// <summary>The count is the property's, so four spaces under <c>tab-size: 4</c>.</summary>
    [Fact]
    public void The_count_is_what_the_property_says() {
        Assert.Equal(Width("    x"), Width("\tx", "tab-size: 4;"), Tolerance);
        Assert.Equal(Width("  x"), Width("\tx", "tab-size: 2;"), Tolerance);
    }

    /// <summary>Two tabs in a row are two columns, not one.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that separates "the next stop strictly after the pen" from "the
    ///     nearest stop at or after it", and the two rules agree everywhere else.</b> Under the
    ///     second reading the first tab lands exactly on stop one, the second finds itself already
    ///     on a stop and advances nothing — so a tabbed table's second column collapses onto its
    ///     first and it reads as a dropped character rather than a wrong width.
    /// </remarks>
    [Fact]
    public void Two_tabs_are_two_columns() =>
        Assert.Equal(Width("                x"), Width("\t\tx"), Tolerance);

    /// <summary>A tab that begins exactly on a stop still advances a whole one.</summary>
    /// <remarks>
    ///     The same rule as <see cref="Two_tabs_are_two_columns" /> reached from the other side:
    ///     eight spaces put the pen on stop one by arithmetic rather than by a previous tab, and
    ///     the tab after them has to go to stop two.
    /// </remarks>
    [Fact]
    public void A_tab_on_a_stop_advances_to_the_next_one() =>
        Assert.Equal(Width("                x"), Width("        \tx"), Tolerance);

    /// <summary>A tab in the middle of a word snaps to the grid rather than adding to the pen.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the one an "advance by N spaces" implementation fails, and it is the whole
    ///     difference between a tab and a wide space.</b> Neither `a` nor `ab` is a whole number of
    ///     spaces wide, so if the tab added eight spaces the `x` would land at two different
    ///     arbitrary offsets; a stop makes it land at exactly eight spaces from the line's start
    ///     whatever precedes it. Two prefixes of different widths therefore put the `x` in the
    ///     *same* place, which is what a column is.
    ///
    ///     ⚠ <b>Both prefixes have to be inside the same column for that to be the claim.</b> `abcd`
    ///     is 36px in Open Sans at 16px and a stop is 33.25px, so a first draft of this test used a
    ///     prefix that had already overflowed into column two and asserted the implementation was
    ///     wrong for putting it there. It was right. The prefixes here are 8px and 19px against a
    ///     33px column.
    /// </remarks>
    [Fact]
    public void A_tab_snaps_to_the_grid_and_does_not_add_to_the_pen() {
        var narrow = Glyphs(Line("a\tx"));
        var wider = Glyphs(Line("ab\tx"));

        // The last glyph of each is the `x`, and both tabs are inside stop one.
        Assert.Equal(narrow[^1].X, wider[^1].X, Tolerance);

        // And that shared position is the grid's, not something the prefixes produced.
        Assert.Equal(Width("        x"), Width("ab\tx"), Tolerance);
    }

    /// <summary>A prefix wider than a column pushes the tab into the next one.</summary>
    /// <remarks>
    ///     The other half of the grid rule, and the half that says these are stops and not a minimum
    ///     width: `abcd` is wider than one column, so the tab after it reaches column two and the
    ///     line measures sixteen spaces rather than eight.
    /// </remarks>
    [Fact]
    public void A_prefix_wider_than_a_column_reaches_the_next_one() {
        Assert.True(Width("abcd") > Width("        "), "the prefix is meant to overflow column one");
        Assert.Equal(Width("                x"), Width("abcd\tx"), Tolerance);
    }

    /// <summary>A zero count makes a tab occupy nothing, which is a real value and not a fallback.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that found the defect, and the defect was a sentinel.</b> Zero
    ///     and "no tab on this line" are the same number inside the layout, and the first draft of
    ///     the mechanism read a non-positive stop as "measure the tab as a glyph" — so
    ///     <c>tab-size: 0</c> reserved the width of a .notdef box (9.6px here) that
    ///     <c>TextRun.Place</c> then declined to draw. Invisible width, which is worse than either
    ///     answer it sat between. A tab is a space whose width the stops decide; with no stops it is
    ///     a space of no width.
    /// </remarks>
    [Fact]
    public void A_zero_count_is_a_tab_that_occupies_nothing() =>
        Assert.Equal(Width("ax"), Width("a\tx", "tab-size: 0;"), Tolerance);

    /// <summary>A negative count clamps to zero rather than running the pen backwards.</summary>
    [Fact]
    public void A_negative_count_clamps() =>
        Assert.Equal(Width("ax"), Width("a\tx", "tab-size: -4;"), Tolerance);

    /// <summary>A length is refused, so the element keeps the initial eight.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than resolved, and the test asserts the refusal is the *documented*
    ///     one.</b> A browser drops a declaration it cannot use and the element keeps the initial
    ///     value; a reader that fell back to zero instead would make <c>tab-size: 20px</c> delete
    ///     every tab in the paragraph, which is a much worse answer than ignoring it.
    /// </remarks>
    [Fact]
    public void A_length_is_refused_and_the_initial_value_stands() =>
        Assert.Equal(Width("        x"), Width("\tx", "tab-size: 20px;"), Tolerance);

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The picture
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A tab puts no glyph on the line, whatever the face mapped U+0009 to.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the defect the feature actually fixes, and it was visible before any
    ///     class was written.</b> HarfBuzz runs U+0009 through the cmap like anything else, Open
    ///     Sans has no glyph for it, and the result is .notdef — a tofu box in the middle of the
    ///     label. Counted as glyphs rather than asserted as a picture because the claim is about
    ///     what is emitted at all; <see cref="A_tab_is_a_gap_and_not_a_box" /> is the pixels.
    /// </remarks>
    [Fact]
    public void A_tab_draws_no_glyph() {
        Assert.Equal(Glyphs(Line("ab")).Count, Glyphs(Line("a\tb")).Count);
        Assert.Equal(Glyphs(Line("ab")).Count, Glyphs(Line("a\t\tb")).Count);
    }

    /// <summary>Nothing is inked between the two letters a tab separates.</summary>
    /// <remarks>
    ///     ⚠ <b>The glyph count is not enough on its own.</b> A suppression written into the line
    ///     rather than the run would leave <c>TextRun.Place</c> emitting the box for every other
    ///     consumer, and a count taken through <c>TextLine</c> would not see it. This asks where the
    ///     glyphs are instead: the gap between the `a` and the `b` is a whole stop wide, so no glyph
    ///     may begin inside it.
    /// </remarks>
    [Fact]
    public void A_tab_is_a_gap_and_not_a_box() {
        var line = Line("a\tb");
        var glyphs = Glyphs(line);

        Assert.Equal(2, glyphs.Count);

        var a = glyphs[0].X;
        var b = glyphs[1].X;

        // The `b` sits on stop one, so the space between them is most of a stop wide.
        Assert.True(b - a > Width("       ") - Tolerance, $"the gap is {b - a}, which is not a stop");

        // And there is nothing in it. `Place` returns every glyph on the line, so this is exhaustive.
        Assert.DoesNotContain(glyphs, glyph => glyph.X > a + Tolerance && glyph.X < b - Tolerance);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The caret
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The caret after a tab is at the next column, not a glyph's advance along.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion the whole feature was held back for.</b> Every other measurement on
    ///     <c>TextLine</c> is a prefix sum over advances that belong to characters; a tab's does not,
    ///     so a caret that asked the run would be handed the .notdef advance and land inside the
    ///     column. Anchored outside the map on purpose: the position is compared against a string of
    ///     spaces rather than against the line's own arithmetic, so an implementation that was
    ///     self-consistently wrong fails.
    /// </remarks>
    [Fact]
    public void The_caret_after_a_tab_is_on_the_stop() {
        var line = Line("\tx");

        // Index 1 is between the tab and the `x`.
        Assert.Equal(Width("        "), line.CaretOffset(1), Tolerance);
    }

    /// <summary>A tab has two caret positions and no interior.</summary>
    /// <remarks>
    ///     A click in the left half of the column goes before the tab and one in the right half goes
    ///     after it, which is what a character with no interior can offer. ⚠ The boundary is the
    ///     middle of the column and not the middle of the glyph the face would have drawn.
    /// </remarks>
    [Fact]
    public void A_click_in_a_tab_goes_to_the_nearer_end() {
        var line = Line("\tx");
        var stop = Width("        ");

        Assert.Equal(0, line.CaretIndexAt(stop * 0.25f));
        Assert.Equal(1, line.CaretIndexAt(stop * 0.75f));
    }

    /// <summary>The caret's two directions still agree across a tab.</summary>
    /// <remarks>
    ///     ⚠ <b>A round trip proves nothing on its own</b> — the identity satisfies it, which is why
    ///     the anchored assertions above exist. It is here because it goes red for a *different*
    ///     reason: a hit test that divided by the tab's glyph advance while the offset used the
    ///     stop's would break the pair without moving either one on its own.
    /// </remarks>
    [Fact]
    public void The_caret_round_trips_across_a_tab() {
        var line = Line("ab\tcd");

        for (var i = 0; i <= 5; i++) {
            Assert.Equal(i, line.CaretIndexAt(line.CaretOffset(i)));
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The wrap
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wrapper measures the stop too, so a tab moves the break.</summary>
    /// <remarks>
    ///     ⚠ <b>The measure and the draw are two passes over different data and both had to learn
    ///     the rule.</b> A tab measured as a glyph in <c>LineWrapper</c> and as a stop in
    ///     <c>TextLine</c> gives a paragraph that breaks where it would have fitted and draws past
    ///     the edge it was broken for — the failure this file's siblings call "breaks in one place,
    ///     draws in another". The oracle is the same string of spaces: the tabbed text and the
    ///     spaced text must break identically.
    /// </remarks>
    [Fact]
    public void The_wrapper_breaks_where_the_stops_put_the_words() {
        // ⚠ 60px is under two columns, so the tab alone is most of the first line's budget and the
        // paragraph has to break. A wider box fits the whole string and the test asserts nothing —
        // which is what a first draft at 120px did.
        // ⚠ The tab is at the start of the line, which is what makes the spaces a valid oracle: a
        // stop is eight spaces from the pen only when the pen is on a stop. A first draft put the
        // tab after `aa` and compared it against six spaces, which is neither the stop's width nor
        // a number the specification names.
        var tabbed = Lines("\tbb cc dd", 60f);
        var spaced = Lines("        bb cc dd", 60f);

        Assert.Equal(spaced.Count, tabbed.Count);
        Assert.True(tabbed.Count > 1, "the box is meant to be too narrow for one line");
    }

    /// <summary>A tab's column is measured from the line box, so an indent does not shift the grid.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TextLine</c> counts from <c>Offset</c> and <c>LineWrapper</c> from its own origin,
    ///     and the two arithmetics have to agree.</b> CSS lays the stops out from the start edge of
    ///     the line box, so an indented first line's columns are the block's columns — which is what
    ///     makes a tabbed table under a hanging indent readable. A pen that started at the glyphs
    ///     instead would put every indented tabbed line a fraction of a stop out.
    /// </remarks>
    [Fact]
    public void An_indent_does_not_move_the_stops() {
        var indented = Line("\tx", "text-indent: 20px;");
        var plain = Line("\tx");

        // `PenOf` includes the offset, so the indented line's `x` is a whole stop from the box edge
        // and the indent is inside the first column rather than added to it.
        Assert.Equal(20f, indented.Offset, Tolerance);
        Assert.Equal(plain.PenOf(plain.Runs.Length - 1), indented.PenOf(indented.Runs.Length - 1), Tolerance);
    }

    static List<TextLine> Lines(string text, float width) {
        var document = Documented($"width: {width}px;");
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        var block = element.Block(width);
        Assert.NotNull(block);

        return [.. block.Lines];
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The property
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>tab-size</c> inherits, so a value on an ancestor reaches the text.</summary>
    /// <remarks>
    ///     ⚠ <b>It can live in <c>InheritedProperties</c> where <c>line-height</c> cannot</b>, and
    ///     the reason is that Vixen inherits *specified* values: a unitless count means the same
    ///     thing wherever it lands, while a length would have to be computed against the ancestor's
    ///     font size and then carried. That is exactly why the length form is refused.
    /// </remarks>
    [Fact]
    public void The_count_inherits() {
        var document = new UiDocument(2000f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            """
            root { width: 2000px; height: 300px; align-items: flex-start; tab-size: 4; }
            label { font-family: Test; font-size: 16px; }
            """
        );

        var element = document.Root.Add("label");
        element.Text = "\tx";
        document.Update();

        var block = element.Block();
        Assert.NotNull(block);

        Assert.Equal(Width("    x"), block.Lines[0].Width, Tolerance);
    }

    /// <summary>Changing the count rebuilds the block rather than reusing the old columns.</summary>
    /// <remarks>
    ///     ⚠ <b>The cache key's own test, and the failure it guards is the invisible kind.</b> A
    ///     block built at one spacing and reused at another draws the old columns until something
    ///     else happens to invalidate it — a picture that is wrong only until it is touched. The
    ///     element's text and every other keyed field are held still here, so the count is the only
    ///     thing that could have caused the rebuild.
    /// </remarks>
    [Fact]
    public void Changing_the_count_rebuilds_the_block() {
        var document = new UiDocument(2000f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            """
            root { width: 2000px; height: 300px; align-items: flex-start; }
            label { font-family: Test; font-size: 16px; }
            label.narrow { tab-size: 4; }
            """
        );

        var element = document.Root.Add("label");
        element.Text = "\tx";
        document.Update();

        var before = element.Block()!.Lines[0].Width;

        element.AddClass("narrow");
        document.Update();

        var after = element.Block()!.Lines[0].Width;

        Assert.NotEqual(before, after, Tolerance);
        Assert.Equal(Width("    x"), after, Tolerance);
    }

    /// <summary>Text with no tab in it measures identically whatever the count says.</summary>
    /// <remarks>
    ///     ⚠ <b>The regression this property is most likely to cause, and it is asserted rather than
    ///     assumed.</b> <c>UiElement.TabStop</c> returns zero for a string with no tab, which takes
    ///     the wrapper's and the line's original paths — so every existing paragraph in the engine
    ///     measures to the same number it did before. If it did not, the layout corpora would be the
    ///     ones to say so, in four places each.
    /// </remarks>
    [Fact]
    public void A_string_with_no_tab_is_untouched_by_the_count() {
        const string Text = "the quick brown fox jumps over the lazy dog";

        var plain = Width(Text);

        Assert.Equal(plain, Width(Text, "tab-size: 1;"), Tolerance);
        Assert.Equal(plain, Width(Text, "tab-size: 0;"), Tolerance);
        Assert.Equal(plain, Width(Text, "tab-size: 97;"), Tolerance);
    }
}
