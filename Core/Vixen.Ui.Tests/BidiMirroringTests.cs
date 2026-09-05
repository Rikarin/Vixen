// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The two things that stay wrong when every glyph is already in the right order.</summary>
/// <remarks>
///     <para>
///         <b>Doc 09 § Text says "nothing mirrors", and by the time this was written that was two
///         thirds untrue.</b> <c>direction</c> reaches the itemiser, runs are cut on level, and
///         <c>TextLine</c> reorders them by UAX#9 L2 — <c>BidiDirectionTests</c> proves the glyphs
///         land in the right order and <c>DrawListBuilder.Indent</c> already resolved a *declared*
///         <c>start</c> and <c>end</c> against the direction. What was left was two defects that a
///         glyph-order assertion cannot see, because in both of them the glyphs are correct.
///     </para>
///     <para>
///         ⚠ <b>The line is placed in the box by <c>text-align</c>, whose initial value is
///         <c>start</c> — and a missing declaration was read as zero.</b> So a right-to-left
///         paragraph that nobody had written a <c>text-align</c> for, which is every paragraph in a
///         plain interface, sat flush against the *left* edge with correctly-ordered text in it.
///         That is the picture doc 09 describes, arrived at from the opposite direction to the one
///         it names.
///     </para>
///     <para>
///         ⚠ <b>And the hit test walked the runs in the order they are read, over pens that are
///         stored in the order they are drawn.</b> On any line that changes direction those are not
///         the same order, so the first run whose pen span reaches the point is the one that reads
///         first rather than the one under the cursor. Asserted here as a round trip —
///         <c>CaretPositionAt(CaretOffset(i))</c> back to the same x — because a hit test that
///         returns <i>a</i> character passes every structural assertion, and only which one is wrong.
///     </para>
/// </remarks>
public class BidiMirroringTests {
    const float Tolerance = 0.01f;

    static readonly FontFace Lana = LoadFont("TestShapeLana.ttf", "lana");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    static UiDocument Documented(string declarations, string text) {
        var document = new UiDocument(400f, 200f);

        document.Fonts.Register("Test", Lana);
        document.Load(
            "root { width: 400px; height: 200px; align-items: flex-start; } "
            + $"label {{ font-family: Test; {declarations} }}"
        );

        var label = document.Root.Add("label");
        label.Text = text;

        document.Update();
        document.Draw();

        return document;
    }

    /// <summary>The left edge of each drawn line, in the order the lines are stacked.</summary>
    /// <remarks>
    ///     ⚠ <b>Grouped by baseline rather than counted as commands.</b> A line is one command per
    ///     run, and a right-to-left line has more runs than the left-to-right one it is being
    ///     compared with — the leading space is a level of its own. Counting commands would compare
    ///     two different things and call the difference a result.
    /// </remarks>
    static List<float> LineLefts(UiDocument document) =>
        [
            .. document.Drawing.Commands
                .Where(static command => command.Kind == DrawCommandKind.Text)
                .GroupBy(static command => command.Y)
                .OrderBy(static line => line.Key)
                .Select(static line => line.Min(static command => command.X))
        ];

    /// <summary>
    ///     A wrapped right-to-left paragraph with no <c>text-align</c> of its own is flush with the
    ///     right edge, because the initial value of <c>text-align</c> is <c>start</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Judged by comparing two lines of different lengths rather than by one number.</b> A
    ///     single line flush left and a single line flush right are both "somewhere in the box", and
    ///     an expectation written as a pixel would have to be recomputed whenever the fixture font
    ///     changed. Two lines that do not measure the same have coincident <i>right</i> edges under
    ///     <c>start</c> and coincident <i>left</i> edges under the bug, and that is a difference no
    ///     font metric can wash out.
    /// </remarks>
    [Fact]
    public void An_rtl_paragraph_that_declares_no_alignment_is_flush_with_the_right_edge() {
        using var mirrored = Documented("width: 60px; direction: rtl;", "AA BBBB");
        using var upright = Documented("width: 60px; direction: ltr;", "AA BBBB");

        var right = LineLefts(mirrored);
        var left = LineLefts(upright);

        // The fixture has to wrap, or the whole assertion is about one line and says nothing.
        Assert.Equal(2, right.Count);
        Assert.Equal(2, left.Count);

        // Left-to-right: the lines start together and end wherever they end.
        Assert.Equal(left[0], left[^1], Tolerance);

        // ⚠ Right-to-left: they must *not*, because the shorter line is pushed across the slack. This
        // is the assertion that was false, and it is false for exactly the reason the alignment was
        // reading a missing declaration as `left`.
        Assert.True(
            Math.Abs(right[0] - right[^1]) > Tolerance,
            "the rtl lines start at the same x, so the paragraph is still flush left"
        );
    }

    /// <summary>
    ///     Every caret on a line that changes direction hit-tests back to where it was drawn.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The round trip is on the <i>x</i> and not on the index, and that is not a
    ///         weakening.</b> In bidi text an index alone is not recoverable from a point — the caret
    ///         after the last Latin character and the caret at the end of an <c>rtl</c> line are the
    ///         same pixel — so a test asserting the index would be asserting which of two correct
    ///         answers this implementation happens to give. The x is recoverable, and the pair that
    ///         comes back is exactly the pair that puts it there.
    ///     </para>
    ///     <para>
    ///         <c>" AB"</c> under <c>direction: rtl</c> is the smallest string that produces the
    ///         defect: the leading space takes the base level 1 and the Latin takes level 2, so the
    ///         line is two runs and L2 draws the second one first. A walk in logical order therefore
    ///         meets the space's run before the Latin one it is drawn to the right of.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_caret_on_a_reordered_line_hit_tests_back_to_where_it_was_drawn() {
        using var document = Documented("width: 200px; direction: rtl;", " AB");

        var line = Assert.Single(document.Root.Children[0].Block()!.Lines);

        // The fixture has to have reordered, or every assertion below holds trivially.
        Assert.True(line.Runs.Length >= 2, $"the line has {line.Runs.Length} run(s) and needs two");
        Assert.True(line.PenOf(0) > line.PenOf(1), "the runs were not reordered, so nothing here is a bidi test");

        foreach (var affinity in new[] { CaretAffinity.Upstream, CaretAffinity.Downstream }) {
            for (var index = 0; index <= 3; index++) {
                var x = line.CaretOffset(index, affinity);
                var found = line.CaretPositionAt(x);

                Assert.Equal(x, line.CaretOffset(found.Index, found.Affinity), Tolerance);
            }
        }
    }

    /// <summary>A click at the left of that line yields a caret that is drawn at the left of it.</summary>
    /// <remarks>
    ///     <para>
    ///         The round trip above is the general oracle; this is the one sentence a reader can
    ///         check against the picture. Under <c>rtl</c> the Latin run is drawn first, so a point a
    ///         fraction into the line is inside <c>A</c> — and the caret it names must be drawn there
    ///         too, rather than at the far end beside the space that reads first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Stated as "which half of the line" rather than as an index, because the index is
    ///         not a discriminator.</b> A walk in logical order lands in the space's run with a
    ///         negative distance, which clamps to that run's <i>trailing</i> edge — and the space's
    ///         trailing edge is index 1, the same index the correct answer gives. The two answers are
    ///         the same number at opposite ends of the line, which is exactly the shape of bug that
    ///         survives an assertion on the number.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_click_at_the_left_of_a_reordered_line_lands_on_the_glyph_drawn_there() {
        using var document = Documented("width: 200px; direction: rtl;", " AB");

        var line = Assert.Single(document.Root.Children[0].Block()!.Lines);
        var latin = line.Runs.First(static run => !run.IsRightToLeft);

        // A fiftieth of the way into the first glyph. A hit test answers with the *nearest* caret, so
        // a point at the middle of `A` is legitimately the caret after it.
        var inside = line.PenOf(line.Runs.IndexOf(latin)) + (latin.Width * 0.02f);

        var found = line.CaretPositionAt(inside);
        var where = line.CaretOffset(found.Index, found.Affinity);

        Assert.True(
            where < line.Width * 0.5f,
            $"a click at {inside} on a {line.Width}-wide line put the caret at {where}"
        );
    }
}
