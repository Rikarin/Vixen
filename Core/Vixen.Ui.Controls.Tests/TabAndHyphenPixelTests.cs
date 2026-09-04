// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>tab-size</c> and <c>hyphens</c>, counted as ink rather than as draw commands.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both features were closed against draw-list assertions, and a draw-list assertion is
///         not a picture.</b> `TabSizeTests` asks where `Place` put the glyphs and `HyphensTests`
///         asks which glyph id came out; neither runs the rasteriser, and this repository has
///         already shipped an overline drawn across the capitals past exactly that kind of check.
///         The oracles here know nothing about the layout: columns of inked *pixels*, and ink in a
///         place that had none.
///     </para>
///     <para>
///         Open Sans rather than a Consortium fixture, for the reason its two sibling files give: a
///         face that draws every character as .notdef inks the same columns however wrong the text
///         is — and for `hyphens` that is not a hypothetical, since the substitution this proves is
///         precisely the one whose wrong answer is a .notdef box.
///     </para>
/// </remarks>
public class TabAndHyphenPixelTests {
    static readonly FontFace Font = LoadFont("OpenSans-Regular.ttf");

    /// <summary>The face whose <c>.notdef</c> is a box rather than an empty glyph.</summary>
    /// <remarks>
    ///     ⚠ <b>Both faces are needed, and finding out why cost a sabotage that stayed green.</b>
    ///     `.notdef` is glyph 0 in each, and what glyph 0 <i>is</i> differs: in Open Sans it has
    ///     <b>zero contours</b> — an empty glyph that inks nothing — and in `TestShapeLana` it has
    ///     two, the familiar hollow box. So a picture drawn in Open Sans cannot tell a suppressed
    ///     U+0009 from a drawn one, and <see cref="A_tab_inks_nothing_between_the_letters_it_separates" />
    ///     passed with the suppression removed until it was moved onto this face.
    /// </remarks>
    static readonly FontFace Boxy = LoadFont("TestShapeLana.ttf");

    static FontFace LoadFont(string file) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Controls.Tests.Fonts.{file}")
            ?? throw new InvalidOperationException($"{file} is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: file);
    }

    static Bitmap Render(string text, string declarations = "", float width = 240f) =>
        Render(Font, text, declarations, width);

    static Bitmap Render(FontFace font, string text, string declarations = "", float width = 240f) {
        using var ui = UiTest.Create(320f, 120f);
        ui.Document.Fonts.Register("Test", font);

        ui.Load(
            $$"""
              root   { width: 320px; height: 120px; background-color: #000000; align-items: flex-start; }
              .label { position: absolute; left: 8px; top: 8px;
                       width: {{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}}px;
                       font-size: 16px; line-height: 2; color: #ffffff; {{declarations}} }
              """
        );

        ui.Create("div", null, "label", "label").Text = text;
        ui.Frame();

        return ui.Capture();
    }

    /// <summary>How many separated columns of inked pixels the frame holds.</summary>
    /// <remarks>
    ///     The transpose of <c>LineClampPixelTests.Bands</c>, and the right axis for a tab: a column
    ///     is a run of x positions with ink somewhere down them, so a gap wide enough to separate two
    ///     of them is a gap the rasteriser actually left.
    /// </remarks>
    static List<(int Start, int End)> Columns(Bitmap image) {
        var columns = new List<(int, int)>();
        var start = -1;

        for (var x = 0; x < image.Width; x++) {
            var inked = false;

            for (var y = 0; y < image.Height && !inked; y++) {
                inked = image.Pixels[image.Offset(x, y)] >= 40;
            }

            if (inked && start < 0) {
                start = x;
            } else if (!inked && start >= 0) {
                columns.Add((start, x));
                start = -1;
            }
        }

        if (start >= 0) {
            columns.Add((start, image.Width));
        }

        return columns;
    }

    static int Ink(Bitmap image) {
        var count = 0;

        foreach (var pixel in image.Pixels) {
            if (pixel >= 40) {
                count++;
            }
        }

        return count;
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // tab-size
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A tab inks nothing, so the letters on either side of it are two separate marks.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The claim `TabSizeTests` can only make about the draw list, made about pixels —
    ///         and it has to be made on <see cref="Boxy" />.</b> A tab shaped to `.notdef` puts a box
    ///         in the gap, which is a third inked column between the two letters. In Open Sans there
    ///         is no such box: `.notdef` there is an <i>empty</i> glyph, so a drawn tab inks nothing
    ///         and this assertion is satisfied by an engine that suppressed nothing at all.
    ///     </para>
    ///     <para>
    ///         Written down because that is exactly what happened: a first draft rendered in Open
    ///         Sans and stayed green with `TextRun.Place`'s suppression sabotaged. A picture is only
    ///         an oracle for the thing the face can actually show.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_tab_inks_nothing_between_the_letters_it_separates() {
        Assert.Equal(2, Columns(Render(Boxy, "x\ty")).Count);
        Assert.Equal(2, Columns(Render(Boxy, "x\t\ty")).Count);
    }

    /// <summary>The gap a tab opens is the width the stops say, measured in pixels.</summary>
    /// <remarks>
    ///     <para>
    ///         The closed-form half, and it needs no number written down: a letter after a tab and a
    ///         letter after eight spaces must ink the same column in the same place. ⚠ Compared as
    ///         *positions* rather than as a count, because a column in the wrong place is what a stop
    ///         of the wrong size looks like and a count cannot see it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tab has to be at the start of the line for the spaces to be a valid oracle,
    ///         and getting that wrong is the mistake this branch made twice.</b> Eight spaces equal a
    ///         stop only when the pen is already on one. A first draft compared <c>"x\ty"</c> against
    ///         <c>"x        y"</c> and reported a real difference as a defect: the tab snaps to
    ///         column one at 33.25px while the spaces *add* 33.25px to the <c>x</c>'s own 8.375px, so
    ///         the two land 8px apart and the tab is the one that is right.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_gap_is_where_the_stops_put_it() =>
        Assert.Equal(Columns(Render("        y")), Columns(Render("\ty")));

    /// <summary>A narrower stop moves the letter left, and the picture says so.</summary>
    [Fact]
    public void A_narrower_stop_draws_a_narrower_gap() {
        var wide = Columns(Render("\ty"));
        var narrow = Columns(Render("\ty", "tab-size: 4;"));

        Assert.Single(narrow);
        Assert.True(narrow[0].Start < wide[0].Start, "tab-4 should put the letter to the left of tab-8's");
        Assert.Equal(Columns(Render("    y")), narrow);
    }

    /// <summary>A mid-line tab snaps to the column rather than adding a fixed gap.</summary>
    /// <remarks>
    ///     ⚠ <b>The distinction the spaces oracle above cannot express, and the whole difference
    ///     between a tab and a wide space.</b> Two prefixes of different widths, both inside column
    ///     one, put the letter after the tab in the *same* place — which is what a column is. A
    ///     tab that added eight spaces to the pen would put it in two different places, and both
    ///     would look reasonable in isolation.
    /// </remarks>
    [Fact]
    public void A_mid_line_tab_snaps_to_the_column() {
        var narrow = Columns(Render("x\ty"));
        var wider = Columns(Render("xx\ty"));

        // ⚠ The *last* column of each, not the second. Two `x`s at this size do not touch, so the
        // wider prefix inks two columns of its own — a count is not an index here.
        Assert.Equal(2, narrow.Count);
        Assert.Equal(3, wider.Count);
        Assert.Equal(narrow[^1], wider[^1]);

        // And both prefixes really are inside column one, or the claim above is vacuous.
        Assert.True(wider[^2].End < narrow[^1].Start, "the prefix must not reach the column the tab opens");
    }

    /// <summary>A zero stop draws the two letters side by side, with no gap and no box.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect this feature actually had, seen as a picture.</b> While a non-positive
    ///     stop meant "measure the tab as a glyph", `tab-size: 0` reserved the width of a .notdef box
    ///     that the run then declined to draw — so the letters sat a box apart with nothing between
    ///     them. Two columns still, which is why the draw list could not see it; the *position* is
    ///     what moved.
    /// </remarks>
    [Fact]
    public void A_zero_stop_leaves_no_gap_at_all() =>
        Assert.Equal(Columns(Render("xy")), Columns(Render("x\ty", "tab-size: 0;")));

    // ────────────────────────────────────────────────────────────────────────────────────────
    // hyphens
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A line broken at a soft hyphen has ink where the hyphen goes.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that was missing, asserted as ink.</b> Before the substitution the same
    ///     paragraph broke in the same place and drew nothing at the break, which no assertion about
    ///     where the lines start can distinguish from a hyphen that is there. Compared against the
    ///     same text written with a real hyphen, which is the picture a browser draws.
    /// </remarks>
    [Fact]
    public void A_hyphenated_break_inks_a_hyphen() {
        var soft = Render("sup­ply", width: 44f);
        var hard = Render("sup-ply", width: 44f);
        var none = Render("supply", width: 44f);

        Assert.Equal(Ink(hard), Ink(soft));
        Assert.True(Ink(soft) > Ink(none), "the hyphen is meant to add ink that `supply` alone does not have");
    }

    /// <summary>The mark it adds is the size of a hyphen.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What U+2010 does depends on the face, and "it would have drawn a tofu box" was
    ///         only half right.</b> U+2010 resolves to glyph 0 in both faces committed here. In
    ///         `TestShapeLana` glyph 0 has two contours and is the familiar hollow box; in Open Sans
    ///         it has <b>zero</b> and draws nothing at all. So the substitution the sizing prescribed
    ///         either shows a box or <i>silently reproduces the defect it was meant to fix</i> — and
    ///         in the engine's own interface face it is the silent one, which is the worse of the
    ///         two because it looks like the change never took.
    ///     </para>
    ///     <para>
    ///         Asserted in Open Sans, where the failure is invisibility: the mark must add ink that
    ///         `supply` alone does not have, which U+2010 there does not. ⚠ The box case is
    ///         <b>not</b> separately asserted — `TestShapeLana`'s Latin advances are different enough
    ///         that the 44px box does not break `supply` in the same place, so a second face needs
    ///         its own width and would be measuring the wrap rather than the mark. The upper bound
    ///         here is what stands in for it, and `HyphensTests.The_drawn_hyphen_is_one_the_face_
    ///         actually_has` asks the cmap directly.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_mark_it_adds_is_the_size_of_a_hyphen() {
        var soft = Render("sup­ply", width: 44f);
        var plain = Render("supply", width: 44f);

        // The ink the hyphenated picture has and the unhyphenated one does not is the mark.
        var added = Ink(soft) - Ink(plain);

        // A hyphen is one short bar; a .notdef box outlines most of an em, and an empty glyph adds
        // nothing. At 16px none of the three is near either bound.
        Assert.InRange(added, 1, 60);
    }
}
