// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary><c>caret-color</c>, as the pixels the software rasteriser produced — doc 43 Interactivity.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The consumption gate cannot be this test, and the reason is narrower than usual.</b>
///         Its verdict is "the draw list changed", and changing a caret's colour changes it whichever
///         colour was picked — so it would pass on a reader that took the palette's
///         <c>--caret-color</c> and ignored the class, provided the two happened to differ. What is
///         under test here is the <i>precedence</i>: two properties name this colour, both inherit,
///         and the standard one has to win.
///     </para>
///     <para>
///         ⚠ <b>Green caret, red text, blue field, on black — four colours because three of them are
///         things that could be mistaken for the caret.</b> The caret is a one-pixel-wide bar drawn
///         over the glyphs, so a scan that only asked "is anything green" would be satisfied by a
///         selection band, a focus ring or a border painted in the same hue. Giving every other part
///         its own channel means a green pixel is the caret and nothing else is.
///     </para>
///     <para>
///         ⚠ <b>And the field is focused, which is the whole reason the property measured inert for
///         as long as it did.</b> <c>TextField.OnDraw</c> returns before it draws anything at all
///         unless the field has focus. A fixture that built the field and never focused it would
///         render a perfectly correct picture with no caret in it, and every assertion below would
///         fail for a reason that has nothing to do with the colour.
///     </para>
/// </remarks>
public class CaretColourPixelTests {
    static readonly FontFace Font = LoadFont();

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>How many pixels of each channel a focused field painted.</summary>
    /// <param name="Declarations">What to write on the field, beside the fixture's own rules.</param>
    /// <returns>The count of green pixels, which is the caret and nothing else.</returns>
    static int Caret(string declarations) {
        using var ui = UiTest.Create(240f, 120f);
        ui.Document.Fonts.Register("Test", Font);

        // ⚠ The theme is quoted rather than loaded: this project has no `root` rule declaring the
        // nine tokens, and `field-text { min-height: 1.2em }` is the line that stops an empty field
        // being its padding and nothing else. `--caret-color` is declared on the root *deliberately*
        // — it is the thing `caret-color` has to beat, and a fixture without it would prove only
        // that the standard property is read when nothing competes with it.
        ui.Load(
            $$"""
            root       { width: 240px; height: 120px; background-color: #000000;
                         --caret-color: #ff00ff; font-family: Test; font-size: 24px; }
            textbox    { position: absolute; left: 20px; top: 20px; width: 160px;
                         flex-direction: row; align-items: center; padding: 4px;
                         background-color: #0000c0; color: #c00000; {{declarations}} }
            field-text { flex-shrink: 0; white-space: nowrap; min-height: 1.2em; }
            """
        );

        var field = ui.Document.Root.Add<TextBox>();
        field.Value = "AB";
        ui.Document.Focus(field);
        ui.Frame();

        var image = ui.Capture();
        var green = 0;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var offset = image.Offset(x, y);
                var r = image.Pixels[offset];
                var g = image.Pixels[offset + 1];
                var b = image.Pixels[offset + 2];

                // Dominant green: the field is blue, the glyphs are red, the ground is black and the
                // fallback caret is magenta — which is red *and* blue, so a `g > r && g > b` test
                // separates the wanted caret from the unwanted one as well as from every background.
                if (g > 24 && g > r && g > b) {
                    green++;
                }
            }
        }

        return green;
    }

    /// <summary>A green <c>caret-color</c> paints a green caret.</summary>
    /// <remarks>
    ///     The floor is the fixture's guard rather than a measurement: a caret is one pixel wide and
    ///     about a line tall, so a handful of pixels is a caret and zero is a caret that was not
    ///     drawn. Asserting a range instead would pin the font's line height, which is not what this
    ///     file is about.
    /// </remarks>
    [Fact]
    public void The_standard_property_colours_the_caret() =>
        Assert.True(Caret("caret-color: #00ff00") > 0, "no green pixel: the caret was not painted");

    /// <summary>And the palette's token alone paints no green one, which is what makes the first test mean something.</summary>
    /// <remarks>
    ///     ⚠ <b>The negative half, and without it the assertion above is satisfied by an engine that
    ///     ignores the property entirely.</b> The root declares <c>--caret-color: #ff00ff</c>, so a
    ///     field with no <c>caret-color</c> of its own draws a magenta caret — green nowhere. If this
    ///     ever starts finding green pixels, something other than the caret has become green and the
    ///     scan above has stopped measuring what it claims to.
    /// </remarks>
    [Fact]
    public void Without_it_the_caret_takes_the_palettes_token_and_no_green_is_painted() =>
        Assert.Equal(0, Caret(string.Empty));

    /// <summary>The standard property beats the token where both are written.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the test the whole file exists for.</b> Both names reach the field — one from
    ///     the root by inheritance, one written on the element — and a reader that asked
    ///     <c>--caret-color</c> first would pass both tests above and fail this one. The order is
    ///     stated on <c>TextField.CaretColour</c> along with what it costs.
    /// </remarks>
    [Fact]
    public void The_standard_property_beats_the_palette_token_on_the_same_element() =>
        Assert.True(
            Caret("caret-color: #00ff00; --caret-color: #ff00ff") > 0,
            "the caret took `--caret-color` while a `caret-color` was written on the same element"
        );
}
