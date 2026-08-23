// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary><c>text-decoration</c> from the cascade to the draw list.</summary>
/// <remarks>
///     <para>
///         <b>What is judged here is the arithmetic, and the numbers it is judged against were read
///         out of the font files rather than out of this engine.</b> <c>TestShapeLana</c> states an
///         underline 90 design units thick on a 2048-unit grid and <c>Zycon</c> states one of 20 on
///         the same grid — a factor of four and a half, in two faces a document could mix — so a bar
///         drawn from a constant, from the wrong table, or at the wrong scale disagrees with one of
///         them however plausible it looks on its own. The same two numbers are asserted from the
///         other end in <c>Vixen.Ui.Text.Tests.DecorationMetricsTests</c>.
///     </para>
///     <para>
///         ⚠ <b>The pixels are asserted somewhere else, and both halves are needed.</b> This file can
///         say that a rectangle of the right size was asked for at the right place; it cannot say
///         that anything drew it, or that it landed on the side of the baseline it was supposed to.
///         <c>Vixen.Ui.Controls.Tests.TextDecorationPixelTests</c> is the half that reads the picture,
///         and it is the half that would catch a bar the geometry builder dropped.
///     </para>
/// </remarks>
public class TextDecorationTests {
    const float Tolerance = 0.01f;

    /// <summary>An underline 90 units thick, centred 130 below the baseline, on a 2048 grid.</summary>
    static readonly FontFace Font = LoadFont("TestShapeLana.ttf", "TestShapeLana");

    /// <summary>The same grid and an underline 20 units thick — four and a half times finer.</summary>
    static readonly FontFace Hairline = LoadFont("Zycon.ttf", "Zycon");

    static FontFace LoadFont(string resource, string name) {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"Vixen.Ui.Tests.Fonts.{resource}")
            ?? throw new InvalidOperationException($"the test font '{resource}' is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: name);
    }

    /// <summary>A label carrying whatever declarations the test is about.</summary>
    /// <param name="declarations">What goes on the label.</param>
    /// <param name="face">Which face to register, defaulting to the thick-underlined one.</param>
    /// <remarks>
    ///     ⚠ <b>No background anywhere, so every <see cref="DrawCommandKind.Rectangle" /> in the list
    ///     is a decoration bar.</b> A root with a fill would put one command in front of them and the
    ///     positional assertions below would be reading it.
    /// </remarks>
    static UiDocument Documented(string declarations, FontFace? face = null) {
        var document = new UiDocument(400f, 200f);
        document.Fonts.Register("Test", face ?? Font);
        document.Load($"root {{ width: 400px; height: 200px; align-items: flex-start; }} label {{ {declarations} }}");

        var label = document.Root.Add("label");
        label.Text = "AB";
        document.Update();
        document.Draw();

        return document;
    }

    static List<DrawCommand> Bars(UiDocument document) =>
        [.. document.Drawing.Commands.Where(static command => command.Kind == DrawCommandKind.Rectangle)];

    static DrawCommand Glyphs(UiDocument document) =>
        Assert.Single(document.Drawing.Commands, static command => command.Kind == DrawCommandKind.Text);

    /// <summary>Undecorated text emits no bars, which is the case that must cost nothing.</summary>
    [Fact]
    public void Text_with_no_decoration_emits_no_rectangles() {
        using var document = Documented("font-size: 40px;");

        Assert.Empty(Bars(document));
        Assert.NotEqual(0, Glyphs(document).Length);
    }

    /// <summary>The bar's thickness is the face's, and two faces disagree about it by four and a half.</summary>
    /// <remarks>
    ///     ⚠ <b>The expected numbers are <c>90 × size / 2048</c> and <c>20 × size / 2048</c>, written
    ///     out rather than read back from <see cref="FontFace.Decoration" />.</b> Asking the same
    ///     property the implementation asked would pass with the two faces swapped, with the strikeout
    ///     table read in place of the underline one, and with the scale applied twice.
    /// </remarks>
    [Fact]
    public void The_thickness_comes_from_the_face_and_not_from_a_constant() {
        using var thick = Documented("font-size: 400px; text-decoration-line: underline;");
        using var thin = Documented("font-size: 400px; text-decoration-line: underline;", Hairline);

        Assert.Equal(90f * 400f / 2048f, Assert.Single(Bars(thick)).Height, Tolerance);
        Assert.Equal(20f * 400f / 2048f, Assert.Single(Bars(thin)).Height, Tolerance);
    }

    /// <summary>A face asking for a sub-pixel hairline gets one whole pixel instead.</summary>
    /// <remarks>
    ///     <c>Zycon</c> at 40px asks for 0.39 of a pixel, which the rasteriser draws as a grey smear.
    ///     The floor is on the <c>auto</c> path only — see <see cref="TextRun.Bar" /> — so this is the
    ///     assertion that would fail if the floor were moved to cover an authored thickness too.
    /// </remarks>
    [Fact]
    public void An_auto_thickness_below_a_pixel_is_floored_and_an_authored_one_is_not() {
        using var automatic = Documented("font-size: 40px; text-decoration-line: underline;", Hairline);
        using var authored = Documented(
            "font-size: 40px; text-decoration-line: underline; text-decoration-thickness: 0.5px;",
            Hairline
        );

        Assert.True(20f * 40f / 2048f < 1f, "the fixture only means something while the face asks for less than a pixel");
        Assert.Equal(1f, Assert.Single(Bars(automatic)).Height, Tolerance);
        Assert.Equal(0.5f, Assert.Single(Bars(authored)).Height, Tolerance);
    }

    /// <summary>A zero thickness is a request for no line, and costs no command.</summary>
    /// <remarks>
    ///     <c>decoration-0</c> is a class v4 ships. Emitting the empty rectangle would draw nothing
    ///     and would make its draw list differ from <c>no-underline</c>'s while the two pictures are
    ///     identical — which is a frame diff told something untrue.
    /// </remarks>
    [Fact]
    public void A_zero_thickness_emits_nothing_at_all() {
        using var document = Documented(
            "font-size: 40px; text-decoration-line: underline; text-decoration-thickness: 0px;"
        );

        Assert.Empty(Bars(document));
        Assert.NotEqual(0, Glyphs(document).Length);
    }

    /// <summary>The three lines are at three different heights, and each on the right side.</summary>
    /// <remarks>
    ///     ⚠ <b>Relative to the glyph command's own baseline, which is the only anchor that is not
    ///     this code's arithmetic repeated.</b> <see cref="DrawCommand.Y" /> on a text command <i>is</i>
    ///     the baseline — see <c>DrawListBuilder.EmitText</c> — so an underline above it or an
    ///     overline below it fails here without anything having to know where the label was laid out.
    /// </remarks>
    [Theory]
    [InlineData("underline")]
    [InlineData("overline")]
    [InlineData("line-through")]
    public void Each_line_sits_on_its_own_side_of_the_baseline(string line) {
        using var document = Documented($"font-size: 40px; text-decoration-line: {line};");

        var baseline = Glyphs(document).Y;
        var bar = Assert.Single(Bars(document));

        switch (line) {
            case "underline":
                Assert.True(bar.Y > baseline, $"an underline belongs below the baseline, and this one is at {bar.Y - baseline}");
                break;
            case "overline":
                Assert.True(bar.Y + bar.Height < baseline, "an overline belongs above the baseline");
                break;
            default:
                // Above the baseline, and not so far above that it has left the glyphs — the face
                // puts its strikeout at 530 units against an x-height of 1120.
                Assert.True(bar.Y + bar.Height < baseline, "a line-through belongs above the baseline");
                Assert.True(bar.Y > baseline - (Font.Metrics.Ascender * 40f / Font.UnitsPerEm), "and inside the ascent");
                break;
        }
    }

    /// <summary>An underline goes under the glyphs and a line-through goes over them.</summary>
    /// <remarks>
    ///     ⚠ <b>CSS Text Decoration 3 § 4.1's painting order, and the only reason a descender
    ///     interrupts an underline.</b> Both orders draw a plausible picture, and the wrong one is
    ///     invisible until a <c>g</c> sits on the line — so it is asserted on the list, where it is
    ///     unambiguous, rather than left to a bitmap where an opaque colour hides it.
    /// </remarks>
    [Fact]
    public void The_underline_is_painted_before_the_glyphs_and_the_line_through_after() {
        using var document = Documented("font-size: 40px; text-decoration-line: underline line-through;");

        var commands = document.Drawing.Commands;
        var text = commands.ToList().FindIndex(static command => command.Kind == DrawCommandKind.Text);
        var bars = Bars(document);

        Assert.Equal(2, bars.Count);
        Assert.True(text > 0, "the underline should have been emitted before the glyph run");
        Assert.Equal(DrawCommandKind.Rectangle, commands[text - 1].Kind);
        Assert.Equal(DrawCommandKind.Rectangle, commands[text + 1].Kind);

        // The one before is the underline — below the baseline — and the one after is the strikeout.
        Assert.True(commands[text - 1].Y > commands[text].Y);
        Assert.True(commands[text + 1].Y < commands[text].Y);
    }

    /// <summary>A thicker decoration is thicker, and a doubled one is two bars.</summary>
    [Fact]
    public void Thickness_and_style_reach_the_command() {
        using var thin = Documented("font-size: 40px; text-decoration-line: underline; text-decoration-thickness: 1px;");
        using var thick = Documented("font-size: 40px; text-decoration-line: underline; text-decoration-thickness: 4px;");
        using var doubled = Documented(
            "font-size: 40px; text-decoration-line: underline; text-decoration-thickness: 2px; text-decoration-style: double;"
        );

        Assert.Equal(1f, Assert.Single(Bars(thin)).Height, Tolerance);
        Assert.Equal(4f, Assert.Single(Bars(thick)).Height, Tolerance);

        var pair = Bars(doubled);
        Assert.Equal(2, pair.Count);
        Assert.Equal(2f, pair[0].Height, Tolerance);
        Assert.Equal(2f, pair[1].Height, Tolerance);

        // A gap of one thickness between them, or the two bars are one thick bar wearing a plural.
        Assert.Equal(pair[0].Y + 4f, pair[1].Y, Tolerance);
    }

    /// <summary><c>text-underline-offset</c> moves the underline down and nothing else.</summary>
    [Fact]
    public void The_offset_moves_the_underline_and_leaves_the_strikeout_alone() {
        using var plain = Documented("font-size: 40px; text-decoration-line: underline line-through;");
        using var shifted = Documented(
            "font-size: 40px; text-decoration-line: underline line-through; text-underline-offset: 6px;"
        );

        var before = Bars(plain);
        var after = Bars(shifted);

        Assert.Equal(6f, after[0].Y - before[0].Y, Tolerance);
        Assert.Equal(before[1].Y, after[1].Y, Tolerance);
    }

    /// <summary>An em offset resolves against the element's own font size.</summary>
    [Fact]
    public void A_relative_offset_resolves_against_the_font_size() {
        using var plain = Documented("font-size: 40px; text-decoration-line: underline;");
        using var relative = Documented("font-size: 40px; text-decoration-line: underline; text-underline-offset: 0.25em;");

        Assert.Equal(10f, Assert.Single(Bars(relative)).Y - Assert.Single(Bars(plain)).Y, Tolerance);
    }

    /// <summary>The bar takes the text's colour unless it is given one of its own.</summary>
    [Fact]
    public void The_colour_falls_back_to_the_text_and_can_be_overridden() {
        using var inherited = Documented("font-size: 40px; color: #ff0000; text-decoration-line: underline;");
        using var own = Documented(
            "font-size: 40px; color: #ff0000; text-decoration-line: underline; text-decoration-color: #0000ff;"
        );

        Assert.Equal(new Color4(1f, 0f, 0f, 1f), Assert.Single(Bars(inherited)).Color);
        Assert.Equal(new Color4(0f, 0f, 1f, 1f), Assert.Single(Bars(own)).Color);
    }

    /// <summary>The bar spans the line, not each run, so it takes the line's own width.</summary>
    [Fact]
    public void The_bar_spans_the_line_and_starts_where_the_glyphs_do() {
        using var document = Documented("font-size: 40px; text-decoration-line: underline;");

        var glyphs = Glyphs(document);
        var bar = Assert.Single(Bars(document));

        Assert.Equal(glyphs.X, bar.X, Tolerance);
        Assert.Equal(glyphs.Width, bar.Width, Tolerance);
    }

    /// <summary>A decoration moves nothing that was measured.</summary>
    /// <remarks>
    ///     ⚠ <b>The hazard that made this worth its own test.</b> <c>TextLayout.Measure</c> reports a
    ///     whole number of <i>device</i> pixels, so anything that perturbed a measured width or height
    ///     by a fraction could round a block up and move every element after it. CSS says a decoration
    ///     does not affect layout; this is that claim, made against the two numbers the pixel grid
    ///     reads.
    /// </remarks>
    [Fact]
    public void Decorating_text_does_not_change_what_it_measures() {
        using var plain = Documented("font-size: 40px;");
        using var decorated = Documented(
            "font-size: 40px; text-decoration-line: underline overline line-through;"
            + " text-decoration-thickness: 8px; text-underline-offset: 8px;"
        );

        var before = plain.Root.Children[0];
        var after = decorated.Root.Children[0];

        Assert.Equal(before.Width, after.Width, Tolerance);
        Assert.Equal(before.Height, after.Height, Tolerance);
        Assert.Equal(3, Bars(decorated).Count);
    }

    /// <summary>The class written on a container reaches the text child a markup file emits.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole feature, for every <c>.vxml</c> panel.</b> An interpolation emits the text
    ///     as a <i>child</i> element, so a decoration that did not inherit would work on
    ///     <c>&lt;label text="…" /&gt;</c> and silently do nothing on the form everything is actually
    ///     written in. See <c>InheritedProperties</c>, where the five names are, and what it costs.
    /// </remarks>
    [Fact]
    public void A_decoration_on_a_container_reaches_the_text_in_its_child() {
        using var document = new UiDocument(400f, 200f);
        document.Fonts.Register("Test", Font);
        document.Load("root { width: 400px; height: 200px; align-items: flex-start; }"
            + " panel { font-size: 40px; text-decoration-line: underline; }");

        var panel = document.Root.Add("panel");
        panel.Add("text").Text = "AB";
        document.Update();
        document.Draw();

        Assert.Single(Bars(document));
    }

    /// <summary>And a child can escape it, which is Vixen's deviation and is deliberate.</summary>
    [Fact]
    public void And_the_child_can_turn_it_off_again() {
        using var document = new UiDocument(400f, 200f);
        document.Fonts.Register("Test", Font);
        document.Load("root { width: 400px; height: 200px; align-items: flex-start; }"
            + " panel { font-size: 40px; text-decoration-line: underline; }"
            + " text { text-decoration-line: none; }");

        var panel = document.Root.Add("panel");
        panel.Add("text").Text = "AB";
        document.Update();
        document.Draw();

        Assert.Empty(Bars(document));
    }

    /// <summary>The <c>text-decoration</c> shorthand reaches the longhands the engine reads.</summary>
    /// <remarks>
    ///     ⚠ <b>Written because a real stylesheet in this repository has been using it the whole
    ///     time.</b> <c>Editor/Vixen.Editor.AssetEditors/AssetEditorTheme.vcss</c> strikes an
    ///     overridden row through with the shorthand, and it drew nothing for as long as nothing read
    ///     the property. The builder interns only the longhands, on the argument that ExCSS expands a
    ///     shorthand while parsing — which is a claim about somebody else's library, so it is
    ///     measured here rather than asserted in a comment. If ExCSS ever stops expanding this one,
    ///     that rule goes quiet again and this is what says so.
    /// </remarks>
    [Fact]
    public void The_shorthand_the_editor_theme_writes_reaches_the_longhand() {
        using var document = Documented("font-size: 40px; text-decoration: line-through;");

        var bar = Assert.Single(Bars(document));
        Assert.True(bar.Y + bar.Height < Glyphs(document).Y, "the strikeout should be above the baseline");
    }
}
