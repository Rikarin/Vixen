// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>text-decoration</c>, as the pixels the software rasteriser produced.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the tests the consumption gate cannot be, and the argument is
///         <c>MaskGradientTests</c>' one word for word.</b> That gate's verdict is "the draw list
///         changed", and any decoration changes it — the rectangle appears whatever it says. It would
///         pass on an underline drawn above the text, on an overline drawn at the baseline, on a
///         line-through drawn at a constant thickness, and on three lines that were all the same
///         line. The draw list is where that gate stops.
///     </para>
///     <para>
///         ⚠ <b>Every relation here is chosen to fail for the <i>neighbouring</i> case rather than
///         only for no decoration at all.</b> The underline must mark below every glyph pixel, which
///         the overline's placement fails; the overline must mark above every one, which the
///         underline's fails; the line-through must mark <i>between</i> them, which either of the
///         other two fails. A thicker bar must mark strictly more pixels than a thinner one, and a
///         doubled one must mark two separated bands rather than one taller one.
///     </para>
///     <para>
///         ⚠ <b>The text and the decoration are given different colours on purpose, and it is what
///         makes any of this measurable.</b> Red glyphs, a blue bar, on black: a blue pixel is the
///         decoration and a red one is a glyph, so the two bands can be compared without anything
///         here having to know where the baseline was. It exercises <c>text-decoration-color</c> for
///         free, and a fixture that drew both in white could not tell an underline from a descender.
///     </para>
///     <para>
///         ⚠ <b>And the glyphs are asserted to exist.</b> A shared test font that lacks a character
///         shapes it to <c>.notdef</c>, which is a visible box — so a band of "glyph" pixels proves
///         nothing on its own, and the relations above would all hold against a row of tofu with the
///         decoration in the wrong place. <see cref="The_font_draws_the_letters_this_file_measures" />
///         is the guard, and it is a <c>Fact</c> rather than an assertion inside the helper so that
///         its failure says what is wrong instead of failing eight tests obscurely.
///     </para>
/// </remarks>
public class TextDecorationPixelTests {
    const string Text = "AB";
    static readonly FontFace Font = LoadFont();

    /// <summary>The rendered frame, and where the two colours landed in it.</summary>
    /// <param name="Glyphs">The rows holding a red pixel, and how many there were.</param>
    /// <param name="Bar">The rows holding a blue one.</param>
    readonly record struct Marks(Band Glyphs, Band Bar);

    /// <summary>The vertical extent of one colour, and how much of it there was.</summary>
    /// <param name="Top">The first row it appears in, or -1.</param>
    /// <param name="Bottom">The last.</param>
    /// <param name="Count">How many pixels, which is what a thickness comparison reads.</param>
    /// <param name="Rows">Which rows, so that two bands can be told from one.</param>
    readonly record struct Band(int Top, int Bottom, int Count, IReadOnlySet<int> Rows) {
        public bool Any => Count > 0;
    }

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>Red text on black, with whatever decoration the test is about, drawn in blue.</summary>
    /// <remarks>
    ///     ⚠ The label is absolutely positioned well inside the viewport, so that an overline at the
    ///     ascent and a doubled underline both have room. A fixture where either ran off the edge
    ///     would silently measure a clipped band and read as a placement error.
    /// </remarks>
    static Marks Render(string declarations) {
        using var ui = UiTest.Create(240f, 160f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
            root { width: 240px; height: 160px; background-color: #000000; }
            .label { position: absolute; left: 20px; top: 40px; font-size: 60px;
                     color: #ff0000; text-decoration-color: #0000ff; {{declarations}} }
            """
        );

        ui.Create("div", null, "label", "label").Text = Text;
        ui.Frame();

        var image = ui.Capture();
        return new Marks(Scan(image, red: true), Scan(image, red: false));
    }

    /// <summary>Where one of the two colours appears. Red is a glyph and blue is a bar.</summary>
    /// <remarks>
    ///     A channel comparison rather than an equality, because both are antialiased against black
    ///     and almost no pixel is the pure colour. The two never blend into each other: the bar is
    ///     drawn as an opaque rectangle, so a pixel it covers is blue whatever was under it.
    /// </remarks>
    static Band Scan(Bitmap image, bool red) {
        var top = int.MaxValue;
        var bottom = -1;
        var count = 0;
        var rows = new HashSet<int>();

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var offset = image.Offset(x, y);
                var r = image.Pixels[offset];
                var b = image.Pixels[offset + 2];

                if (red ? r <= b || r < 8 : b <= r || b < 8) {
                    continue;
                }

                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                count++;
                rows.Add(y);
            }
        }

        return new Band(count == 0 ? -1 : top, bottom, count, rows);
    }

    /// <summary>The two letters this file measures are letters and not <c>.notdef</c> boxes.</summary>
    /// <remarks>
    ///     ⚠ <b>Written because an ellipsis test in this repository passed seven of its eight cases
    ///     against a tofu box.</b> Every relation below is about where a band of glyph pixels is, and
    ///     a missing glyph still produces one — so without this the whole file could be measuring the
    ///     placement of a rectangle the font drew to say it had nothing.
    /// </remarks>
    [Fact]
    public void The_font_draws_the_letters_this_file_measures() {
        foreach (var character in Text) {
            Assert.NotEqual(0, Font.GlyphFor(character));
        }
    }

    /// <summary>A bar batches as ordinary geometry, which is why there is no second executor to keep in step.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the measurement behind a refusal, and the refusal is not adding a
    ///         decoration to <c>UiCompositingTests</c>.</b> That suite exists to catch the device and
    ///         the software renderer disagreeing, and it has caught three real divergences — but every
    ///         one of them was a feature with code on both sides to disagree <i>with</i>. A decoration
    ///         has none: it goes out as <see cref="DrawCommandKind.Rectangle" /> with a zero radius,
    ///         batches as <see cref="BatchKind.Geometry" />, and is drawn by the same rounded-box
    ///         field a background is. There is no branch that could take a different turn on a GPU,
    ///         so a scene added over there would cost a Vulkan run to compare two paths that are one
    ///         path.
    ///     </para>
    ///     <para>
    ///         What could go wrong is the premise, not the conclusion — so the premise is what is
    ///         asserted. If a decoration ever grows a kind of its own, a dash pattern or a wave, this
    ///         fails, and the comment above stops being true at the same moment.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_decoration_batches_as_the_same_geometry_a_background_does() {
        using var ui = UiTest.Create(240f, 160f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            """
            root { width: 240px; height: 160px; background-color: #000000; }
            .label { position: absolute; left: 20px; top: 40px; font-size: 60px;
                     color: #ff0000; text-decoration-line: underline overline; }
            """
        );

        ui.Create("div", null, "label", "label").Text = Text;
        ui.Frame();

        var kinds = ui.Geometry.Draws.Select(static draw => draw.Kind).ToList();

        Assert.Contains(BatchKind.Text, kinds);
        Assert.DoesNotContain(BatchKind.PathFill, kinds);
        Assert.DoesNotContain(BatchKind.PathStroke, kinds);

        // The root's background and both bars, and nothing that is not one of the two kinds the
        // executors already share.
        Assert.All(kinds, static kind => Assert.True(kind is BatchKind.Geometry or BatchKind.Text, $"unexpected batch {kind}"));
    }

    /// <summary>Undecorated text puts red on the screen and no blue at all.</summary>
    /// <remarks>The baseline for everything else: if this fails, no other reading here means anything.</remarks>
    [Fact]
    public void Plain_text_marks_glyph_pixels_and_no_bar_pixels() {
        var marks = Render(string.Empty);

        Assert.True(marks.Glyphs.Count > 100, $"the glyphs should be visible, and only {marks.Glyphs.Count} pixels were");
        Assert.False(marks.Bar.Any, "nothing asked for a decoration");
        Assert.True(marks.Glyphs.Bottom > marks.Glyphs.Top, "the glyphs should occupy more than one row");
    }

    /// <summary>An underline marks below every glyph pixel.</summary>
    /// <remarks>
    ///     ⚠ <b>Below the glyphs and not merely below their middle</b>, which is what makes this fail
    ///     for a line-through as well as for an overline. <c>A</c> and <c>B</c> have no descenders, so
    ///     the last glyph row is the baseline and there is nothing legitimately beneath it.
    /// </remarks>
    [Fact]
    public void An_underline_marks_below_every_glyph_pixel() {
        var marks = Render("text-decoration-line: underline;");

        Assert.True(marks.Bar.Any, "the underline drew nothing");
        Assert.True(marks.Glyphs.Any, "the glyphs drew nothing");
        Assert.True(
            marks.Bar.Top > marks.Glyphs.Bottom,
            $"the bar starts at row {marks.Bar.Top} and the glyphs end at {marks.Glyphs.Bottom}"
        );
    }

    /// <summary>An overline marks above every glyph pixel.</summary>
    [Fact]
    public void An_overline_marks_above_every_glyph_pixel() {
        var marks = Render("text-decoration-line: overline;");

        Assert.True(marks.Bar.Any, "the overline drew nothing");
        Assert.True(
            marks.Bar.Bottom < marks.Glyphs.Top,
            $"the bar ends at row {marks.Bar.Bottom} and the glyphs start at {marks.Glyphs.Top}"
        );
    }

    /// <summary>A line-through marks between the top of the glyphs and the baseline.</summary>
    /// <remarks>
    ///     ⚠ <b>Compared against the <i>undecorated</i> glyph band, because a line-through paints over
    ///     the letters and takes some of their rows with it.</b> Read against its own capture, the
    ///     glyph band would have moved underneath the measurement — which is precisely the case a
    ///     naive version of this test gets wrong and calls a pass.
    /// </remarks>
    [Fact]
    public void A_line_through_marks_across_the_glyphs() {
        var plain = Render(string.Empty).Glyphs;
        var marks = Render("text-decoration-line: line-through;");

        Assert.True(marks.Bar.Any, "the line-through drew nothing");
        Assert.True(marks.Bar.Top > plain.Top, $"the bar starts at {marks.Bar.Top}, above the glyphs at {plain.Top}");
        Assert.True(marks.Bar.Bottom < plain.Bottom, $"the bar ends at {marks.Bar.Bottom}, below the baseline at {plain.Bottom}");
    }

    /// <summary>A thicker decoration marks strictly more pixels than a thinner one.</summary>
    /// <remarks>
    ///     ⚠ <b>Strictly, and against its immediate neighbour rather than against nothing.</b> A test
    ///     that compared <c>decoration-8</c> with no decoration at all would pass on an implementation
    ///     that ignored the thickness entirely.
    /// </remarks>
    [Fact]
    public void A_thicker_decoration_marks_strictly_more_pixels() {
        var one = Render("text-decoration-line: underline; text-decoration-thickness: 1px;").Bar;
        var two = Render("text-decoration-line: underline; text-decoration-thickness: 2px;").Bar;
        var four = Render("text-decoration-line: underline; text-decoration-thickness: 4px;").Bar;

        Assert.True(one.Any, "the thinnest bar drew nothing at all");
        Assert.True(two.Count > one.Count, $"2px marked {two.Count} pixels and 1px marked {one.Count}");
        Assert.True(four.Count > two.Count, $"4px marked {four.Count} pixels and 2px marked {two.Count}");
    }

    /// <summary>A doubled underline is two separated bars and not one taller one.</summary>
    [Fact]
    public void A_doubled_underline_leaves_a_gap_between_two_bars() {
        var doubled = Render(
            "text-decoration-line: underline; text-decoration-thickness: 2px; text-decoration-style: double;"
        ).Bar;

        Assert.True(doubled.Any, "the doubled underline drew nothing");

        // Rows the bar did not reach, strictly inside its own extent. One bar of any thickness has
        // none; two with a gap between them have exactly the gap.
        var gaps = Enumerable.Range(doubled.Top, doubled.Bottom - doubled.Top + 1)
            .Count(row => !doubled.Rows.Contains(row));

        Assert.True(gaps > 0, "the two bars ran together, which is one thick bar wearing a plural");
    }

    /// <summary><c>text-underline-offset</c> pushes the bar further down.</summary>
    [Fact]
    public void An_offset_moves_the_underline_down_and_a_larger_one_moves_it_further() {
        var near = Render("text-decoration-line: underline;").Bar;
        var far = Render("text-decoration-line: underline; text-underline-offset: 8px;").Bar;

        Assert.True(near.Any && far.Any, "both fixtures should have drawn a bar");
        Assert.True(far.Top > near.Top, $"the offset bar starts at {far.Top} and the plain one at {near.Top}");
        Assert.Equal(near.Count, far.Count);
    }

    /// <summary>Three lines at once are three bands, not one.</summary>
    /// <remarks>
    ///     The relation the individual tests cannot make: each of them would pass if all three
    ///     keywords resolved to the same line, because each fixture only ever asks for one.
    /// </remarks>
    [Fact]
    public void Three_lines_at_once_are_three_separate_bands() {
        var marks = Render("text-decoration-line: underline overline line-through; text-decoration-thickness: 2px;");

        var bands = 0;
        var previous = -2;

        foreach (var row in marks.Bar.Rows.Order()) {
            if (row != previous + 1) {
                bands++;
            }

            previous = row;
        }

        Assert.Equal(3, bands);
    }

    /// <summary>The bar takes the text's own colour when it is not given one.</summary>
    /// <remarks>
    ///     ⚠ Without <c>text-decoration-color</c> the whole picture is red, so the blue scan finds
    ///     nothing and the glyph band grows an underline — which is the observation, and is why this
    ///     one is written the other way round from every other test here.
    /// </remarks>
    [Fact]
    public void Without_a_colour_of_its_own_the_bar_is_the_text_colour() {
        using var ui = UiTest.Create(240f, 160f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            """
            root { width: 240px; height: 160px; background-color: #000000; }
            .label { position: absolute; left: 20px; top: 40px; font-size: 60px;
                     color: #ff0000; text-decoration-line: underline; }
            """
        );

        ui.Create("div", null, "label", "label").Text = Text;
        ui.Frame();

        var image = ui.Capture();
        var red = Scan(image, red: true);
        var blue = Scan(image, red: false);

        Assert.False(blue.Any, "nothing in the picture should be blue");
        Assert.True(red.Bottom > Render(string.Empty).Glyphs.Bottom, "the red should now reach below the baseline");
    }
}
