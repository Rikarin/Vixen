// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     CSS Text § 4.1.3's phase II, read off the rasterised picture: a <c>pre-line</c> label's
///     leading and trailing spaces are not drawn.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A width assertion is not evidence that a picture changed</b>, and this change is a
///         picture — the ink of a paragraph moving by a space. So the claim is made on pixels: the
///         leftmost inked column of <c>"   ab"</c> under <c>pre-line</c> is the leftmost inked column
///         of <c>"ab"</c>, and the rightmost inked column of a right-aligned <c>"ab   "</c> is that of
///         a right-aligned <c>"ab"</c>. Both are closed-form — the same glyphs in the same place —
///         so nothing is tolerated.
///     </para>
///     <para>
///         ⚠ <b>Each row has a control that must differ, and it is what makes the equality mean
///         something.</b> The same text under <c>normal</c> — which in this engine preserves every
///         space, as CSS's <c>pre-wrap</c> does — has to start further right, or end further left,
///         than the reference. A picture in which the spaces had no width at all would pass the
///         equalities and fail the controls.
///     </para>
///     <para><c>Rikarin/Vixen#249</c>.</para>
/// </remarks>
public class WhiteSpacePhaseTwoPixelTests {
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

    /// <summary>
    ///     Six labels, one per row: the reference, the subject and the control, left-aligned and then
    ///     right-aligned.
    /// </summary>
    static readonly (string Text, string WhiteSpace, string Align)[] Rows = [
        ("ab", "normal", "left"),
        ("   ab", "pre-line", "left"),
        ("   ab", "normal", "left"),
        ("ab", "normal", "right"),
        ("ab   ", "pre-line", "right"),
        ("ab   ", "normal", "right")
    ];

    /// <summary>Renders the fixture and returns each row's inked extent.</summary>
    /// <returns>The leftmost and rightmost inked column of each row, in row order.</returns>
    static (int Left, int Right)[] Ink() {
        var image = Render();
        var extents = new (int Left, int Right)[Rows.Length];

        for (var i = 0; i < Rows.Length; i++) {
            var left = int.MaxValue;
            var right = int.MinValue;

            for (var y = i * Row; y < (i + 1) * Row && y < image.Height; y++) {
                for (var x = 0; x < image.Width; x++) {
                    // Any channel lit: the ground is black and the ink is white, so the first
                    // non-black column is where the first glyph's coverage begins.
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
    /// <returns>The picture.</returns>
    internal static Bitmap Render() {
        using var ui = UiTest.Create(240f, Rows.Length * Row);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
              root  { width: 240px; height: {{Rows.Length * Row}}px; background-color: #000000; }
              label { position: absolute; left: 20px; width: 200px; height: {{Row}}px;
                      font-family: Test; font-size: 24px; color: #ffffff; }
              """
        );

        for (var i = 0; i < Rows.Length; i++) {
            var (text, whiteSpace, align) = Rows[i];

            ui.Load($".r{i} {{ top: {i * Row}px; white-space: {whiteSpace}; text-align: {align}; }}");
            ui.Create("label", null, null, $"r{i}").Text = text;
        }

        ui.Frame();

        return ui.Capture();
    }

    /// <summary>A leading collapsible run is not drawn, and a preserved one is.</summary>
    [Fact]
    public void The_leading_run_is_not_drawn_under_pre_line() {
        var ink = Ink();

        Assert.Equal(ink[0].Left, ink[1].Left);
        Assert.True(ink[2].Left > ink[0].Left + 3, $"the control starts at {ink[2].Left}, not a space after {ink[0].Left}");
    }

    /// <summary>A trailing collapsible run is not drawn, so a right-aligned label ends flush.</summary>
    [Fact]
    public void The_trailing_run_is_not_drawn_under_pre_line() {
        var ink = Ink();

        Assert.Equal(ink[3].Right, ink[4].Right);
        Assert.True(ink[5].Right < ink[3].Right - 3, $"the control ends at {ink[5].Right}, not a space before {ink[3].Right}");
    }
}
