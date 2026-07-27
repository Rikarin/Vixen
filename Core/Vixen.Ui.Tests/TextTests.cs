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
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
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

        var run = label.Run()!;
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
        Assert.Equal(small.Run()!.Width * 2f, big.Run()!.Width, Tolerance);
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
        var run = label.Run()!;

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
        Assert.Null(label.Run());
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
        Assert.True(document.Shaping.Hits > 1);
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
        Assert.Null(label.Run());
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
}
