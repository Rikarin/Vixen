// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Text from the cascade to the draw list.</summary>
/// <remarks>
///     Against a real font rather than a stub with invented metrics, because a stub agrees with
///     whatever this assembly does to it. The one thing these tests cannot judge is the shaping
///     itself — that is HarfBuzz's, and <c>Vixen.Ui.Text.Tests</c> judges it against the Consortium's
///     expectations. What is judged here is everything around it: the font a declaration resolves to,
///     the scale from design units to pixels, where the baseline goes, and whether the frame diff
///     notices a word changing.
/// </remarks>
public class TextTests {
    const float Tolerance = 0.01f;
    static readonly FontFace Font = LoadFont("TestShapeLana.ttf", "TestShapeLana");
    static readonly FontFace Kannada = LoadFont("NotoSerifKannada-Regular.ttf", "Kannada");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    static UiDocument Documented(string css = "root { width: 400px; height: 200px; }") {
        var document = new UiDocument(400f, 200f);
        document.Fonts.Register("Test", Font);
        document.Load(css);

        return document;
    }

    static DrawCommand TextCommand(UiDocument document) =>
        Assert.Single(document.Drawing.Commands, static command => command.Kind == DrawCommandKind.Text);

    [Fact]
    public void An_element_with_text_measures_itself_from_it() {
        // ⚠ `align-items: flex-start`, or the height would come from the container rather than from
        // the text: CSS stretches a flex item on the cross axis by default, so a label in a
        // two-hundred-pixel row is two hundred pixels tall however small its text is. Correct, and
        // exactly the kind of thing that reads as a broken measurement.
        using var document = Documented("root { width: 400px; height: 200px; align-items: flex-start; }");

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();

        var run = label.Block()!.Lines[0];
        Assert.True(run.Width > 0f);

        // Flexbox asked the text how big it is rather than being told, which is what a measure
        // function is for. Without one the element would be its content's size and its content is
        // nothing, so this failing looks like the text not being there at all.
        //
        // ⚠ Within a pixel rather than exactly. A measured width is a real number — this string is
        // 21.875 pixels wide — and the layout engine snaps every edge to the pixel grid so that a
        // box does not straddle one. So an element's width is its text's width rounded, and a test
        // written against the exact measurement fails by a fraction that looks like a scaling bug.
        Assert.Equal(run.Width, label.Width, 1f);
        Assert.Equal(run.Height, label.Height, 1f);
    }

    [Fact]
    public void The_same_string_at_twice_the_size_is_twice_as_wide() {
        using var document = Documented("""
            root { width: 400px; height: 200px; }
            label { font-family: Test; }
            .big { font-size: 32px; }
        """);

        var small = document.Root.Add("label");
        var big = document.Root.Add("label", null, "big");

        small.Text = "AB";
        big.Text = "AB";

        document.Update();

        // Shaping happens once, in the font's design units, and the size is applied afterwards —
        // which is what makes the shaping cache size-independent. If the size were baked into the
        // shaping this would still pass and the cache would hold one entry per DPI scale.
        //
        // Against the measured runs rather than the laid-out widths, because those are snapped to
        // the pixel grid and two roundings of the same number are not one rounding of twice it.
        Assert.Equal(16f, small.FontSize, Tolerance);
        Assert.Equal(32f, big.FontSize, Tolerance);
        Assert.Equal(small.Block()!.Lines[0].Width * 2f, big.Block()!.Lines[0].Width, Tolerance);
        Assert.Equal(1, document.Shaping.Misses);
    }

    [Fact]
    public void The_glyphs_reach_the_draw_list_where_the_shaper_put_them() {
        using var document = Documented();

        var label = document.Root.Add("label");

        // ⚠ Tai Tham rather than Latin, and that is the whole point of the choice. Every Latin glyph
        // in this font sits on the baseline with a zero y offset, so an assertion about the y is
        // vacuous against `AB` — the first version of this test passed with the negation deleted.
        // These three characters are a consonant with a vowel sign hung below it, so one glyph comes
        // back at a non-zero y and the sign of it is decided by something.
        label.Text = "ᨡᩬᩴ";

        document.Update();
        document.Draw();

        var command = TextCommand(document);
        var expected = TextShaper.Shape(Font, label.Text).Placements().ToList();
        var scale = label.FontSize / Font.UnitsPerEm;

        Assert.Equal(expected.Count, command.Length);
        Assert.Equal(label.FontSize, command.FontSize, Tolerance);
        Assert.Same(Font, document.Drawing.Fonts[command.Font]);
        Assert.Contains(expected, static placement => placement.Y != 0f);

        for (var i = 0; i < expected.Count; i++) {
            var glyph = document.Drawing.Glyphs[command.Offset + i];

            Assert.Equal(expected[i].GlyphId, glyph.GlyphId);
            Assert.Equal(expected[i].X * scale, glyph.X, Tolerance);

            // Negated, because shaping puts y positive upwards — that is how a font's design grid is
            // drawn — and the draw list is in document space where y grows downwards. The vowel sign
            // that hangs below the letter comes out of the shaper at a negative y and has to be
            // drawn at a larger one.
            Assert.Equal(-expected[i].Y * scale, glyph.Y, Tolerance);
        }
    }

    [Fact]
    public void The_run_sits_on_its_baseline_inside_the_content_box() {
        using var document = Documented("""
            root { width: 400px; height: 200px; }
            label { padding-left: 7px; padding-top: 5px; border-left-width: 2px; border-top-width: 3px;
                    border-style: solid; }
        """);

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        var command = TextCommand(document);
        var run = label.Block()!.Lines[0];

        // Inside the border and the padding, because that is what those two properties mean, and
        // read from the layout results rather than resolved again from the style.
        Assert.Equal(label.AbsoluteLeft + 9f, command.X, Tolerance);

        // ⚠ And the y is the baseline rather than the top. Glyph origins sit on the baseline, so
        // putting the content box's top there draws every line one ascender too high — which for a
        // single line reads as a padding mistake.
        Assert.Equal(label.AbsoluteTop + 8f + run.Baseline, command.Y, Tolerance);
        Assert.True(run.Baseline > 0f);
    }

    [Fact]
    public void Changing_the_word_changes_the_drawing_even_when_nothing_moves() {
        using var document = Documented();

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        var before = document.Drawing.Version;

        // ⚠ Two letters for two letters, in a font where they are the same width — so the command's
        // position, size and glyph range are all byte-identical and only the side buffer differs. A
        // frame diff that compared commands alone would report no change and a renderer trusting it
        // would keep drawing the old word.
        label.Text = "BA";
        document.Update();

        Assert.True(document.Draw());
        Assert.NotEqual(before, document.Drawing.Version);
    }

    [Fact]
    public void Redrawing_the_same_text_changes_nothing() {
        using var document = Documented();

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        var version = document.Drawing.Version;

        Assert.False(document.Draw());
        Assert.Equal(version, document.Drawing.Version);
    }

    [Fact]
    public void Taking_the_text_away_leaves_nothing_behind() {
        using var document = Documented("""
            root { width: 400px; height: 200px; }
            box { width: 40px; height: 40px; }
        """);

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        Assert.True(label.Width > 0f);

        label.Text = null;
        document.Update();
        document.Draw();

        Assert.Equal(0f, label.Width, Tolerance);
        Assert.DoesNotContain(document.Drawing.Commands, static command => command.Kind == DrawCommandKind.Text);

        // ⚠ And the measure function is detached rather than left in place answering zero, which is
        // only observable here: a node that measures itself is a leaf, so its children are never
        // laid out at all. Asserting the label's own width does not catch it — a measure function
        // over no text answers zero and looks exactly like not having one — and this child sitting
        // at nothing by nothing is what a label that stopped being a label would leave behind.
        var child = label.Add("box");
        document.Update();

        Assert.Equal(40f, child.Width, Tolerance);
    }

    [Fact]
    public void An_element_with_text_cannot_also_have_children() {
        using var document = Documented();

        var panel = document.Root.Add("label");
        panel.Add("box");

        // ⚠ The layout tree refuses it, and it is right to: a node that measures itself and also has
        // children has its size decided twice, by two rules that do not have to agree. So a text
        // element is a leaf, full stop — mixed content is what the owed run list is for rather than
        // something to fake by nesting.
        Assert.Throws<InvalidOperationException>(() => panel.Text = "AB");
    }

    [Fact]
    public void A_font_family_names_a_registered_face() {
        using var document = Documented("""
            root { width: 400px; height: 200px; }
            label { font-family: "Not Installed", Test; }
        """);

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        // The list is tried in order until a registered family is found, and the quotes are CSS
        // syntax rather than part of the name. ⚠ This is not per-glyph fallback: a face that is
        // registered and lacks the character draws .notdef rather than passing to the next name.
        Assert.Same(Font, document.Drawing.Fonts[TextCommand(document).Font]);
    }

    [Fact]
    public void Text_with_no_font_to_draw_it_in_draws_nothing() {
        using var document = new UiDocument(400f, 200f);

        document.Load("root { width: 400px; height: 200px; }");

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        // No registry entry, so no default either. An interface with a missing font is a bug worth
        // seeing rather than an exception worth throwing at whoever set the text.
        Assert.Null(label.Block());
        Assert.Equal(0f, label.Width, Tolerance);
        Assert.Empty(document.Drawing.Commands);
    }

    [Fact]
    public void Measuring_and_drawing_shape_once_between_them() {
        using var document = Documented();

        var first = document.Root.Add("label");
        var second = document.Root.Add("label");

        first.Text = "AB";
        second.Text = "AB";

        document.Update();
        document.Draw();

        // Two elements, a measure pass and a draw pass, and one trip through HarfBuzz. The cache is
        // keyed on the font and the string rather than on the element, which is what makes a table of
        // ten thousand rows saying the same word cost one shaping.
        Assert.Equal(1, document.Shaping.Misses);
        Assert.True(document.Shaping.Hits > 0);

        // ⚠ And the second pass does not reach the shaping cache at all, which is the other half:
        // `UiElement.Line` keeps the runs it built, so drawing asks the element rather than the
        // cache. That matters more than the hit count does — deciding which face draws which
        // character costs a native call per code point, and doing it twice a frame per element is
        // what a cache here is for.
        var lookups = document.Shaping.Hits + document.Shaping.Misses;

        document.Draw();
        Assert.Equal(lookups, document.Shaping.Hits + document.Shaping.Misses);
        Assert.Same(first.Block(), first.Block());
    }

    [Fact]
    public void The_text_takes_its_colour_from_the_cascade() {
        using var document = Documented("""
            root { width: 400px; height: 200px; }
            label { color: #ff0000; }
        """);

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        var color = TextCommand(document).Color;
        Assert.Equal(1f, color.R, Tolerance);
        Assert.Equal(0f, color.G, Tolerance);
    }

    [Fact]
    public void Text_is_drawn_over_its_own_background() {
        using var document = Documented("""
            root { width: 400px; height: 200px; }
            label { background-color: #ffffff; }
        """);

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        var kinds = document.Drawing.Commands.Select(static command => command.Kind).ToList();

        // CSS paints an element's background, then its border, then its own content, then its
        // children. Only the first half of that is reachable here: a text element cannot have
        // children, so "under its children" is the ordering the builder is written for and not a
        // claim this can check. Said rather than asserted, because the alternative is a test whose
        // name promises more than it does.
        Assert.Equal([DrawCommandKind.Rectangle, DrawCommandKind.Text], kinds);
    }

    [Fact]
    public void An_empty_string_is_not_text() {
        using var document = Documented();

        var label = document.Root.Add("label");
        label.Text = "";

        document.Update();
        document.Draw();

        // Empty and absent are the same thing here, so a label whose text is cleared to "" behaves
        // like one that never had any rather than like one measuring an empty line.
        Assert.Null(label.Block());
        Assert.Empty(document.Drawing.Commands);
    }

    [Fact]
    public void The_default_face_catches_a_family_nobody_registered() {
        var registry = new FontRegistry();
        Assert.Null(registry.Resolve("Test"));

        registry.Register("Test", Font);

        // The first face registered becomes the default, so a stylesheet with a typo in a family name
        // draws in some font rather than not at all — which is both what a browser does and what
        // makes the typo findable.
        Assert.Same(Font, registry.Default);
        Assert.Same(Font, registry.Resolve("Nonexistent"));
        Assert.Same(Font, registry.Resolve(null));
        Assert.Same(Font, registry.Resolve("  test  "));
    }

    [Fact]
    public void A_glyph_run_is_placed_from_the_start_of_the_line() {
        using var document = Documented("""
            root { width: 400px; height: 200px; flex-direction: column; align-items: flex-start; }
            label { margin-left: 30px; }
        """);

        var here = document.Root.Add("label");
        var there = document.Root.Add("label");

        here.Text = "AB";
        there.Text = "AB";

        document.Update();
        document.Draw();

        var commands = document.Drawing.Commands.Where(static c => c.Kind == DrawCommandKind.Text).ToList();

        // Two labels in different places holding identical glyph runs, because a glyph's position is
        // relative to the start of its line and the command carries where that is. It is what will
        // let the batcher notice that two runs are the same — and it is why the y differs here while
        // every glyph agrees.
        Assert.NotEqual(commands[0].Y, commands[1].Y);

        for (var i = 0; i < commands[0].Length; i++) {
            Assert.Equal(
                document.Drawing.Glyphs[commands[0].Offset + i],
                document.Drawing.Glyphs[commands[1].Offset + i]
            );
        }
    }

    [Fact]
    public void The_font_index_is_stable_within_a_frame() {
        using var document = Documented();

        var first = document.Root.Add("label");
        var second = document.Root.Add("label");

        first.Text = "AB";
        second.Text = "BA";

        document.Update();
        document.Draw();

        // One face used twice is one entry, because the index is what batching will compare and two
        // indices for one font would break every batch between them.
        Assert.Single(document.Drawing.Fonts);
    }

    [Fact]
    public void Text_with_no_colour_declared_is_black() {
        using var document = Documented();

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();
        document.Draw();

        // Rather than transparent, which is what falling through to `default` would give — text that
        // is there, measured, batched and invisible.
        Assert.Equal(Color4.Black, TextCommand(document).Color);
    }

    [Fact]
    public void Text_is_aligned_within_its_content_box_and_not_its_border_box() {
        // ⚠ The padding is uneven on purpose. Centring against the border box puts the run half the
        // padding difference out — which looks exactly like a padding mistake, and is the reason
        // this test measures against a box whose two paddings differ.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Test; width: 200px; padding-left: 40px; padding-right: 0px; }
            .middle { text-align: center; }
            .far { text-align: right; }
        """);

        var start = document.Root.Add("label");
        var middle = document.Root.Add("label", null, "middle");
        var far = document.Root.Add("label", null, "far");

        foreach (var label in new[] { start, middle, far }) {
            label.Text = "AB";
        }

        document.Update();
        document.Draw();

        var commands = document.Drawing.Commands.Where(static c => c.Kind == DrawCommandKind.Text).ToArray();
        Assert.Equal(3, commands.Length);

        // ⚠ 200, not 160. `box-sizing` defaults to content-box, so the declared width *is* the
        // content width and the padding is added outside it — the border box is 240. Alignment that
        // subtracted the padding from the declared width would centre every padded label short.
        const float content = 200f;

        Assert.Equal(240f, start.Width, 1f);
        var run = start.Block()!.Lines[0];

        Assert.Equal(start.AbsoluteLeft + 40f, commands[0].X, 1f);
        Assert.Equal(middle.AbsoluteLeft + 40f + ((content - run.Width) / 2f), commands[1].X, 1f);
        Assert.Equal(far.AbsoluteLeft + 40f + (content - run.Width), commands[2].X, 1f);
    }

    [Fact]
    public void Text_wider_than_its_box_is_not_aligned_anywhere() {
        // Centring negative slack would hide the beginning of the string, and the beginning is the
        // part a reader needs to recognise what has been cut off.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Test; width: 4px; text-align: center; }
        """);

        var label = document.Root.Add("label");
        label.Text = "ABABABAB";

        document.Update();
        document.Draw();

        Assert.True(label.Block()!.Lines[0].Width > label.Width);
        Assert.Equal(label.AbsoluteLeft, TextCommand(document).X, 1f);
    }

    [Fact]
    public void Letter_spacing_widens_the_run_and_the_measurement_with_it() {
        // Tracking has to reach the *measure*, not just the drawing — an element sized from text it
        // then draws wider would clip its own last letter, and the clipping would look like a
        // shaping bug rather than a spacing one.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Test; }
            .wide { letter-spacing: 4px; }
        """);

        var tight = document.Root.Add("label");
        var wide = document.Root.Add("label", null, "wide");

        tight.Text = "AB";
        wide.Text = "AB";

        document.Update();

        var run = wide.Block()!.Lines[0].Runs[0];

        // Two characters, and CSS adds the spacing after the last one as well as between — which is
        // the wart every browser reproduces and this deliberately matches.
        Assert.Equal(2, run.Clusters);
        Assert.Equal(tight.Block()!.Lines[0].Width + 8f, run.Width, Tolerance);
        Assert.True(wide.Width > tight.Width);
    }

    [Fact]
    public void Letter_spacing_is_relative_to_the_elements_own_font_size() {
        // `em` on every property except `font-size` itself means the element's own size, so tracking
        // on a heading is a fraction of the heading rather than of whatever it sits in.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; font-size: 16px; }
            label { font-family: Test; font-size: 32px; letter-spacing: 0.5em; }
        """);

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();

        // Half of 32, not half of the root's 16.
        Assert.Equal(16f, label.Block()!.Lines[0].Runs[0].Tracking, Tolerance);
    }

    [Fact]
    public void A_line_height_replaces_the_fonts_own() {
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Test; font-size: 20px; }
            .tall { line-height: 40px; }
        """);

        var natural = document.Root.Add("label");
        var tall = document.Root.Add("label", null, "tall");

        natural.Text = "AB";
        tall.Text = "AB";

        document.Update();

        Assert.Equal(40f, tall.Block()!.Lines[0].Height, Tolerance);
        Assert.NotEqual(40f, natural.Block()!.Lines[0].Height, 1f);

        // And the element measures itself at it, which is the half of this that matters — a run that
        // reported one height while the layout used another would put every baseline in the wrong
        // place by the difference.
        Assert.Equal(40f, tall.Height, 1f);
    }

    [Fact]
    public void The_extra_height_is_split_above_and_below_the_text() {
        // CSS's half-leading. Putting it all below is what makes a generous `line-height` look like
        // a top margin, and it is the sort of thing that gets called a padding bug for a week.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Test; font-size: 20px; line-height: 60px; }
        """);

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();

        var run = label.Block()!.Lines[0].Runs[0];
        var content = (Font.Metrics.Ascender - Font.Metrics.Descender) * run.Scale;
        var above = run.Baseline - (Font.Metrics.Ascender * run.Scale);
        var below = run.Height - run.Baseline - (-Font.Metrics.Descender * run.Scale);

        Assert.Equal((60f - content) / 2f, above, Tolerance);
        Assert.Equal(above, below, Tolerance);
    }

    [Fact]
    public void A_unitless_line_height_is_a_ratio_each_descendant_applies_to_itself() {
        // ⚠ The whole reason the unitless form exists, and the one place computing a value is not
        // simply resolving it. `1.5` inherits as the *number*; `1.5em` inherits as the length the
        // ancestor resolved. A panel at 10px with a 30px child gets 15 and 45 from the first and
        // 15 and 15 from the second.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            .ratio { font-size: 10px; line-height: 1.5; }
            .fixed { font-size: 10px; line-height: 1.5em; }
            label { font-family: Test; font-size: 30px; }
        """);

        var ratio = document.Root.Add("div", classNames: "ratio");
        var relative = document.Root.Add("div", classNames: "fixed");

        var underRatio = ratio.Add("label");
        var underRelative = relative.Add("label");

        underRatio.Text = "AB";
        underRelative.Text = "AB";

        document.Update();

        Assert.Equal(45f, underRatio.Block()!.Lines[0].Height, Tolerance);
        Assert.Equal(15f, underRelative.Block()!.Lines[0].Height, Tolerance);
    }

    [Fact]
    public void A_percentage_line_height_is_the_ancestors_and_not_the_descendants() {
        // A percentage is *not* the unitless form, which is exactly the trap the unitless form
        // exists to avoid: `150%` resolves once, against the element that declared it.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            .panel { font-size: 10px; line-height: 150%; }
            label { font-family: Test; font-size: 30px; }
        """);

        var label = document.Root.Add("div", classNames: "panel").Add("label");
        label.Text = "AB";

        document.Update();

        Assert.Equal(15f, label.Block()!.Lines[0].Height, Tolerance);
    }

    /// <summary>A <c>line-height</c> in a unit that measures no distance keeps the inherited one.</summary>
    /// <remarks>
    ///     ⚠ <b>The zero this used to answer is the most destructive one in the file and still says
    ///     nothing.</b> <c>ResolveText</c> read the declaration through <c>LengthContext.PixelsPer</c>,
    ///     which answers zero for a duration — so <c>line-height: 3s</c> computed a line height of
    ///     <i>nothing</i> and every line of the element, and of everything beneath it, stacked onto
    ///     one baseline. It reads as a shaping or a leading bug, and the declaration that caused it
    ///     is three properties away from anything a reader would suspect. Left inherited now, because
    ///     an invalid declaration is one a browser drops, and what an element with no
    ///     <c>line-height</c> of its own has is its parent's.
    /// </remarks>
    [Fact]
    public void A_line_height_in_a_unit_that_is_not_a_distance_is_refused_rather_than_zeroed() {
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            .panel { font-size: 20px; line-height: 50px; }
            label { font-family: Test; }
            .odd { line-height: 3s; }
        """);

        var panel = document.Root.Add("div", classNames: "panel");
        var kept = panel.Add("label");
        var odd = panel.Add("label", null, "odd");

        kept.Text = "AB";
        odd.Text = "AB";

        document.Update();

        Assert.Equal(50f, kept.Block()!.Lines[0].Height, Tolerance);
        Assert.Equal(50f, odd.Block()!.Lines[0].Height, Tolerance);
    }

    /// <summary>And a <c>letter-spacing</c> in one does too, where the silent answer was the default.</summary>
    /// <remarks>
    ///     ⚠ <b>The worst-shaped instance of the whole class.</b> Zero tracking <i>is</i>
    ///     <c>letter-spacing: normal</c>, which is the initial value — so a declaration silently read
    ///     as zero produced a frame indistinguishable from one where nobody had written the property,
    ///     with no diagnostic to say otherwise. The inherited four pixels are what makes the two
    ///     outcomes tell apart here: a reader still zeroing the unit comes out at nought, and this
    ///     comes out red.
    /// </remarks>
    [Fact]
    public void A_letter_spacing_in_a_unit_that_is_not_a_distance_is_refused_rather_than_zeroed() {
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            .panel { font-size: 20px; letter-spacing: 4px; }
            label { font-family: Test; }
            .odd { letter-spacing: 2deg; }
            .normal { letter-spacing: normal; }
        """);

        var panel = document.Root.Add("div", classNames: "panel");
        var kept = panel.Add("label");
        var odd = panel.Add("label", null, "odd");
        var cleared = panel.Add("label", null, "normal");

        kept.Text = "AB";
        odd.Text = "AB";
        cleared.Text = "AB";

        document.Update();

        Assert.Equal(4f, kept.Block()!.Lines[0].Runs[0].Tracking, Tolerance);
        Assert.Equal(4f, odd.Block()!.Lines[0].Runs[0].Tracking, Tolerance);

        // ⚠ And `normal` still means nought, which is the outcome the refusal must not swallow: a
        // reader that answered "inherited" for everything it did not resolve would keep four pixels
        // here and break the one spelling CSS gives for turning the property off.
        Assert.Equal(0f, cleared.Block()!.Lines[0].Runs[0].Tracking, Tolerance);
    }

    [Fact]
    public void An_inherited_text_property_changing_rebuilds_the_children_that_never_declared_it() {
        // ⚠ The trap this design creates. `line-height` and `letter-spacing` are inherited outside
        // the cascade now, so a label whose *parent* changed one has an unchanged ComputedStyle —
        // the pass's reference test passes, nothing is rebuilt, and the label keeps measuring itself
        // at the old height for the rest of the document's life.
        using var document = Documented("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            .panel { font-size: 20px; }
            .panel.tall { line-height: 50px; }
            label { font-family: Test; }
        """);

        var panel = document.Root.Add("div", classNames: "panel");
        var label = panel.Add("label");
        label.Text = "AB";

        document.Update();
        Assert.NotEqual(50f, label.Height, 1f);

        panel.AddClass("tall");
        document.Update();

        Assert.Equal(50f, label.Block()!.Lines[0].Height, Tolerance);
        Assert.Equal(50f, label.Height, 1f);
    }

    [Fact]
    public void Letter_spacing_is_added_per_cluster_and_not_per_glyph() {
        // ⚠ The one thing Latin cannot show. This syllable is five code points that shape to more
        // glyphs than clusters, so a per-glyph implementation adds tracking *inside* it — pushing
        // the marks off the letter they belong to and measuring the run too wide. Against "AB" the
        // two implementations agree exactly, which is why this test uses a font that reorders.
        const string syllable = "ಲ್ಲಿ";

        var document = new UiDocument(400f, 200f);
        document.Fonts.Register("Kannada", Kannada);
        document.Load("""
            root { width: 400px; height: 200px; align-items: flex-start; }
            label { font-family: Kannada; letter-spacing: 10px; }
        """);

        using var owned = document;
        var label = document.Root.Add("label");
        label.Text = syllable;

        document.Update();

        var run = label.Block()!.Lines[0].Runs[0];
        var glyphs = new List<PositionedGlyph>();
        run.Place(glyphs);

        Assert.True(
            glyphs.Count > run.Clusters,
            $"the test needs a string with more glyphs than clusters; got {glyphs.Count} and {run.Clusters}"
        );

        // Tracking per cluster, so the width grows by the clusters and not by the glyphs.
        var untracked = new TextRun(run.Font, run.Shaped, run.Size);
        Assert.Equal(untracked.Width + (10f * run.Clusters), run.Width, Tolerance);

        // And exactly which gaps grew: the ones that cross a cluster boundary, and no others. A
        // per-glyph implementation widens every gap by the same amount and would satisfy any check
        // looser than this one — including "each gap grew by nothing or by one step", which every
        // gap growing by one step also satisfies.
        var placed = new List<PositionedGlyph>();
        untracked.Place(placed);

        var boundaries = run.Shaped.Placements().Select(static placement => placement.Cluster).ToArray();
        var shared = 0;

        for (var i = 1; i < glyphs.Count; i++) {
            var grew = glyphs[i].X - glyphs[i - 1].X - (placed[i].X - placed[i - 1].X);
            var crosses = boundaries[i] != boundaries[i - 1];

            if (!crosses) {
                shared++;
            }

            Assert.Equal(crosses ? 10f : 0f, grew, Tolerance);
        }

        Assert.True(shared > 0, "the test needs at least one adjacent pair of glyphs inside one cluster");
    }

    /// <summary>
    ///     A face registered after the interface has already been laid out re-measures the text that
    ///     was laid out without one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The regression is a whole interface of nought-pixel labels.</b> A host that builds its
    ///     document and then installs a font — which is what the editor's does, because the font is
    ///     the window's business and the shell's contents are not — laid out every label against an
    ///     empty registry, measured zero, and never measured again: registering a face changes nothing
    ///     on an element, so nothing is dirty, so nothing re-measures. The symptom is a menu bar and a
    ///     toolbar with the right strings in them, at the right colour, none of which is on the screen.
    /// </remarks>
    [Fact]
    public void A_face_registered_after_the_first_pass_measures_the_text_that_had_none() {
        using var document = new UiDocument(400f, 200f);
        document.Load("root { width: 400px; height: 200px; align-items: flex-start; }");

        var label = document.Root.Add("label");
        label.Text = "AB";

        document.Update();

        // Nothing to shape with, so nothing to measure — the honest outcome, and the state the
        // registration below has to get the document out of.
        Assert.Equal(0f, label.Bounds.Width, Tolerance);
        Assert.Equal(0f, label.Bounds.Height, Tolerance);

        document.Fonts.Register("Test", Font);

        // ⚠ No `Invalidate` and nothing touched on the element: the point is that the pass on its own
        // notices, because a caller that had to know to ask is a caller that will not.
        Assert.True(document.Update());

        // Within a pixel, for the reason the measure test above spells out: the layout snaps every
        // edge to the pixel grid, so an element's width is its text's width rounded.
        var run = label.Block()!.Lines[0];

        Assert.True(run.Width > 0f);
        Assert.Equal(run.Width, label.Width, 1f);
        Assert.Equal(run.Height, label.Height, 1f);

        // And it settles: a second pass with the same faces re-measures nothing, or every frame after
        // a registration would be a cold one.
        Assert.False(document.Update());
    }
}
