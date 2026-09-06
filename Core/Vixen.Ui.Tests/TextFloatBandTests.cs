// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     CSS 2.1 §9.5's staircase: a paragraph beside a float has a width per line, not a width.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What was missing here was the question and not the answer, which is why this took a
///         band query rather than a second text wrapper.</b> §9.5 shortens the LINE BOXES beside a
///         float and leaves the block box full width, so a text leaf's measured size is one rectangle
///         either way and the staircase lives inside it. <c>LayoutTree.ContentBands</c> is what a
///         measure function can now ask; <c>UiElement.Block</c> breaks one line per band and then
///         everything that is left in one pass, so a paragraph pays for the lines that really are
///         beside a float and no more.
///     </para>
///     <para>
///         <b>The geometry was read out of Chrome 148.0.7778.280</b> over <c>http://localhost</c>: a
///         300-wide <c>display: flow-root</c>, a 100×60 left float and a paragraph at
///         <c>line-height: 20px</c>. The paragraph's own rectangle is <c>[0, 0, 300 × 80]</c> — the
///         box is not narrowed — the first three line rectangles start at <c>x = 100</c> with 200 to
///         run in, and the fourth, whose top is the float's bottom edge, starts at <c>x = 0</c>.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately not compared with Chrome is <i>which characters</i> each line
///         ends on.</b> That is a claim about shaping and about a font both sides have, which this
///         repository does not have with any browser yet — see <c>#257</c>, where the browser-read
///         break positions are and what has to agree before one of them is usable. So the
///         break positions are pinned against this store's own answer at a stated width instead:
///         a line in a 200-point band ends where a line in a 200-point box ends, which is the
///         property that would break if the band were an approximation of one.
///     </para>
/// </remarks>
public class TextFloatBandTests {
    const float Tolerance = 0.01f;

    const string Paragraph =
        "aa bb cc dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr ss tt uu vv ww xx yy zz ab cd ef gh ij kl mn op qr st uv wx yz";

    static readonly FontFace Font = LoadFont();

    /// <summary>The paragraph's lines are inset by the float and shortened by it.</summary>
    [Fact]
    public void A_paragraph_beside_a_left_float_starts_after_it_on_every_line_it_crosses() {
        using var document = Beside(floated: true);
        var label = Paragraphed(document);
        var block = label.Block()!;

        // Chrome: the block box is the container's full width at its origin. §9.5 moves line boxes
        // and no block box, which is why one measured rectangle was never the obstacle.
        Assert.Equal(0f, label.Left, Tolerance);
        Assert.Equal(300f, label.Width, Tolerance);

        Assert.True(block.Lines.Length >= 4, $"expected the paragraph to step out, got {block.Lines.Length} lines");

        for (var i = 0; i < 3; i++) {
            Assert.Equal(100f, block.Lines[i].Offset, Tolerance);
            Assert.True(block.Lines[i].Width <= 200f + Tolerance, $"line {i} is {block.Lines[i].Width} wide in a 200 band");
        }

        // ⚠ And the offset is on the line rather than applied by whoever draws it, so the caret, the
        // selection band and the draw list get it for free — `PenOf` is what all three go through.
        Assert.Equal(100f, block.Lines[0].PenOf(0), Tolerance);
    }

    /// <summary>The line whose top is the float's bottom edge steps back out to the full width.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the half that a single narrowed width cannot express, and the reason the
    ///     assertion is <c>&gt; 200</c> rather than an equality.</b> A paragraph wrapped to the band
    ///     throughout would look right — every line short, none overflowing — and would leave a
    ///     line's worth of text tucked under a float that is no longer beside it. A line wider than
    ///     the band is only possible if the band went away.
    /// </remarks>
    [Fact]
    public void The_line_below_the_float_is_wider_than_the_band_above_it() {
        using var document = Beside(floated: true);
        var block = Paragraphed(document).Block()!;

        Assert.Equal(0f, block.Lines[3].Offset, Tolerance);
        Assert.True(block.Lines[3].Width > 200f, $"the step-out line is {block.Lines[3].Width} wide, so it never stepped out");
    }

    /// <summary>A line in a 200-point band ends where a line in a 200-point box ends.</summary>
    /// <remarks>
    ///     ⚠ <b>The band is a width and not a hint, which is what this pins.</b> An implementation
    ///     that shifted a finished line rather than wrapping to the band would break in the same
    ///     places as a 300-wide box and hang 100 points off the right; one that subtracted the float
    ///     from the paragraph's width would break here correctly and never step out. Only wrapping
    ///     each line to its own band gives both answers at once.
    /// </remarks>
    [Fact]
    public void The_lines_beside_the_float_break_where_a_box_of_the_bands_width_breaks() {
        using var banded = Beside(floated: true);
        var bandedBlock = Paragraphed(banded).Block()!;

        using var narrow = new UiDocument(400f, 300f);
        narrow.Fonts.Register("Test", Font);
        narrow.Load("root { width: 200px; height: 300px; display: block; } label { display: block; line-height: 20px; }");

        var narrowBlock = Paragraphed(narrow).Block()!;

        for (var i = 0; i < 3; i++) {
            Assert.Equal(narrowBlock.Lines[i].Start, bandedBlock.Lines[i].Start);
            Assert.Equal(narrowBlock.Lines[i].Length, bandedBlock.Lines[i].Length);
        }
    }

    /// <summary>The same paragraph with no float beside it is not offset and is shorter.</summary>
    /// <remarks>
    ///     ⚠ <b>The control, and it is what says the band query is answering rather than the caller
    ///     assuming.</b> Everything above is also true of a paragraph indented by 100 points on three
    ///     lines for no reason; this is the assertion that goes red if <c>ContentBands</c> starts
    ///     answering for nodes that no float touches, which is the failure that would look like
    ///     success.
    /// </remarks>
    [Fact]
    public void A_paragraph_with_no_float_beside_it_keeps_the_whole_width() {
        using var document = Beside(floated: false);
        var label = Paragraphed(document);
        var block = label.Block()!;

        Assert.All(block.Lines, line => Assert.Equal(0f, line.Offset, Tolerance));
        Assert.Equal(3, block.Lines.Length);
        Assert.Equal(60f, label.Height, Tolerance);
    }

    /// <summary>A document 300 points wide, with or without a 100×60 float in front of the text.</summary>
    static UiDocument Beside(bool floated) {
        var document = new UiDocument(400f, 300f);
        document.Fonts.Register("Test", Font);
        document.Load(
            "root { width: 300px; height: 300px; display: block; } "
            + "box { float: left; width: 100px; height: 60px; } "
            + "label { display: block; line-height: 20px; }"
        );

        if (floated) {
            document.Root.Add("box");
        }

        return document;
    }

    static UiElement Paragraphed(UiDocument document) {
        var element = document.Root.Add("label");
        element.Text = Paragraph;
        document.Update();

        return element;
    }

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }
}
