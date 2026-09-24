// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A <c>text</c> leaf takes its colour from the element it is in, under the control theme (#1372).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Markup puts an element's words in a child <c>text</c> element</b> —
///         <c>BuildContext.Text</c>, for a literal and for an interpolation alike — so a
///         <c>color:</c> written on the element reaches the glyphs only by inheritance. The control
///         theme matched that child directly with <c>text { color: var(--text); }</c>, and a matched
///         rule beats an inherited value: a colour on <c>statistic-warning</c> or
///         <c>keybindings-status.conflict</c> changed the parent's computed style and drew nothing.
///         Several sheets had worked round it one tag at a time with <c>tag &gt; text</c> rules.
///     </para>
///     <para>
///         ⚠ <b>The issue said "an interpolation" and it is any markup text.</b> The emitter turns a
///         literal into the same call (<c>ComponentEmitter.EmitNode</c>'s <c>BoundText</c> arm), so
///         <c>&lt;x&gt;Words&lt;/x&gt;</c> was exactly as deaf to <c>x { color }</c> as
///         <c>&lt;x&gt;@Words&lt;/x&gt;</c>. The element built here is what either arm builds.
///     </para>
/// </remarks>
public class TextColourInheritanceTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    static Color4 Colour(UiDocument document, UiElement element) =>
        document.ColorOf(element.Style, document.PropertyId("color"))
        ?? throw new InvalidOperationException($"<{element.Tag}> resolved no colour");

    /// <summary>A colour on the element is the colour of the words markup put inside it.</summary>
    [Fact]
    public void A_colour_on_an_element_reaches_the_text_leaf_inside_it() {
        using var fixture = new ControlFixture(css: "status.warning { color: #ff8000; } probe { color: var(--text); }");

        var status = fixture.Document.Root.Add("status");
        status.AddClass("warning");

        var words = fixture.Document.Create("text", status);
        words.Text = "Four archetypes hold chunks";

        var probe = fixture.Document.Root.Add("probe");

        fixture.Update();

        // The two colours have to differ, or the equality below could not tell inheritance from the
        // leaf's own rule.
        Assert.NotEqual(Colour(fixture.Document, probe), Colour(fixture.Document, status));
        Assert.Equal(Colour(fixture.Document, status), Colour(fixture.Document, words));
    }

    /// <summary>
    ///     The control: with no colour anywhere above it a text leaf is still the theme's text colour,
    ///     because <c>root</c> declares <c>color: var(--text)</c> and everything inherits it — which is
    ///     what made the leaf's own rule redundant everywhere it was right.
    /// </summary>
    [Fact]
    public void A_text_leaf_with_no_colour_above_it_is_the_theme_text_colour() {
        using var fixture = new ControlFixture(css: "probe { color: var(--text); }");

        var words = fixture.Document.Create("text", fixture.Document.Root);
        words.Text = "Plain";

        var probe = fixture.Document.Root.Add("probe");

        fixture.Update();

        Assert.Equal(Colour(fixture.Document, probe), Colour(fixture.Document, words));
    }

    /// <summary>The two variants are still the text leaf's own, and still win over what it would inherit.</summary>
    [Theory]
    [InlineData("variant-subtle", "--text-muted")]
    [InlineData("variant-danger", "--danger")]
    public void A_variant_on_the_text_leaf_still_colours_it(string variant, string token) {
        using var fixture = new ControlFixture(css: $"status {{ color: #ff8000; }} probe {{ color: var({token}); }}");

        var status = fixture.Document.Root.Add("status");
        var words = fixture.Document.Create("text", status);
        words.Text = "Words";
        words.AddClass(variant);

        var probe = fixture.Document.Root.Add("probe");

        fixture.Update();

        Assert.Equal(Colour(fixture.Document, probe), Colour(fixture.Document, words));
        Assert.NotEqual(Colour(fixture.Document, status), Colour(fixture.Document, words));
    }

    /// <summary>
    ///     ⚠ <b>And on the picture</b>, because a computed colour is what the defect already had right
    ///     on the parent. Orange words on black, drawn by the software rasteriser the screenshot suites
    ///     use: the most inked pixel is the parent's colour. The theme's text colour on the light palette
    ///     is near black, so before the fix the brightest pixel of the whole picture was (3, 3, 4).
    /// </summary>
    [Fact]
    public void The_words_are_drawn_in_the_colour_of_the_element_they_are_in() {
        using var ui = UiTest.Create(240f, 60f);
        ui.Document.Fonts.Register("Test", Font);
        ControlTheme.Install(ui.Document);

        ui.Load(
            """
            root   { width: 240px; height: 60px; background-color: #000000; }
            status { position: absolute; left: 10px; top: 10px; font-family: Test; font-size: 32px; color: #ff8000; }
            """
        );

        var status = ui.Create("status");
        ui.Document.Create("text", status).Text = "Wll";

        ui.Frame();

        var image = ui.Capture();
        var (r, g, b) = (0, 0, 0);

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var at = image.Offset(x, y);

                if (image.Pixels[at] + image.Pixels[at + 1] + image.Pixels[at + 2] > r + g + b) {
                    (r, g, b) = (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
                }
            }
        }

        // The capture holds the colour as the renderer works in it — #ff8000's green is 0.216 there,
        // not 0.502 — so the expectation is the parent's own computed colour rather than a literal.
        var want = Colour(ui.Document, status);

        Assert.True(r > 200, $"nothing near full coverage was drawn: ({r}, {g}, {b})");
        Assert.InRange(g, (int)(want.G * 255f) - 8, (int)(want.G * 255f) + 8);
        Assert.True(b < 10, $"the most inked pixel is ({r}, {g}, {b}), not the parent's {want}");
    }
}
