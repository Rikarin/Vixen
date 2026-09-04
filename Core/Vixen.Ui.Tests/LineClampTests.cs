// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Layout;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>-webkit-line-clamp</c>: the budget, the height it reports, and the marker.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the one truncation in this engine that happens in the <i>measure</i> path,
///         and the reason is the whole feature.</b> An ellipsis changes the picture and nothing else
///         — the element is as wide as it always was, which is what makes its parent shrink it and
///         is why <c>UiElement.Ellipsized</c> runs at paint. A clamp changes <i>how many lines there
///         are</i>, so it changes the height, so a budget applied after layout would reserve room
///         for lines nobody draws and leave a hole under the text.
///         <see cref="A_clamped_block_measures_the_lines_it_kept" /> is that assertion.
///     </para>
///     <para>
///         ⚠ <b>The count and the marker are applied in different passes on purpose.</b>
///         <c>Block</c> drops lines and nothing else, so every line it returns is still a whole
///         substring of the text and <c>TextLine.Start</c>, the caret and the selection go on
///         meaning what they mean. The ellipsis on the last kept line is put there by
///         <c>Ellipsized</c>, at paint, like every other ellipsis in the file.
///     </para>
/// </remarks>
public class LineClampTests {
    const float Tolerance = 0.01f;
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    // Eight short words at 16px in a 60px box is comfortably more than three lines, so a clamp of
    // three has something to drop and a clamp of nine does not.
    const string Paragraph = "one two three four five six seven eight";

    static UiDocument Documented(string label) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);
        document.Load($"root {{ width: 400px; height: 300px; align-items: flex-start; }} label {{ {label} }}");

        return document;
    }

    static UiElement Labelled(UiDocument document, string text = Paragraph) {
        var element = document.Root.Add("label");
        element.Text = text;
        document.Update();

        return element;
    }

    static string Drawn(TextLine line) {
        var text = string.Empty;

        foreach (var run in line.Runs) {
            text += run.Shaped.Text;
        }

        return text;
    }

    [Fact]
    public void The_fixture_wraps_to_more_lines_than_the_clamp_keeps() {
        using var document = Documented("width: 60px; font-size: 16px;");

        Assert.True(
            Labelled(document).Block()!.Lines.Length > 3,
            "the paragraph has to overflow the clamp for anything below to say anything"
        );
    }

    [Fact]
    public void A_clamp_keeps_that_many_lines() {
        using var document = Documented("width: 60px; font-size: 16px; -webkit-line-clamp: 3;");

        Assert.Equal(3, Labelled(document).Block()!.Lines.Length);
    }

    [Fact]
    public void A_clamp_larger_than_the_paragraph_drops_nothing() {
        using var plain = Documented("width: 60px; font-size: 16px;");
        using var clamped = Documented("width: 60px; font-size: 16px; -webkit-line-clamp: 99;");

        Assert.Equal(Labelled(plain).Block()!.Lines.Length, Labelled(clamped).Block()!.Lines.Length);
    }

    [Fact]
    public void None_is_the_opt_out() {
        using var plain = Documented("width: 60px; font-size: 16px;");
        using var clamped = Documented("width: 60px; font-size: 16px; -webkit-line-clamp: none;");

        Assert.Equal(Labelled(plain).Block()!.Lines.Length, Labelled(clamped).Block()!.Lines.Length);
    }

    /// <summary>
    ///     ⚠ <b>The assertion the sizing called unresolved.</b> A clamped block reports the height of
    ///     the lines it kept, which is what a parent lays out around — and it is the first thing in
    ///     this engine whose measured height is not the height of all its text.
    /// </summary>
    [Fact]
    public void A_clamped_block_measures_the_lines_it_kept() {
        using var plain = Documented("width: 60px; font-size: 16px;");
        using var clamped = Documented("width: 60px; font-size: 16px; -webkit-line-clamp: 3;");

        var whole = Labelled(plain).Block()!;
        var cut = Labelled(clamped).Block()!;

        var expected = whole.TopOf(3);

        Assert.True(whole.Height > cut.Height, $"the clamped block is shorter: {cut.Height} vs {whole.Height}");
        Assert.Equal(expected, cut.Height, Tolerance);
    }

    /// <summary>And the layout tree agrees, which is the half a block-level assertion cannot see.</summary>
    [Fact]
    public void The_element_is_laid_out_at_the_clamped_height() {
        using var plain = Documented("width: 60px; font-size: 16px;");
        using var clamped = Documented("width: 60px; font-size: 16px; -webkit-line-clamp: 3;");

        var tall = Labelled(plain).Height;
        var short_ = Labelled(clamped).Height;

        Assert.True(short_ < tall, $"the clamped element is shorter: {short_} vs {tall}");
    }

    /// <summary>
    ///     ⚠ <b>The lines that survive are still the text.</b> The marker is not applied in this pass,
    ///     so a caret and a selection read a block whose every line is a whole substring — which is
    ///     what stops a clamp doing to the caret what <c>text-transform</c> was split off to avoid.
    /// </summary>
    [Fact]
    public void The_kept_lines_are_untouched_substrings_of_the_text() {
        using var document = Documented("width: 60px; font-size: 16px; -webkit-line-clamp: 3;");
        var block = Labelled(document).Block()!;

        foreach (var line in block.Lines) {
            Assert.Equal(Paragraph.Substring(line.Start, line.Length), Drawn(line));
        }
    }

    /// <summary>
    ///     ⚠ <b>The marker lands on a line that fits, which is what makes this different from an
    ///     ellipsis.</b> A width test before the marker would silently drop it on every clamped
    ///     paragraph whose last kept line happens to be short — and most are, because a wrap point
    ///     is where the next word did not fit.
    /// </summary>
    [Fact]
    public void The_last_kept_line_is_marked_although_it_fits() {
        using var document = Documented("width: 60px; font-size: 16px; -webkit-line-clamp: 3;");
        var element = Labelled(document);

        var drawn = element.Ellipsized(60f)!;

        Assert.Equal(3, drawn.Lines.Length);
        Assert.EndsWith("…", Drawn(drawn.Lines[^1]), StringComparison.Ordinal);

        // And only the last one: the two above it are the block's own lines, shared rather than
        // rebuilt, which is also what keeps a clamped paragraph from re-shaping every frame.
        Assert.DoesNotContain('…', Drawn(drawn.Lines[0]));
    }

    /// <summary>
    ///     A clamp that dropped nothing marks nothing — the class is written on the day the text
    ///     might be long, and a short one has to look exactly as it would without the class.
    /// </summary>
    [Fact]
    public void A_clamp_that_kept_everything_marks_nothing() {
        using var document = Documented("width: 200px; font-size: 16px; -webkit-line-clamp: 9;");
        var element = Labelled(document);

        Assert.Same(element.Block(), element.Ellipsized(200f));
    }

    /// <summary>
    ///     ⚠ <b>The clamp is in the block's cache key, and the element's text does not change when it
    ///     arrives.</b> A key that missed it is a paragraph that measured five lines and draws three:
    ///     the box stays two lines too tall and the gap reads as a margin nobody wrote.
    /// </summary>
    [Fact]
    public void Changing_the_clamp_rebuilds_the_block() {
        using var document = Documented("width: 60px; font-size: 16px;");
        var element = Labelled(document);
        var before = element.Block()!.Lines.Length;

        document.Load(
            "root { width: 400px; height: 300px; align-items: flex-start; }"
            + " label { width: 60px; font-size: 16px; -webkit-line-clamp: 3; }"
        );

        document.Update();

        Assert.True(before > 3, "the fixture overflowed to begin with");
        Assert.Equal(3, element.Block()!.Lines.Length);
    }

    /// <summary>
    ///     It inherits, for <c>text-overflow</c>'s reason: the class is written on the card and the
    ///     glyphs are in a child.
    /// </summary>
    [Fact]
    public void It_inherits_to_the_child_that_holds_the_text() {
        using var document = Documented("width: 60px; font-size: 16px;");
        document.Load(
            "root { width: 400px; height: 300px; align-items: flex-start; -webkit-line-clamp: 2; }"
            + " label { width: 60px; font-size: 16px; }"
        );

        Assert.Equal(2, Labelled(document).Block()!.Lines.Length);
    }

    /// <summary>
    ///     ⚠ <b>The two features compose rather than one shadowing the other.</b> A clamped paragraph
    ///     whose last kept line is <i>also</i> too wide has to be cut for width and marked for the
    ///     clamp, and one `…` is what both of them want.
    /// </summary>
    [Fact]
    public void A_clamp_and_an_ellipsis_produce_one_marker() {
        using var document = Documented(
            "width: 60px; font-size: 16px; text-overflow: ellipsis; -webkit-line-clamp: 2;"
        );

        var drawn = Labelled(document).Ellipsized(60f)!;
        var last = Drawn(drawn.Lines[^1]);

        Assert.Equal(2, drawn.Lines.Length);
        Assert.EndsWith("…", last, StringComparison.Ordinal);
        Assert.Equal(1, last.Count(character => character == '…'));
    }
}
