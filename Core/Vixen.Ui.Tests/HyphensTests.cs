// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>hyphens</c>: the break that already worked, and the hyphen that was never drawn.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This closed a defect rather than an absence, and the two halves were in different
///         files.</b> <c>LineBreaker.Opportunities("sup­ply")</c> has always returned <c>[4,7]</c> —
///         byte-identical to <c>"sup-ply"</c>, where <c>"supply"</c> returns <c>[6]</c> — so Vixen
///         already broke <c>sup|ply</c> where the author asked. It then drew nothing at the break,
///         because U+00AD is <c>Default_Ignorable</c> and <c>TextShaper</c> sets
///         <c>BufferFlags.RemoveDefaultIgnorables</c>. Seven characters, six glyphs, a word split
///         with nothing to show for it. <see cref="A_line_broken_at_a_soft_hyphen_draws_one" /> is
///         the half that was missing.
///     </para>
///     <para>
///         ⚠ <b>The substitution is U+002D and the sizing said U+2010, which was measurably
///         wrong.</b> <see cref="The_drawn_hyphen_is_one_the_face_actually_has" /> is that
///         assertion, and it is here rather than in a comment because it is the one mistake in this
///         area that would have looked like a success: <c>.notdef</c> has a glyph id and an advance,
///         so a test that counted glyphs or measured a width passes against it.
///     </para>
///     <para>
///         ⚠ <b>And what U+2010 would have <i>drawn</i> depends on the face, which matters because
///         the harmless-sounding outcome is the dangerous one.</b> Glyph 0 in <c>TestShapeLana</c>
///         has two contours — a hollow box, visible and obviously wrong. In Open Sans it has
///         <b>zero</b> and draws nothing, so in the engine's own interface face the prescribed
///         substitution would have reproduced the exact defect it was written to fix and looked like
///         a change that simply did not take.
///     </para>
/// </remarks>
public class HyphensTests {
    const float Tolerance = 0.05f;
    const string Soft = "­";
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    static UiDocument Documented(float width, string label = "") {
        var document = new UiDocument(600f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root  { width: 600px; height: 300px; align-items: flex-start; }
              label { font-family: Test; font-size: 16px; width: {{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}}px; {{label}} }
              """
        );

        return document;
    }

    static List<TextLine> Lines(string text, float width, string label = "") {
        var document = Documented(width, label);
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        var block = element.Block(width);
        Assert.NotNull(block);

        return [.. block.Lines];
    }

    /// <summary>Every character the line actually put on the page, in order.</summary>
    static string Drawn(TextLine line) {
        var text = string.Empty;

        foreach (var run in line.Runs) {
            text += run.Shaped.Text;
        }

        return text;
    }

    // ⚠ 44px holds `sup-` and not `supply` in Open Sans at 16px, so the paragraph has to take the
    // opportunity the soft hyphen offers. Any wider and the word fits whole, both values of the
    // property draw the same picture, and every test below asserts nothing at all.
    const float Narrow = 44f;

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The break, which already worked
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A soft hyphen offers a break exactly where a real hyphen would.</summary>
    /// <remarks>
    ///     The claim the sizing made and this confirms, asserted against <c>LineBreaker</c> directly
    ///     because it is a fact about UAX#14 and not about this engine's layout. `"supply"` is the
    ///     control: without the character there is no opportunity inside the word at all.
    /// </remarks>
    [Fact]
    public void A_soft_hyphen_offers_the_break_a_real_one_would() {
        Assert.Equal(LineBreaker.Opportunities("sup-ply"), LineBreaker.Opportunities($"sup{Soft}ply"));
        Assert.Equal([6], LineBreaker.Opportunities("supply"));
    }

    /// <summary>And the paragraph is broken there.</summary>
    [Fact]
    public void The_paragraph_breaks_at_the_soft_hyphen() {
        var lines = Lines($"sup{Soft}ply", Narrow);

        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].Start);
        Assert.Equal(4, lines[1].Start);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The hyphen, which did not
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A line broken at a soft hyphen ends in a hyphen that is drawn.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted as a glyph count as well as a character, because the character alone was
    ///     never the problem.</b> U+00AD was in the line's text before this change too — what was
    ///     missing is that it reached the page. Four characters and four glyphs is the claim; before
    ///     the fix the same line held four characters and three glyphs.
    /// </remarks>
    [Fact]
    public void A_line_broken_at_a_soft_hyphen_draws_one() {
        var lines = Lines($"sup{Soft}ply", Narrow);
        var glyphs = new List<PositionedGlyph>();
        lines[0].Place(glyphs);

        Assert.Equal("sup-", Drawn(lines[0]));
        Assert.Equal(4, glyphs.Count);
    }

    /// <summary>The drawn hyphen is a glyph the face has, and U+2010 is not one.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that refutes the sizing, and it is about the font rather than the
    ///     engine.</b> The plan document said to substitute U+2010 HYPHEN. `GlyphFor` reads the
    ///     cmap: U+2010 is glyph <b>0</b> — <c>.notdef</c> — in Open Sans and in
    ///     <c>TestShapeLana</c> alike, so that substitution draws a box in the one and nothing at
    ///     all in the other. U+002D is glyph 16 in both. The second half of this test is what makes
    ///     the first half worth having: it says the engine picked the character the face can draw,
    ///     not merely that it picked one.
    /// </remarks>
    [Fact]
    public void The_drawn_hyphen_is_one_the_face_actually_has() {
        Assert.Equal(0, Font.GlyphFor(0x2010));
        Assert.NotEqual(0, Font.GlyphFor(0x002D));

        var glyphs = new List<PositionedGlyph>();
        Lines($"sup{Soft}ply", Narrow)[0].Place(glyphs);

        Assert.Equal(Font.GlyphFor(0x002D), glyphs[^1].GlyphId);
    }

    /// <summary>The broken line is as wide as the same letters written with a real hyphen.</summary>
    /// <remarks>
    ///     <para>
    ///         The closed-form half: whatever the engine substituted, the line has to measure like
    ///         `"sup-"`.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This does NOT catch the tofu box, and a first draft of this comment claimed it
    ///         did.</b> The reasoning was that `.notdef` is 1229 design units in Open Sans against
    ///         the hyphen's 659, so a wrong glyph would measure wrong — and it is measurably false:
    ///         a wrapped line reports the *wrapper's* width, and the wrapper charges
    ///         `UiElement.HyphenWidth`, which measures U+002D whatever the substitution then draws.
    ///         Sabotaged to U+2010, this test stays green and the three that ask about the glyph go
    ///         red. Recorded because a width oracle that looks like it covers the picture and does
    ///         not is exactly the instrument this repo keeps being caught by.
    ///     </para>
    ///     <para>
    ///         What it does catch is the wrapper failing to charge for the hyphen at all, which is
    ///         the defect it found when it was written.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_broken_line_measures_like_a_real_hyphen() {
        var broken = Lines($"sup{Soft}ply", Narrow)[0];
        var written = Lines("sup-", 200f)[0];

        Assert.Equal(written.Width, broken.Width, Tolerance);
    }

    /// <summary>A soft hyphen the line did not break at goes on drawing nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of what the character means, and the one a careless substitution
    ///     breaks.</b> U+00AD is a *conditional* hyphen: visible where the line ends, invisible
    ///     everywhere else. Substituting it in the paragraph rather than in the line would put a
    ///     hyphen in the middle of `supply` on every box wide enough not to break it.
    /// </remarks>
    [Fact]
    public void An_unbroken_soft_hyphen_draws_nothing() {
        var lines = Lines($"sup{Soft}ply", 200f);
        var glyphs = new List<PositionedGlyph>();

        Assert.Single(lines);
        lines[0].Place(glyphs);

        // Six letters, six glyphs: the soft hyphen is still deleted by the shaper, as it should be.
        Assert.Equal(6, glyphs.Count);
        Assert.Equal(Lines("supply", 200f)[0].Width, lines[0].Width, Tolerance);
    }

    /// <summary>A paragraph that merely ends on a soft hyphen does not grow one.</summary>
    /// <remarks>
    ///     ⚠ <b>The edge the substitution would have got wrong if it had keyed on "last character of
    ///     the line".</b> The last line of a paragraph ends where the text does, not at a break — so
    ///     a trailing U+00AD was never used as a hyphenation point and has nothing to show.
    /// </remarks>
    [Fact]
    public void A_trailing_soft_hyphen_is_not_a_break_and_draws_nothing() {
        var lines = Lines($"sup{Soft}", 200f);

        Assert.Single(lines);
        Assert.Equal(Lines("sup", 200f)[0].Width, lines[0].Width, Tolerance);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // hyphens: none
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>hyphens: none</c> refuses the break the soft hyphen offered.</summary>
    /// <remarks>
    ///     The word overflows its box instead, which is what `overflow-wrap: normal` does with a word
    ///     that has nowhere to break — and is the whole point of the declaration: the author would
    ///     rather the word ran over than was split.
    /// </remarks>
    [Fact]
    public void None_refuses_the_soft_hyphens_break() {
        var lines = Lines($"sup{Soft}ply", Narrow, "hyphens: none;");

        Assert.Single(lines);
        Assert.Equal(Lines("supply", 200f)[0].Width, lines[0].Width, Tolerance);
    }

    /// <summary>It suppresses only the break the soft hyphen created.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion that separates "no soft-hyphen break" from "no break".</b> A filter
    ///     keyed on the line ending rather than on the character would take a real hyphen's break
    ///     too, and `hyphens` has nothing to say about U+002D — a browser breaks `co-op` under
    ///     `hyphens: none` exactly as it does without it.
    /// </remarks>
    [Fact]
    public void None_leaves_a_real_hyphens_break_alone() {
        var lines = Lines("sup-ply", Narrow, "hyphens: none;");

        Assert.Equal(2, lines.Count);
        Assert.Equal("sup-", Drawn(lines[0]));
    }

    /// <summary>And a space is still a space.</summary>
    [Fact]
    public void None_leaves_ordinary_wrapping_alone() {
        Assert.Equal(
            Lines("sup ply", Narrow).Count,
            Lines("sup ply", Narrow, "hyphens: none;").Count
        );
    }

    /// <summary><c>manual</c> is the initial value, so writing it changes nothing.</summary>
    [Fact]
    public void Manual_is_the_initial_value() =>
        Assert.Equal(
            Lines($"sup{Soft}ply", Narrow).Count,
            Lines($"sup{Soft}ply", Narrow, "hyphens: manual;").Count
        );

    /// <summary><c>auto</c> is refused, and falls back to <c>manual</c> rather than to nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused, not approximated, and the fallback direction matters.</b> Vixen has no
    ///     hyphenation patterns, so it cannot find the breaks `auto` would add — but `auto` also
    ///     honours the author's own soft hyphens, and that half it can do. Landing on `manual` keeps
    ///     it; landing on `none` would make a declaration asking for *more* hyphenation produce
    ///     less. No utility emits the keyword, so this is only reachable from hand-written vcss.
    /// </remarks>
    [Fact]
    public void Auto_falls_back_to_manual_rather_than_to_none() {
        var lines = Lines($"sup{Soft}ply", Narrow, "hyphens: auto;");

        Assert.Equal(2, lines.Count);
        Assert.Equal("sup-", Drawn(lines[0]));
    }

    /// <summary>The mode inherits, so a card can keep its words whole.</summary>
    [Fact]
    public void The_mode_inherits() {
        var document = new UiDocument(600f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root  { width: 600px; height: 300px; align-items: flex-start; hyphens: none; }
              label { font-family: Test; font-size: 16px; width: {{Narrow}}px; }
              """
        );

        var element = document.Root.Add("label");
        element.Text = $"sup{Soft}ply";
        document.Update();

        Assert.Single(element.Block(Narrow)!.Lines);
    }

    /// <summary>Changing the mode rebuilds the block rather than reusing the old breaks.</summary>
    [Fact]
    public void Changing_the_mode_rebuilds_the_block() {
        var document = new UiDocument(600f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root         { width: 600px; height: 300px; align-items: flex-start; }
              label        { font-family: Test; font-size: 16px; width: {{Narrow}}px; }
              label.whole  { hyphens: none; }
              """
        );

        var element = document.Root.Add("label");
        element.Text = $"sup{Soft}ply";
        document.Update();

        Assert.Equal(2, element.Block(Narrow)!.Lines.Length);

        element.AddClass("whole");
        document.Update();

        Assert.Single(element.Block(Narrow)!.Lines);
    }
}
