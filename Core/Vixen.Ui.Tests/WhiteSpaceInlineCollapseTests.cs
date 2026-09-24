// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     <c>white-space: pre-line</c> across the boundary between two <c>display: inline</c> elements —
///     CSS Text § 4.1.1 collapsing and § 4.1.3's phase II at the edges of a formatting context (#1363).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every number here is Chrome's.</b> <c>Oracle/inline-collapse.html</c> lays each case
///         out as spans in a <c>pre-line</c> block and reads the block's max-content width;
///         Chrome 153.0 (headless, the OpenSans face this assembly embeds) gives
///         <c>["foo ", " bar"]</c> 54.016, the single span <c>"foo bar"</c> 54.016, <c>["foo", " bar"]</c>
///         and <c>["foo ", "bar"]</c> 54.016, <c>["   ab"]</c> and <c>["ab   "]</c> 18.688 against
///         <c>"ab"</c>'s 18.688, <c>["foo ", "   ", " bar"]</c> 54.016, and <c>["foo\n", " bar"]</c>
///         25.219, the same as <c>"foo\nbar"</c>. This engine shapes that face to Chrome's advances to
///         three decimals, so a disagreement here is a collapsing disagreement.
///     </para>
///     <para>
///         ⚠ <b>Measured off the laid-out boxes, not off one element's block.</b> What the defect
///         changed was the width of two elements together — each measured its own text alone, so
///         <c>foo␠</c> and <c>␠bar</c> were each right and the line was a space too wide. The distance
///         from the first box's left edge to the last one's right edge is the line's advance, and it
///         is also what proves the layout was told: a block rebuilt and a box left at its old size
///         would pass an assertion about the block.
///     </para>
///     <para>
///         ⚠ <b>Each case has a control under <c>normal</c></b>, which in this engine preserves every
///         space — <c>WhiteSpacePreLineTests</c>' premise — so a pair that measures narrower under the
///         declaration is the collapse and not a box that happened to be too small.
///     </para>
/// </remarks>
public class WhiteSpaceInlineCollapseTests {
    const float Tolerance = 0.05f;

    const string PreLine = "white-space: pre-line;";

    const string Normal = "white-space: normal;";

    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>A block container of inline labels, one per string, laid out.</summary>
    static (UiDocument Document, UiElement[] Labels) Line(string label, params string[] parts) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root      { width: 800px; height: 300px; }
              container { display: block; width: 800px; }
              label     { font-family: Test; font-size: 16px; line-height: 20px; display: inline; {{label}} }
              label.pre { white-space: normal; }
              """
        );

        var container = document.Root.Add("container");
        var labels = parts.Select(part => {
            var element = container.Add("label");
            element.Text = part;
            return element;
        }).ToArray();

        document.Update();
        return (document, labels);
    }

    /// <summary>How far the labels reach along their line, as their boxes were measured.</summary>
    /// <remarks>
    ///     ⚠ <b>The measured widths, summed, and the boxes held to them.</b> Layout rounds each box up
    ///     to a whole pixel, so the distance across the boxes is up to a pixel per label wider than the
    ///     text and cannot be compared with Chrome to three decimals. So the sum is of the blocks, and
    ///     each box is asserted to be its block rounded up and to start where the one before it ends —
    ///     which is what says the layout was told about a block that changed because a neighbour did.
    /// </remarks>
    static float Advance(UiElement[] labels) {
        var total = 0f;

        for (var i = 0; i < labels.Length; i++) {
            var width = labels[i].Block()?.Width ?? 0f;

            Assert.Equal(MathF.Ceiling(width - 0.0001f), labels[i].Bounds.Width, 0.001f);

            if (i > 0) {
                Assert.Equal(labels[i - 1].Bounds.Right, labels[i].Bounds.X, 0.001f);
            }

            total += width;
        }

        return total;
    }

    static float Advance(string label, params string[] parts) => Advance(Line(label, parts).Labels);

    /// <summary>
    ///     ⚠ <b>The defect as filed</b>: <c>foo␠</c> and <c>␠bar</c> in two elements are one space
    ///     between two words, exactly as they are in one — § 4.1.1 removes the second of two
    ///     collapsible spaces "even outside the boundary of the inline containing that space".
    /// </summary>
    [Fact]
    public void Two_pre_line_siblings_draw_one_space_between_them() {
        var one = Advance(PreLine, "foo bar");

        Assert.Equal(54.016f, one, Tolerance);
        Assert.True(Advance(Normal, "foo ", " bar") > one + 1f, "the control has to keep both spaces or this measures nothing");
        Assert.Equal(one, Advance(PreLine, "foo ", " bar"), Tolerance);
    }

    /// <summary>
    ///     The halves that must not move: a space on only one side of the boundary is the one space
    ///     between the words, and it survives. An implementation that trimmed every inline leaf's
    ///     leading run would join <c>foo</c> and <c>␠bar</c> into one word.
    /// </summary>
    [Fact]
    public void A_space_on_one_side_of_the_boundary_is_kept() {
        Assert.Equal(54.016f, Advance(PreLine, "foo", " bar"), Tolerance);
        Assert.Equal(54.016f, Advance(PreLine, "foo ", "bar"), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A space between two words in two elements is not hung, under any value</b> — the
    ///     defect under this issue's defect. <c>TextLayout</c> left every paragraph's trailing white
    ///     space out of its measure (#1211's half of CSS Text § 5.2, right at the end of a line box),
    ///     so <c>foo␠</c> measured as <c>foo</c> and the next element's <c>bar</c> was laid against
    ///     it: <c>foobar</c>, 51 pixels of boxes where Chrome gives 54.016. It is also why
    ///     <c>foo␠</c> beside <c>␠bar</c> never showed two spaces here — the first was hung.
    /// </summary>
    [Theory]
    [InlineData(PreLine)]
    [InlineData(Normal)]
    public void A_space_before_a_word_in_the_next_element_takes_its_width(string label) {
        Assert.Equal(54.016f, Advance(label, "foo ", "bar"), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A preserved space is not a collapsible one.</b> A <c>normal</c> label ending in a space
    ///     before a <c>pre-line</c> one starting with a space draws both: § 4.1.1 collapses a
    ///     collapsible space that follows another <i>collapsible</i> space, and this engine preserves
    ///     under <c>normal</c>.
    /// </summary>
    [Fact]
    public void A_preserved_space_before_the_boundary_does_not_collapse_the_one_after_it() {
        var (document, mixed) = Line(PreLine, "foo ", " bar");

        mixed[0].AddClass("pre");
        document.Update();

        Assert.True(Advance(mixed) > 54.016f + 1f, $"a preserved space swallowed the collapsible one after it: {Advance(mixed)}");
    }

    /// <summary>
    ///     ⚠ <b>The pin this issue inverts</b> (<c>WhiteSpacePreLineTests.An_inline_leaf_keeps_its_leading_space_only_where_it_shares_a_line</c>, now <c>…only_after_content</c>):
    ///     an inline leaf with nothing before it in its formatting context starts the container's
    ///     first line, so its leading run is phase II's to remove, and one with nothing after it ends
    ///     the last.
    /// </summary>
    [Fact]
    public void An_inline_leaf_at_either_end_of_its_container_drops_the_run_there() {
        var bare = Advance(PreLine, "ab");

        Assert.Equal(18.688f, bare, Tolerance);
        Assert.True(Advance(Normal, "   ab") > bare + 1f, "the control has to draw the spaces");
        Assert.Equal(bare, Advance(PreLine, "   ab"), Tolerance);
        Assert.Equal(bare, Advance(PreLine, "ab   "), Tolerance);
    }

    /// <summary>
    ///     An element of nothing but spaces between two others is one space — which, collapsed onto
    ///     the space before it, is no extra width at all — and one at the end of the line is nothing.
    /// </summary>
    [Fact]
    public void A_label_of_only_spaces_between_two_others_adds_nothing_to_one_space() {
        Assert.Equal(54.016f, Advance(PreLine, "foo ", "   ", " bar"), Tolerance);
        Assert.Equal(Advance(PreLine, "foo"), Advance(PreLine, "foo ", "   "), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A segment break at the end of one element starts the next element's text on a new
    ///     line</b>, so the run after it is removed twice over: it is beside a segment break
    ///     (§ 4.1.1) and at the start of a line (§ 4.1.3).
    /// </summary>
    [Fact]
    public void A_run_after_a_segment_break_in_the_previous_element_is_removed() {
        var (_, labels) = Line(PreLine, "foo\n", " bar");
        var bar = Line(PreLine, "bar").Labels[0].Block()!.Lines[0].Width;

        Assert.Equal(bar, labels[1].Block()!.Lines[0].Width, Tolerance);
    }

    /// <summary>
    ///     A block-level box before an inline element ends the lines before it, so the element begins
    ///     a line and loses its leading run; an atomic inline sits on the line like a word, so the
    ///     element keeps it. Chrome 153: <c>18.688</c> after a <c>div</c>, <c>22.844</c> — <c>␠ab</c> —
    ///     after an <c>inline-block</c>.
    /// </summary>
    [Theory]
    [InlineData("block", 18.688f)]
    [InlineData("inline-block", 22.844f)]
    public void A_box_before_the_element_decides_by_whether_it_ends_the_line(string display, float chrome) {
        var document = new UiDocument(900f, 300f);
        document.Fonts.Register("Test", Font);

        document.Load(
            $$"""
              root      { width: 800px; height: 300px; }
              container { display: block; width: 800px; }
              before    { font-family: Test; font-size: 16px; line-height: 20px; display: {{display}}; }
              label     { font-family: Test; font-size: 16px; line-height: 20px; display: inline; {{PreLine}} }
              """
        );

        var container = document.Root.Add("container");
        container.Add("before").Text = "x";

        var label = container.Add("label");
        label.Text = "   ab";
        document.Update();

        Assert.Equal(chrome, label.Block()!.Width, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>Decided by a neighbour's text, so a neighbour's text change has to reach this
    ///     element's box</b> — which the layout tree does not do by itself: a node is re-measured
    ///     when it or its style changes, and here neither did. Without the neighbour being dirtied the
    ///     block would rebuild on the next draw and the box would keep the old width, which is a
    ///     picture drawn over a layout that disagrees with it.
    /// </summary>
    [Fact]
    public void Changing_the_text_before_the_boundary_moves_the_box_after_it() {
        var (document, labels) = Line(PreLine, "foo ", " bar");

        Assert.Equal(54.016f, Advance(labels), Tolerance);

        labels[0].Text = "foo";
        document.Update();

        Assert.Equal(54.016f, Advance(labels), Tolerance);

        labels[0].Text = "foo ";
        document.Update();

        Assert.Equal(54.016f, Advance(labels), Tolerance);
    }

    /// <summary>
    ///     The style half of the same dependency: turning the first element's <c>white-space</c> away
    ///     from <c>pre-line</c> preserves its space, so the second element's leading space comes back
    ///     — though nothing about the second element changed.
    /// </summary>
    [Fact]
    public void Changing_the_white_space_before_the_boundary_moves_the_box_after_it() {
        var (document, labels) = Line(PreLine, "foo ", " bar");
        var collapsed = Advance(labels);

        labels[0].AddClass("pre");
        document.Update();

        var preserved = Advance(labels);

        Assert.Equal(54.016f, collapsed, Tolerance);
        Assert.True(preserved > collapsed + 1f, $"the second label kept its collapsed width after the first stopped collapsing: {preserved}");

        labels[0].RemoveClass("pre");
        document.Update();

        Assert.Equal(54.016f, Advance(labels), Tolerance);
    }
}
