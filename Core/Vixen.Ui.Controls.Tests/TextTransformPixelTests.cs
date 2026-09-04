// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>text-transform</c>, as the pixels the software rasteriser produced.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A draw-list assertion cannot settle this one, for the reason
///         <c>TextDecorationPixelTests</c> gives about a bar.</b> "The glyph ids changed" is true of
///         a transform that mapped the wrong characters, of one that dropped an expansion, and of
///         one that uppercased a string it should have titlecased. The oracle that closes all three
///         is <see cref="Uppercasing_straße_draws_exactly_what_writing_STRASSE_draws" />: two
///         documents, one transformed and one not, asserted pixel for pixel — which can only hold if
///         the full case mapping, the seven-character measurement and the shaping of the longer
///         string all agree with the plain path.
///     </para>
///     <para>
///         ⚠ <b>Open Sans rather than a Consortium fixture, and it is the point of the file.</b>
///         <c>TestShapeLana</c> has no <c>ß</c>, so both sides of that comparison would be a
///         .notdef box beside another .notdef box and the test would pass against a case mapping
///         that did nothing at all.
///     </para>
/// </remarks>
public class TextTransformPixelTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.OpenSans-Regular.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "OpenSans");
    }

    /// <summary>White text on black, with whatever <c>text-transform</c> the test is about.</summary>
    static Bitmap Render(string text, string declarations) {
        using var ui = UiTest.Create(320f, 120f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            $$"""
            root { width: 320px; height: 120px; background-color: #000000; }
            .label { position: absolute; left: 16px; top: 24px; font-size: 40px;
                     color: #ffffff; {{declarations}} }
            """
        );

        ui.Create("div", null, "label", "label").Text = text;
        ui.Frame();

        return ui.Capture();
    }

    /// <summary>The rows holding an inked pixel, top and bottom.</summary>
    static (int Top, int Bottom, int Count) Ink(Bitmap image) {
        var top = -1;
        var bottom = -1;
        var count = 0;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                if (image.Pixels[image.Offset(x, y)] < 40) {
                    continue;
                }

                top = top < 0 ? y : top;
                bottom = y;
                count++;
            }
        }

        return (top, bottom, count);
    }

    static bool Same(Bitmap left, Bitmap right) {
        if (left.Width != right.Width || left.Height != right.Height) {
            return false;
        }

        return left.Pixels.SequenceEqual(right.Pixels);
    }

    /// <summary>The characters this file measures are drawn, and are not <c>.notdef</c> boxes.</summary>
    /// <remarks>
    ///     ⚠ Written for the reason <c>TextDecorationPixelTests</c> writes its own: an ellipsis test
    ///     in this repository once passed seven of eight cases against a tofu box. Without this the
    ///     comparisons below could be measuring the placement of a rectangle the font drew to say it
    ///     had nothing.
    /// </remarks>
    [Fact]
    public void The_font_draws_every_character_this_file_measures() {
        foreach (var character in "agAGSTREß") {
            Assert.NotEqual(0, Font.GlyphFor(character));
        }
    }

    /// <summary>
    ///     ⚠ <b>The closed-form oracle: the transform is exactly the string substitution it claims to
    ///     be.</b> Six characters uppercased into seven, laid out and drawn, must land on the same
    ///     pixels as the seven characters written out — and no weaker relation distinguishes a
    ///     working full case mapping from <c>STRAßE</c>, which is what .NET's own uppercase produces.
    /// </summary>
    [Fact]
    public void Uppercasing_straße_draws_exactly_what_writing_STRASSE_draws() {
        var transformed = Render("straße", "text-transform: uppercase;");
        var written = Render("STRASSE", string.Empty);

        Assert.True(Same(transformed, written), "the transformed picture is the written one");
    }

    /// <summary>
    ///     And it is not the picture the simple mapping would have made — which is the assertion that
    ///     goes red if <see cref="SpecialCasingTable" /> is ever bypassed.
    /// </summary>
    [Fact]
    public void And_it_is_not_the_picture_the_frameworks_own_uppercase_would_have_made() {
        var transformed = Render("straße", "text-transform: uppercase;");
        var simple = Render("straße".ToUpperInvariant(), string.Empty);

        Assert.False(Same(transformed, simple), "STRASSE is not STRAßE");
    }

    /// <summary>
    ///     ⚠ <b>A relation chosen to fail for the neighbouring case.</b> <c>ag</c> has a descender
    ///     and an x-height; <c>AG</c> has neither and reaches the cap height. So uppercasing must
    ///     move the ink <i>up</i> at both ends, which lowercasing and no transform both fail.
    /// </summary>
    [Fact]
    public void Uppercase_lifts_the_ink_off_the_descender_and_up_to_the_cap_height() {
        var plain = Ink(Render("ag", string.Empty));
        var loud = Ink(Render("ag", "text-transform: uppercase;"));

        Assert.True(loud.Top < plain.Top, $"the capitals start higher: {loud.Top} vs {plain.Top}");
        Assert.True(loud.Bottom < plain.Bottom, $"and the descender is gone: {loud.Bottom} vs {plain.Bottom}");
    }

    [Fact]
    public void Lowercase_puts_the_descender_back() {
        var loud = Ink(Render("AG", string.Empty));
        var quiet = Ink(Render("AG", "text-transform: lowercase;"));

        Assert.True(quiet.Bottom > loud.Bottom, $"the descender reaches lower: {quiet.Bottom} vs {loud.Bottom}");
    }

    /// <summary>
    ///     ⚠ <b>Every keyword by hand.</b> The consumption gate scores the property, so
    ///     <c>capitalize</c> quietly falling through to <c>uppercase</c> — or to nothing — is a green
    ///     row. Here it has to be a third picture, different from both of the others.
    /// </summary>
    [Fact]
    public void Capitalize_is_a_third_picture_and_not_either_of_the_other_two() {
        var plain = Render("ag ag", string.Empty);
        var loud = Render("ag ag", "text-transform: uppercase;");
        var titled = Render("ag ag", "text-transform: capitalize;");

        Assert.False(Same(titled, plain), "capitalize changed something");
        Assert.False(Same(titled, loud), "and it is not uppercase");

        // The closed form for this one: `Ag Ag` written out is what `capitalize` of `ag ag` means.
        Assert.True(Same(titled, Render("Ag Ag", string.Empty)), "capitalize of `ag ag` is `Ag Ag`");
    }

    /// <summary>
    ///     <c>normal-case</c>'s value, which has to be a real opt-out rather than an inert keyword —
    ///     it is the class an author writes to undo an inherited transform.
    /// </summary>
    [Fact]
    public void None_draws_what_was_written() {
        Assert.True(
            Same(Render("ag AG", "text-transform: none;"), Render("ag AG", string.Empty)),
            "`none` is what no declaration is"
        );
    }

    /// <summary>
    ///     ⚠ <b>Inherited, and this is the arrangement every markup panel is in.</b> A
    ///     <c>.vxml</c> interpolation emits its text as a child, so a transform written on the
    ///     container reaches the glyphs only by inheriting — the same reason
    ///     <c>text-decoration-line</c> is in <c>InheritedProperties</c> although CSS does not
    ///     inherit it.
    /// </summary>
    [Fact]
    public void A_transform_on_the_container_reaches_the_text_in_the_child() {
        using var ui = UiTest.Create(320f, 120f);
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            """
            root { width: 320px; height: 120px; background-color: #000000; }
            .box   { position: absolute; left: 16px; top: 24px; text-transform: uppercase; }
            .label { font-size: 40px; color: #ffffff; }
            """
        );

        var box = ui.Create("div", null, "box", "box");
        ui.Create("div", box, "label", "label").Text = "ag";
        ui.Frame();

        Assert.True(Same(ui.Capture(), Render("AG", string.Empty)), "the child drew the capitals");
    }
}
