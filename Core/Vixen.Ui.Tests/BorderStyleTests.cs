// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>border-style</c> and <c>outline-style</c>, from the cascade to the draw list.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing read these four longhands at all until doc 43 § A3.</b> A
///         <c>border-style</c> resolved into <c>border-top-style</c> and its three siblings and moved
///         no channel in any scene — which is why <c>border-solid</c>, <c>divide-dashed</c>,
///         <c>decoration-dotted</c> and <c>outline-double</c> were all unregistered at once and why
///         the four families close together.
///     </para>
///     <para>
///         ⚠ <b>An absent style is <c>solid</c> here and <c>none</c> in CSS, deliberately.</b> A
///         browser paints nothing for <c>border-width: 2px</c> alone; this engine has always painted,
///         and every theme and utility class in the tree is written against that. The first test
///         below is that departure asserted rather than assumed, because it is the one thing a
///         faithful reading of the specification would have broken.
///     </para>
///     <para>
///         ⚠ <b>The broken styles are judged by covered length rather than by a picture.</b> The dash
///         distribution stretches its marks to fit, so the ink is a closed form — see
///         <c>DashesTests</c> — and a test can say a dashed edge covers strictly less than a solid one
///         without anybody eyeballing a screenshot.
///     </para>
/// </remarks>
public class BorderStyleTests {
    static UiDocument Drawn(string css) {
        var document = new UiDocument(200f, 200f);
        document.Load(".probe { width: 40px; height: 20px; } " + css);
        document.Root.Add("div", classNames: "probe");
        document.Update();
        document.Draw();

        return document;
    }

    static IReadOnlyList<DrawCommand> Rings(UiDocument document) =>
        [.. document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Border)];

    static IReadOnlyList<DrawCommand> Bands(UiDocument document) =>
        [.. document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Rectangle)];

    static IReadOnlyList<DrawCommand> Strokes(UiDocument document) =>
        [.. document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.PathStroke)];

    [Fact]
    public void A_width_with_no_style_still_paints_which_is_not_what_css_says() {
        using var document = Drawn(".probe { border-width: 2px; border-color: #ff0000; }");

        // ⚠ The departure, asserted. CSS's initial `border-style` is `none` and a browser draws
        // nothing here; taking that reading would blank every border in the repository at once, to
        // obey a rule whose only purpose is to let the `border` shorthand carry a width without
        // committing to a style.
        Assert.Single(Rings(document));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("hidden")]
    public void A_border_told_not_to_draw_draws_nothing(string keyword) {
        using var document = Drawn($".probe {{ border-width: 2px; border-color: #ff0000; border-style: {keyword}; }}");

        Assert.Empty(Rings(document));
        Assert.Empty(Bands(document));
        Assert.Empty(Strokes(document));
    }

    [Fact]
    public void An_unrecognised_style_is_solid_and_not_nothing() {
        // `groove` is real CSS this engine cannot draw — it is two-tone and the border record carries
        // one colour. Drawing it flat is CSS's own fallback for a style the user agent cannot render;
        // drawing nothing would make a typo invisible.
        using var document = Drawn(".probe { border-width: 2px; border-color: #ff0000; border-style: groove; }");

        Assert.Single(Rings(document));
    }

    [Fact]
    public void A_doubled_border_is_two_rings_a_third_as_thick_with_the_middle_third_between_them() {
        using var document = Drawn(".probe { border-width: 3px; border-color: #ff0000; border-style: double; }");

        var rings = Rings(document);

        Assert.Equal(2, rings.Count);
        Assert.Equal(1f, rings[0].Thickness, 0.001f);
        Assert.Equal(1f, rings[1].Thickness, 0.001f);

        // The outer ring is the border box; the inner one starts two thirds in, so the gap between
        // the two painted thirds is the third in the middle.
        Assert.Equal(rings[0].X + 2f, rings[1].X, 0.001f);
        Assert.Equal(rings[0].Width - 4f, rings[1].Width, 0.001f);
    }

    [Theory]
    [InlineData("dashed")]
    [InlineData("dotted")]
    public void A_broken_border_is_a_stroked_path_and_not_a_ring(string keyword) {
        using var document = Drawn($".probe {{ border-width: 2px; border-color: #ff0000; border-style: {keyword}; }}");

        // ⚠ The switch of machinery, asserted. A ring's fragment shader knows how far a pixel is from
        // the outline and not how far *along* it, and a dash is an arc length — so a broken border
        // cannot be the command a solid one is, however much it looks like the same picture.
        Assert.Empty(Rings(document));

        var stroke = Assert.Single(Strokes(document));

        Assert.Equal(2f, stroke.Thickness, 0.001f);
        Assert.True(stroke.Length > 2, "a dashed ring is more than one sub-path");
    }

    [Fact]
    public void A_dotted_border_has_more_sub_paths_than_a_dashed_one() {
        using var dashed = Drawn(".probe { border-width: 2px; border-color: #ff0000; border-style: dashed; }");
        using var dotted = Drawn(".probe { border-width: 2px; border-color: #ff0000; border-style: dotted; }");

        // ⚠ The assertion that tells the two keywords apart. A reader that mapped both onto one
        // pattern satisfies every other test in this file.
        Assert.True(Assert.Single(Strokes(dotted)).Length > Assert.Single(Strokes(dashed)).Length);
    }

    [Fact]
    public void A_broken_border_keeps_its_corner_radius_because_it_walks_the_ring() {
        using var square = Drawn(".probe { border-width: 2px; border-color: #ff0000; border-style: dashed; }");
        using var rounded = Drawn(
            ".probe { border-width: 2px; border-color: #ff0000; border-style: dashed; border-radius: 8px; }"
        );

        // ⚠ The property the band path cannot have, and the reason the uniform case is a stroked
        // centre line rather than four dashed rectangles. A rounded box's ring is shorter than a
        // square one's — the corners cut a quarter of each right angle off — so a walk that honoured
        // the radius produces a different number of sub-paths from one that did not.
        Assert.NotEqual(Assert.Single(Strokes(square)).Length, Assert.Single(Strokes(rounded)).Length);
    }

    [Fact]
    public void A_style_on_one_edge_is_read_on_that_edge_only() {
        using var document = Drawn(
            """
            .probe { border-width: 2px; border-color: #ff0000; border-top-style: none; }
            """
        );

        // Four edges that no longer agree about their style are bands, not a ring — and the top one
        // is missing. Three bands, and none of them is at the top.
        Assert.Empty(Rings(document));

        var bands = Bands(document);

        Assert.Equal(3, bands.Count);
        Assert.DoesNotContain(bands, band => band is { Y: 0f, Height: 2f, Width: 44f });
    }

    [Fact]
    public void A_divider_is_a_band_and_a_dashed_divider_is_marks_along_it() {
        using var solid = Drawn(".probe { border-bottom-width: 2px; border-color: #ff0000; }");
        using var dashed = Drawn(
            ".probe { border-bottom-width: 2px; border-color: #ff0000; border-style: dashed; }"
        );

        // ⚠ Where `divide-dashed` lands. A divider writes a width on one edge and zero on the other
        // three, so it is never the uniform case and never reaches the stroked ring — which is why
        // the band path had to answer the broken styles as well.
        var whole = Assert.Single(Bands(solid));
        var marks = Bands(dashed);

        Assert.True(marks.Count > 1, "a dashed divider is more than one rectangle");

        var ink = marks.Sum(mark => mark.Width);

        Assert.True(ink < whole.Width, $"a dashed divider covers less than a solid one: {ink} of {whole.Width}");
        Assert.Equal(marks.Count * 6f, ink, 0.001f);
        Assert.Equal(whole.X, marks[0].X, 0.001f);
        Assert.Equal(whole.X + whole.Width, marks[^1].X + marks[^1].Width, 0.001f);

        foreach (var mark in marks) {
            Assert.Equal(whole.Y, mark.Y, 0.001f);
            Assert.Equal(whole.Height, mark.Height, 0.001f);
        }
    }

    [Fact]
    public void A_dashed_band_carries_the_corner_radii_the_solid_band_carries() {
        using var solid = Drawn(
            ".probe { border-bottom-width: 2px; border-color: #ff0000; border-radius: 8px; }"
        );

        using var dashed = Drawn(
            """
            .probe {
                border-bottom-width: 2px;
                border-color: #ff0000;
                border-radius: 8px;
                border-style: dashed;
            }
            """
        );

        // ⚠ The defect, asserted as geometry rather than as a picture. A band's two radii live in the
        // draw list's side buffer, so "the dashed band rounds where the solid one does" is the
        // statement that the first and last mark carry the same two corners the whole band carried —
        // and the marks between them carry none, because the shape they are pieces of is square
        // there. Before this, every mark was square and the side buffer held nothing at all.
        var whole = Assert.Single(Bands(solid));

        Assert.True(whole.HasStyle, "a solid band on a rounded box carries its corners");

        var band = solid.Drawing.Boxes[whole.Offset].Corners;

        Assert.Equal(new Vector2(8f, 8f), band.BottomLeft);
        Assert.Equal(new Vector2(8f, 8f), band.BottomRight);

        var marks = Bands(dashed);

        Assert.True(marks.Count > 2, "the run is long enough to have a mark that touches neither end");

        var first = Corners(dashed, marks[0]);
        var last = Corners(dashed, marks[^1]);

        Assert.Equal(band.BottomLeft, first.BottomLeft);
        Assert.Equal(Vector2.Zero, first.BottomRight);
        Assert.Equal(band.BottomRight, last.BottomRight);
        Assert.Equal(Vector2.Zero, last.BottomLeft);

        // The band's only curve is at its two ends, so a mark in the middle is square — and pays
        // nothing for saying so, because `Styled` leaves a square box out of the side buffer.
        for (var index = 1; index < marks.Count - 1; index++) {
            Assert.False(marks[index].HasStyle, "a mark that touches neither end of the band is square");
        }

        static CornerRadii Corners(UiDocument document, DrawCommand mark) {
            Assert.True(mark.HasStyle, "a mark at the end of a rounded band carries that end's corner");
            return document.Drawing.Boxes[mark.Offset].Corners;
        }
    }

    [Fact]
    public void A_dashed_band_on_a_square_box_still_costs_nothing() {
        using var document = Drawn(
            ".probe { border-bottom-width: 2px; border-color: #ff0000; border-style: dashed; }"
        );

        // The cheap path the marks came from, kept. A divider is the overwhelmingly common band and
        // it never rounds, so no mark of it may reach the side buffer the frame diff walks entry by
        // entry — rounding the ends must cost only the boxes that are actually round.
        Assert.True(Bands(document).Count > 1);
        Assert.All(Bands(document), mark => Assert.False(mark.HasStyle));
        Assert.Empty(document.Drawing.Boxes);
    }

    [Fact]
    public void A_band_cannot_be_walked_because_its_thickness_is_its_own_cross_axis_extent() {
        var outline = new List<Vector2>();

        // ⚠ Why the marks are cut out of the band rather than walked along it, which is the obvious
        // reading of "make the band agree with the ring". A ring's centre line sits half a thickness
        // inside the *border box*, which is thicker than the border; a band's cross-axis extent *is*
        // its thickness, so the same inset closes it completely and `Rings.Outline` returns nothing.
        // A dashed divider walked on this would draw no ink at all.
        Ui.Rings.Outline(
            0f,
            20f,
            40f,
            2f,
            new CornerRadii(default, default, new(8f, 8f), new(8f, 8f)),
            1f,
            outline
        );

        Assert.Empty(outline);
    }

    [Fact]
    public void A_doubled_divider_is_two_strips_of_a_third_each() {
        using var document = Drawn(
            ".probe { border-bottom-width: 3px; border-color: #ff0000; border-style: double; }"
        );

        var strips = Bands(document);

        Assert.Equal(2, strips.Count);
        Assert.Equal(1f, strips[0].Height, 0.001f);
        Assert.Equal(1f, strips[1].Height, 0.001f);
        Assert.Equal(strips[0].Y + 2f, strips[1].Y, 0.001f);
    }

    [Fact]
    public void An_outline_reads_the_same_five_keywords() {
        using var solid = Drawn(".probe { outline-width: 3px; outline-color: #ff0000; }");
        using var doubled = Drawn(
            ".probe { outline-width: 3px; outline-color: #ff0000; outline-style: double; }"
        );

        using var dashed = Drawn(
            ".probe { outline-width: 3px; outline-color: #ff0000; outline-style: dashed; }"
        );

        Assert.Single(Rings(solid));
        Assert.Equal(2, Rings(doubled).Count);
        Assert.Empty(Rings(dashed));
        Assert.Single(Strokes(dashed));
    }
}
