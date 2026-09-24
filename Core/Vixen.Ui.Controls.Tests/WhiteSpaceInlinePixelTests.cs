// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     White space across the boundary between two <c>display: inline</c> elements, read off the
///     rasterised picture (#1363).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A width assertion is not evidence that a picture changed</b>, for
///         <c>WhiteSpacePhaseTwoPixelTests</c>' reason. Every row is a block container of inline
///         labels, drawn white on black, and what is compared is the inked extent of the row: the
///         same words with one space between them ink the same columns wherever the element
///         boundaries fall. <c>WhiteSpaceInlineCollapseTests</c> holds the measured widths to Chrome.
///     </para>
///     <para>
///         ⚠ <b>One pixel is tolerated on a split row, and it is not slack.</b> Layout rounds each
///         box up to a whole pixel, so the second element's glyphs start on the next whole column
///         rather than at the fractional pen position one paragraph would give them. A space at this
///         size is six pixels, so the controls — which must differ by one — are well clear of it.
///     </para>
/// </remarks>
public class WhiteSpaceInlinePixelTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>The height of one row of the fixture, in pixels.</summary>
    const int Row = 40;

    /// <summary>Each row: its labels, split where the element boundaries fall, and their <c>white-space</c>.</summary>
    static readonly (string[] Parts, string WhiteSpace)[] Rows = [
        (["foo bar"], "normal"),
        (["foo ", "bar"], "normal"),
        (["foo ", " bar"], "pre-line"),
        (["foo ", " bar"], "normal"),
        (["ab"], "normal"),
        (["   ab"], "pre-line"),
        (["   ab"], "normal")
    ];

    /// <summary>Renders the fixture and returns each row's inked extent.</summary>
    static (int Left, int Right)[] Ink() {
        var image = Render();
        var extents = new (int Left, int Right)[Rows.Length];

        for (var i = 0; i < Rows.Length; i++) {
            var left = int.MaxValue;
            var right = int.MinValue;

            for (var y = i * Row; y < (i + 1) * Row && y < image.Height; y++) {
                for (var x = 0; x < image.Width; x++) {
                    if (image.Pixels[image.Offset(x, y)] > 16) {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
            }

            Assert.True(left <= right, $"row {i} drew nothing, so every comparison below would be about nothing");
            extents[i] = (left, right);
        }

        return extents;
    }

    /// <summary>The fixture, rasterised by the software rasteriser the screenshot suites use.</summary>
    internal static Bitmap Render() {
        using var ui = UiTest.Create(260f, Rows.Length * Row);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
              root  { width: 260px; height: {{Rows.Length * Row}}px; background-color: #000000; }
              line  { position: absolute; left: 20px; width: 220px; height: {{Row}}px; display: block; }
              label { display: inline; font-family: Test; font-size: 24px; color: #ffffff; }
              """
        );

        for (var i = 0; i < Rows.Length; i++) {
            var (parts, whiteSpace) = Rows[i];

            ui.Load($".r{i} {{ top: {i * Row}px; }} .r{i} label {{ white-space: {whiteSpace}; }}");
            var line = ui.Create("line", null, null, $"r{i}");

            foreach (var part in parts) {
                line.Add("label").Text = part;
            }
        }

        ui.Frame();

        var picture = ui.Capture();

        if (Environment.GetEnvironmentVariable("VIXEN_PICTURES") is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, "inline-white-space.png"), picture);
        }

        return picture;
    }

    /// <summary>
    ///     ⚠ <b>The defect under the filed one</b>: a trailing space before a word in the next element
    ///     was hung out of its box, so <c>foo␠</c> beside <c>bar</c> drew <c>foobar</c> — under
    ///     <c>normal</c>, which is every undeclared label.
    /// </summary>
    [Fact]
    public void A_space_before_the_next_elements_word_is_drawn() {
        var ink = Ink();

        Assert.InRange(ink[1].Right - ink[1].Left, ink[0].Right - ink[0].Left, ink[0].Right - ink[0].Left + 1);
    }

    /// <summary>
    ///     Two <c>pre-line</c> spaces either side of the boundary are one, and two preserved ones —
    ///     the control — are two.
    /// </summary>
    [Fact]
    public void Two_collapsible_spaces_across_the_boundary_draw_as_one() {
        var ink = Ink();
        var reference = ink[0].Right - ink[0].Left;

        Assert.InRange(ink[2].Right - ink[2].Left, reference, reference + 1);
        Assert.True(ink[3].Right - ink[3].Left > reference + 3, $"the control inks {ink[3].Right - ink[3].Left} columns against {reference}");
    }

    /// <summary>An inline element that begins its container's line does not draw its leading run; the control does.</summary>
    [Fact]
    public void The_leading_run_of_an_inline_element_that_begins_the_line_is_not_drawn() {
        var ink = Ink();

        Assert.Equal(ink[4].Left, ink[5].Left);
        Assert.True(ink[6].Left > ink[4].Left + 3, $"the control starts at {ink[6].Left}, not a space after {ink[4].Left}");
    }
}
