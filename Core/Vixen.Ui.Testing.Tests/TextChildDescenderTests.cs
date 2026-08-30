// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>
///     The same paragraph draws the same pixels whether an element holds it in its own
///     <c>Text</c> or a <c>text</c> child does.
/// </summary>
/// <remarks>
///     <para>
///         <b>The pixel half of what <c>TextWrapTests</c> asserts in numbers.</b> Over there the two
///         forms are required to <i>measure</i> the same and the container is required not to be
///         shorter than its child; here they are required to <i>draw</i> the same, which is the claim
///         the original report actually made and the only one a reader can see. The two are not the
///         same assertion: a height that is a pixel short is a defect only if something is drawn in
///         that pixel, and establishing that took rendering rather than arithmetic.
///     </para>
///     <para>
///         ⚠ <b>The clipping is real, and it is a row of glyph ink rather than a row of leading.</b>
///         With <c>TextLayout.Measure</c>'s ceiling reverted, at <c>line-height: 20.3px</c> and 300
///         pixels wide the box came out 81 tall around a text child measuring 82, and the picture
///         lost 11 475 units of ink — the bottom row of the last line's descenders, row 81 present in
///         the one form and absent in the other. That is the defect; the one-pixel disagreement in
///         the measurement is its shadow.
///     </para>
///     <para>
///         ⚠ <b>It only shows where the leading is tight, which is why the sweep is over
///         <c>line-height</c> and not only over width.</b> A fractional block height needs a
///         fractional line height, and the generous ones — the font's own recommendation, or the
///         27.875px the report was written against — leave half-leading below the last baseline that
///         is deeper than any descender reaches. There the lost pixel is blank and the pictures match
///         even with the bug present. The line heights below are chosen to be fractional <i>and</i>
///         close to the font size, which is the combination that puts ink in the last row.
///     </para>
///     <para>
///         ⚠ <b>Open Sans and not TestShapeLana, and the difference decides whether this test works at
///         all.</b> Lana is a Tai Tham face whose line box is sized for stacked marks, so its Latin
///         descenders stop four to six pixels short of the block's bottom edge; the whole sweep run
///         against it with the fix reverted loses no ink anywhere and passes. A fixture that cannot
///         express the property is indistinguishable from a fixed engine — so the choice of face is
///         load-bearing and is asserted below rather than assumed.
///     </para>
///     <para>
///         ⚠ <b>Chrome was consulted, and it settles the question the two forms were disagreeing
///         about without settling the integer.</b> Driven over the same face at the same sizes, a
///         browser reports the <i>same</i> height for both spellings at every width — 111.5 for the
///         27.875px case the report was written against, 81.1875 at 20.3px — so the container is
///         never shorter than the text in it. It reaches neither 112 nor 111, because it keeps
///         sixty-fourths of a pixel where this engine reports whole device ones. Of the two integers,
///         only the ceiling can hold a block that needs 111.5, so that is the one that is right and
///         the rounding-down was the defect. Line counts agreed with this engine's at every width
///         tested, which is what makes the comparison a comparison and not two different paragraphs.
///     </para>
///     <para>
///         <b>One divergence found on the way and deliberately not closed here:</b>
///         <c>line-height: normal</c>. This engine forms it from the face's unrounded ascent and
///         descent — 27.236 pixels for Open Sans at 20 — where Chrome rounds each to a whole pixel
///         first and gets 21 + 6 = 27. That is a difference in the strut and not in this rounding,
///         and it is worth naming here because it is <i>why</i> the bug above is reachable at all:
///         the default line height is fractional in this engine and integral in a browser. It is not
///         filed in <c>InlineKnownGaps.txt</c>, whose subject is <c>Vixen.Ui.Layout</c>'s inline
///         formatting, which has no font metrics at all.
///     </para>
///     <para>
///         <b>Compared against each other rather than against a committed picture.</b> Nothing here
///         is a reference PNG: the two forms are rendered in the same process and required to be
///         equal byte for byte, so the assertion is untouched by anything that changes how the
///         rasteriser draws a glyph. It says only that the two spellings agree, which is exactly the
///         claim.
///     </para>
/// </remarks>
public class TextChildDescenderTests {
    const int Side = 420;
    const float FontSize = 20f;

    static readonly Color4 Background = new(0f, 0f, 0f, 1f);
    static readonly FontFace Font = LoadFont();

    /// <summary>Line heights that are fractional and tight enough to put ink in the last row.</summary>
    static readonly string[] LineHeights = ["20.3px", "19.7px", "21.4px", "22.3px", "23.2px", "24.1px", "18.2px"];

    static readonly float[] Widths = [400f, 300f, 260f, 220f, 180f];

    /// <summary>Descenders in every line, so the last one has something to lose.</summary>
    const string Paragraph =
        "the quick brown fox jumps over lazy dogs page pgqjy and jumps again pgqjy "
        + "over every gap and page pgqjy";

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Testing.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the Latin test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "Test");
    }

    static UiTest Opened(float width, string lineHeight, string extra = "") {
        var ui = UiTest.Create(Side, Side, new UiTestOptions { Background = Background });
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            string.Create(
                CultureInfo.InvariantCulture,
                $"root {{ width: {Side}px; height: {Side}px; align-items: flex-start; }} "
                + $"box {{ width: {width}px; overflow: hidden; font-family: Test; "
                + $"font-size: {FontSize}px; line-height: {lineHeight}; color: #ffffff; }} "
                + $"text {{ font-family: Test; font-size: {FontSize}px; line-height: {lineHeight}; "
                + $"color: #ffffff; }} {extra}"
            )
        );

        return ui;
    }

    /// <summary>Draws the paragraph as the box's own text.</summary>
    static Bitmap AsOwnText(float width, string lineHeight, out float blockHeight, out int lines) {
        using var ui = Opened(width, lineHeight);
        var box = ui.Create("box", ui.Document.Root);
        box.Text = Paragraph;
        ui.Frame();

        var block = box.Block()!;
        blockHeight = block.Height;
        lines = block.Lines.Length;

        return ui.Capture();
    }

    /// <summary>Draws the paragraph as a <c>text</c> child, which is what a markup interpolation emits.</summary>
    static Bitmap AsChild(float width, string lineHeight) {
        using var ui = Opened(width, lineHeight);
        var box = ui.Create("box", ui.Document.Root);
        var child = ui.Create("text", box);
        child.Text = Paragraph;
        ui.Frame();

        return ui.Capture();
    }

    static long Ink(in Bitmap image) {
        var total = 0L;

        for (var i = 0; i < image.Width * image.Height; i++) {
            total += image.Pixels[(i * 4) + 0] + image.Pixels[(i * 4) + 1] + image.Pixels[(i * 4) + 2];
        }

        return total;
    }

    /// <summary>The bottom-most row carrying any ink, or −1.</summary>
    static int LastInkRow(in Bitmap image) {
        for (var y = image.Height - 1; y >= 0; y--) {
            for (var x = 0; x < image.Width; x++) {
                var offset = image.Offset(x, y);

                if (image.Pixels[offset] > 8 || image.Pixels[offset + 1] > 8 || image.Pixels[offset + 2] > 8) {
                    return y;
                }
            }
        }

        return -1;
    }

    [Fact]
    public void A_paragraph_draws_the_same_pixels_as_a_child_as_it_does_as_the_boxs_own_text() {
        var sawFraction = false;
        var sawWrapping = false;

        foreach (var lineHeight in LineHeights)
        foreach (var width in Widths) {
            var own = AsOwnText(width, lineHeight, out var blockHeight, out var lines);
            var child = AsChild(width, lineHeight);

            sawFraction |= Math.Abs(blockHeight - MathF.Round(blockHeight)) > 0.01f;
            sawWrapping |= lines > 1;

            // ⚠ The bottom-most row first, because it names the defect. When this fails it fails
            // because the child form's last line of descenders was cut off by a container a pixel
            // shorter than the text inside it, and saying so is worth more than "the pictures
            // differ".
            Assert.Equal(LastInkRow(own), LastInkRow(child));

            Assert.True(
                Ink(own) == Ink(child),
                $"at line-height {lineHeight} and width {width} the paragraph drew {Ink(own)} of ink "
                + $"as the box's own text and {Ink(child)} as a text child, a loss of "
                + $"{Ink(own) - Ink(child)}"
            );

            Assert.Equal(own.Pixels, child.Pixels);
        }

        // ⚠ Two instrument checks, because this assertion passes on a broken engine in two ways.
        // Without a fractional block height there is no rounding for the two paths to disagree
        // about, and without wrapping there is no last line to clip.
        Assert.True(sawFraction, "no configuration produced a fractional block height");
        Assert.True(sawWrapping, "nothing wrapped, so there was never a last line to lose");
    }

    /// <summary>
    ///     ⚠ The fixture can see a clipped descender: the clip removes ink, and the ink it would
    ///     remove is a glyph rather than leading.
    /// </summary>
    /// <remarks>
    ///     Both halves of this were wrong once. <c>overflow: hidden</c> that did not clip, or a face
    ///     whose descenders stop short of the block's bottom edge, each turn the test above into one
    ///     that cannot fail — and the second was true of the font this project already embedded. So
    ///     the clip is shown to bite, and the last row of the block is shown to carry glyph ink.
    /// </remarks>
    [Fact]
    public void The_clip_bites_and_the_last_row_of_a_tight_line_box_carries_ink() {
        const string LineHeight = "20.3px";
        const float Width = 300f;

        var full = AsOwnText(Width, LineHeight, out var blockHeight, out _);
        var unclipped = Ink(full);

        // The clip is live: forcing the box shorter than its text removes ink.
        using (var ui = Opened(Width, LineHeight, "box { height: 40px; }")) {
            var box = ui.Create("box", ui.Document.Root);
            box.Text = Paragraph;
            ui.Frame();

            Assert.True(
                Ink(ui.Capture()) < unclipped,
                "`overflow: hidden` removed nothing, so nothing below could ever have been clipped"
            );
        }

        // And the ink reaches the last row of the block, so the pixel the two paths disagreed about
        // is a glyph rather than leading. This is what TestShapeLana could not express.
        var bottom = (int)MathF.Ceiling(blockHeight) - 1;

        Assert.Equal(bottom, LastInkRow(full));
    }
}
