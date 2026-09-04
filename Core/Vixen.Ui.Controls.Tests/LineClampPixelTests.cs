// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>-webkit-line-clamp</c>, counted as bands of ink rather than as lines of a block.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A block-level assertion cannot close this one.</b> "The layout holds three lines" is
///         true of a clamp that dropped the wrong three, of one that dropped them and drew them
///         anyway, and of one whose marker landed on a line nobody can see. The oracle here is
///         closed-form and needs to know nothing about the layout: a generous <c>line-height</c>
///         leaves a blank row between lines, so the number of <i>contiguous bands of inked rows</i>
///         is the number of lines the rasteriser actually drew.
///     </para>
///     <para>
///         Open Sans rather than a Consortium fixture, for the reason
///         <c>TextTransformPixelTests</c> gives: a face that draws every character as .notdef
///         produces bands of tofu that count the same however wrong the text is.
///     </para>
/// </remarks>
public class LineClampPixelTests {
    const string Paragraph = "one two three four five six seven eight nine ten";
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    static Bitmap Render(string declarations) {
        using var ui = UiTest.Create(200f, 400f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
            root  { width: 200px; height: 400px; background-color: #000000; align-items: flex-start; }
            .label { position: absolute; left: 8px; top: 8px; width: 70px; font-size: 16px;
                     line-height: 2; color: #ffffff; {{declarations}} }
            """
        );

        ui.Create("div", null, "label", "label").Text = Paragraph;
        ui.Frame();

        return ui.Capture();
    }

    /// <summary>How many separated bands of inked rows the frame holds.</summary>
    static int Bands(Bitmap image) {
        var bands = 0;
        var inside = false;

        for (var y = 0; y < image.Height; y++) {
            var inked = false;

            for (var x = 0; x < image.Width && !inked; x++) {
                inked = image.Pixels[image.Offset(x, y)] >= 40;
            }

            if (inked && !inside) {
                bands++;
            }

            inside = inked;
        }

        return bands;
    }

    /// <summary>The lowest inked row, which is where the block visually ends.</summary>
    static int Bottom(Bitmap image) {
        for (var y = image.Height - 1; y >= 0; y--) {
            for (var x = 0; x < image.Width; x++) {
                if (image.Pixels[image.Offset(x, y)] >= 40) {
                    return y;
                }
            }
        }

        return -1;
    }

    [Fact]
    public void The_unclamped_paragraph_draws_more_lines_than_any_clamp_here_keeps() {
        Assert.True(Bands(Render(string.Empty)) > 3, "the fixture has to overflow for the rest to mean anything");
    }

    [Fact]
    public void A_clamp_of_three_draws_three_lines() {
        Assert.Equal(3, Bands(Render("-webkit-line-clamp: 3;")));
    }

    [Fact]
    public void A_clamp_of_one_draws_one() {
        Assert.Equal(1, Bands(Render("-webkit-line-clamp: 1;")));
    }

    /// <summary>
    ///     ⚠ <b>And the ink stops where the block does.</b> A clamp that shortened the measurement
    ///     and went on painting every line would satisfy nothing here and everything in a layout
    ///     assertion — which is the failure this file exists for.
    /// </summary>
    [Fact]
    public void The_dropped_lines_are_not_drawn_below_the_kept_ones() {
        Assert.True(
            Bottom(Render("-webkit-line-clamp: 3;")) < Bottom(Render(string.Empty)),
            "the clamped picture ends higher up"
        );
    }

    [Fact]
    public void None_draws_the_whole_paragraph() {
        Assert.Equal(Bands(Render(string.Empty)), Bands(Render("-webkit-line-clamp: none;")));
    }
}
