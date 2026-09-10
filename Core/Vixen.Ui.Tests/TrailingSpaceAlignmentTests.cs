// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     Where a line's trailing white space goes, and the two different answers CSS gives.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/1211">#1211</a> is half right and the
///         wrong half is the half it names.</b> It reports that a right-aligned label ending in two
///         spaces draws 8.31 points short of the box's right edge, calls that the ragged edge
///         <c>LineWrapper.Width</c>'s trim exists to prevent, and says <c>text-align</c> sees the
///         trimmed width in every browser. Measured in Chrome 152 — a 200px right-aligned box, 16px
///         sans-serif, <c>white-space: pre-wrap</c> — the glyphs of <c>AB</c> end at 200.00 and the
///         glyphs of <c>AB</c>-two-spaces end at <b>191.11</b>: shifted left by 8.891, which is
///         exactly the two spaces. <c>pre</c> and <c>break-spaces</c> agree with it to the pixel.
///         Only <c>normal</c> puts the glyphs flush, and there for a different reason — the spaces
///         are <i>removed</i> by white-space collapsing rather than hung.
///     </para>
///     <para>
///         ⚠ <b>Chrome hangs preserved trailing white space at a soft wrap and nowhere else.</b> The
///         same paragraph broken across lines puts the wrapped line's spaces outside the box —
///         <c>ab cd</c> ends at 60.00 in a 60px box and the two spaces occupy [60.00, 68.89] — while
///         a line ending at the end of the text, and a line ending at a forced break, both keep them
///         inside and shift the glyphs. Vixen already hangs at a soft wrap and its unwrapped path
///         already keeps them, so <b>the picture #1211 measured is the browser's picture</b>.
///     </para>
///     <para>
///         <b>What is left after that is real, and it is the other question.</b> An intrinsic measure
///         never counts hanging white space (CSS Text § 5.2): in the same Chrome, a shrink-to-fit box
///         around <c>AB</c> and one around <c>AB</c>-two-spaces are both 21.344 wide, and one around
///         nothing but spaces is 0.000. Vixen's unwrapped path measured the block from the same
///         untrimmed number it aligns by, so every label ending in a space was that much too wide.
///         That is what <see cref="TextLine.Trimmed" /> separates and what the second test below
///         pins.
///     </para>
///     <para>
///         ⚠ <b>Both tests are pinned to the trim in <c>LineWrapper.Width</c> and to
///         <c>TextLine.Hung</c>, which is what a sabotage has to break to turn them red</b> — the
///         first goes red if the unwrapped line stops carrying its spaces, the second if the block
///         starts counting them. And whatever implements <c>white-space: break-spaces</c> turns the
///         second one over: that value stops the hanging outright. See
///         <c>WhiteSpaceBreakSpacesTests</c> and <c>Rikarin/Vixen#249</c>.
///     </para>
/// </remarks>
public class TrailingSpaceAlignmentTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    static UiDocument Documented(string css) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);
        document.Load(css);

        return document;
    }

    /// <summary>A trailing space stays in the line box, so a right-aligned line shifts by it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This asserts the shift rather than its absence, which is the correction #1211
    ///         needs.</b> The two spaces are inside the line box because the line ended where the
    ///         text did and not at a soft wrap, so <c>text-align</c> distributes the slack that is
    ///         left after them — Chrome 152 to the pixel, and the picture the issue called a bug.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The shift is compared against a measured pair of spaces rather than a constant</b>,
    ///         so the assertion says "by exactly the spaces" instead of "by 8.31 at this size in this
    ///         face". A third label holding the two spaces alone is what measures them, and its own
    ///         line width is the untrimmed one for the same reason the second label's is.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_trailing_space_stays_in_the_line_box_and_the_alignment_pays_for_it() {
        using var document = Documented(
            """
            root { width: 900px; height: 300px; align-items: flex-start; }
            label { font-family: Test; font-size: 16px; width: 200px; text-align: right; }
            """
        );

        var bare = document.Root.Add("label");
        var trailing = document.Root.Add("label");
        var spaces = document.Root.Add("label");

        bare.Text = "AB";
        trailing.Text = "AB  ";
        spaces.Text = "  ";

        document.Update();
        document.Draw();

        var commands = document.Drawing.Commands
            .Where(static c => c.Kind == DrawCommandKind.Text)
            .ToArray();

        Assert.Equal(3, commands.Length);

        // The control: the bare label really is pushed across the box, so a pair that agreed because
        // neither was aligned could not pass.
        var flush = commands[0].X - bare.AbsoluteLeft;
        Assert.True(flush > 100f, $"the bare label is at {flush}, so nothing aligned it");

        var pair = spaces.Block()!.Lines[0].Width;
        Assert.True(pair > 0f, $"the two spaces measure {pair}, so the shift below is not a measure");

        Assert.Equal(flush - pair, commands[1].X - trailing.AbsoluteLeft, 0.01f);
    }

    /// <summary>And it does not widen the box the text is measured into.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The other question, and the one Vixen was answering with the first one's
    ///         number.</b> <c>TextLayout.Width</c> is what a shrink-to-fit box is sized from, and CSS
    ///         Text § 5.2 leaves hanging white space out of an intrinsic measure whatever the line
    ///         did with it — Chrome 152 makes both of these 21.344 and a box of nothing but spaces
    ///         0.000.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The wrapped paragraph has always answered this way</b>, which is what says the
    ///         rule is the engine's rather than this test's opinion: a line the wrapper broke carries
    ///         <c>LineWrapper.Width</c>'s trimmed measure and always has. Only the unwrapped path
    ///         disagreed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The line's own width is not asserted here and cannot be, which is worth saying
    ///         because it looks like the obvious extra check.</b> A shrink-to-fit box is laid out
    ///         twice: the second pass offers the paragraph the width the first one measured, the
    ///         untrimmed run no longer fits in it, and the line comes back through the wrapper
    ///         carrying the trimmed measure after all. That costs nothing — a box wrapped to its own
    ///         text has no alignment slack for the difference to show in — and it is why the first
    ///         test declares a width rather than letting the label find one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_trailing_space_does_not_widen_the_box_the_text_is_measured_into() {
        using var document = Documented(
            """
            root { width: 900px; height: 300px; align-items: flex-start; }
            label { font-family: Test; font-size: 16px; }
            """
        );

        var bare = document.Root.Add("label");
        var trailing = document.Root.Add("label");

        bare.Text = "AB";
        trailing.Text = "AB  ";

        document.Update();

        // The control: the labels are shrink-to-fit and the width really did come from the text.
        Assert.True(bare.Width > 1f, $"the bare label is {bare.Width} wide, so nothing measured it");

        Assert.Equal(bare.Block()!.Width, trailing.Block()!.Width, 0.01f);
        Assert.Equal(bare.Width, trailing.Width, 0.01f);
    }
}
