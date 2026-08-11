// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>The picture, checked by reading pixels rather than by comparing it with another picture.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A reference image cannot bootstrap itself.</b> Committing the first PNG that came out
///         is committing whatever came out first, so what a screenshot claims has to be established
///         some other way before anything is committed — which is the argument
///         <c>Vixen.Graphics.Golden.Tests</c>' README makes about properties, applied here. These
///         assert the properties: the box is where the layout put it, it is the colour the draw list
///         says, a border is hollow, a clip removes what is outside it, and two runs agree byte for
///         byte.
///     </para>
///     <para>
///         Colours are read off the <see cref="DrawCommand" /> rather than written as literals. The
///         question these tests ask is whether the rasteriser draws what the draw list said, not
///         whether the cascade converts <c>#3b82f6</c> the way anybody remembers — the second is
///         <c>Vixen.Ui.Styling</c>'s to answer and would make these fail for the wrong reason.
///     </para>
/// </remarks>
public class RasterizerTests {
    const int Side = 64;

    static readonly Color4 Background = new(0f, 0f, 0f, 1f);

    static UiTest Opened(string css) {
        var ui = UiTest.Create(
            Side,
            Side,
            new UiTestOptions { Background = Background }
        );

        ui.Load($"root {{ width: {Side}px; height: {Side}px; }} {css}");
        return ui;
    }

    static DrawCommand CommandOf(UiTest ui, DrawCommandKind kind) =>
        ui.Document.Drawing.Commands.First(command => command.Kind == kind);

    static (byte R, byte G, byte B, byte A) Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(x, y);

        return (
            image.Pixels[offset],
            image.Pixels[offset + 1],
            image.Pixels[offset + 2],
            image.Pixels[offset + 3]
        );
    }

    static byte Quantised(float channel) => (byte)Math.Clamp(MathF.Round(channel * 255f), 0f, 255f);

    [Fact]
    public void A_filled_box_lands_where_the_layout_put_it_in_the_colour_the_draw_list_says() {
        using var ui = Opened(".box { width: 20px; height: 10px; background-color: #3b82f6; }");
        ui.Create("div", ui.Document.Root, null, "box");
        ui.Frame();

        var command = CommandOf(ui, DrawCommandKind.Rectangle);
        var image = ui.Capture();

        Assert.Equal(0f, command.X, 0.001f);
        Assert.Equal(20f, command.Width, 0.001f);

        // Well inside it.
        var inside = Pixel(image, 10, 5);
        Assert.Equal(Quantised(command.Color.R), inside.R);
        Assert.Equal(Quantised(command.Color.G), inside.G);
        Assert.Equal(Quantised(command.Color.B), inside.B);
        Assert.Equal(255, inside.A);

        // Well outside it, on both axes, which is what catches a picture drawn upside down or a
        // rectangle whose width and height were swapped.
        Assert.Equal((0, 0, 0, 255), Pixel(image, 40, 5));
        Assert.Equal((0, 0, 0, 255), Pixel(image, 10, 40));
    }

    [Fact]
    public void The_background_fills_everything_nothing_was_drawn_on() {
        using var ui = UiTest.Create(
            8,
            8,
            new UiTestOptions { Background = new Color4(0.25f, 0.5f, 0.75f, 1f) }
        );

        var image = ui.Capture();

        Assert.All(
            Enumerable.Range(0, 64),
            i => Assert.Equal((Quantised(0.25f), Quantised(0.5f), Quantised(0.75f), (byte)255), Pixel(image, i % 8, i / 8))
        );
    }

    [Fact]
    public void A_border_is_hollow() {
        using var ui = Opened("""
            .framed {
                width: 30px;
                height: 30px;
                border-width: 3px;
                border-style: solid;
                border-color: #ffffff;
            }
        """);

        ui.Create("div", ui.Document.Root, null, "framed");
        ui.Frame();

        var image = ui.Capture();

        // ⚠ The assertion that separates "a border" from "a filled box the colour of the border",
        // which is what a shader taking the border as a second shape rather than as the difference
        // of two coverages would draw.
        Assert.True(Pixel(image, 1, 15).R > 200, "the left edge should be drawn");
        Assert.True(Pixel(image, 15, 1).R > 200, "the top edge should be drawn");
        Assert.Equal((byte)0, Pixel(image, 15, 15).R);
    }

    [Fact]
    public void A_corner_radius_takes_the_corner_off() {
        using var ui = Opened(".round { width: 40px; height: 40px; background-color: #ffffff; border-radius: 16px; }");
        ui.Create("div", ui.Document.Root, null, "round");
        ui.Frame();

        var image = ui.Capture();

        // The very corner is outside a sixteen-pixel radius; the middle of each edge is not.
        Assert.Equal((byte)0, Pixel(image, 0, 0).R);
        Assert.True(Pixel(image, 20, 1).R > 200, "the middle of the top edge should be filled");
        Assert.True(Pixel(image, 1, 20).R > 200, "the middle of the left edge should be filled");
        Assert.True(Pixel(image, 20, 20).R > 200, "the centre should be filled");
    }

    [Fact]
    public void A_clip_removes_what_is_outside_it() {
        using var ui = Opened("""
            .window { width: 20px; height: 20px; overflow: hidden; }
            .wide { position: absolute; left: 0; top: 0; width: 60px; height: 10px; background-color: #ffffff; }
        """);

        var window = ui.Create("div", ui.Document.Root, null, "window");
        ui.Create("div", window, null, "wide");
        ui.Frame();

        var image = ui.Capture();

        // ⚠ A clip is a scissor, and a scissor that is not set is a clip that does not clip. Inside
        // the window the strip is drawn; five pixels past its right edge it must not be.
        Assert.True(Pixel(image, 10, 5).R > 200, "inside the clip the strip should be drawn");
        Assert.Equal((byte)0, Pixel(image, 25, 5).R);
    }

    [Fact]
    public void Text_draws_ink_where_a_glyph_run_says_and_nowhere_else() {
        using var ui = Opened(".label { width: 60px; height: 20px; font-family: Test; font-size: 16px; color: #ffffff; }");
        ui.Document.Fonts.Register("Test", LoadFont());

        var label = ui.Create("div", ui.Document.Root, null, "label");
        label.Text = "III";
        ui.Frame();

        var image = ui.Capture();
        var ink = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (Pixel(image, x, y).R > 40) {
                    ink++;
                }
            }
        }

        // The claim is deliberately weak — that the glyph path draws anything at all, through the
        // real shaper, the real atlas and the median-of-three the text shader does. What it is
        // strong enough to catch is an atlas sampled as a colour, a run placed at the origin, or a
        // baseline flipped, all of which leave the strip empty or fill it.
        Assert.InRange(ink, 1, Side * 20);
    }

    [Fact]
    public void Two_renderings_of_the_same_interface_are_byte_identical() {
        using var ui = Opened(".box { width: 20px; height: 10px; background-color: #3b82f6; border-radius: 4px; }");
        ui.Create("div", ui.Document.Root, null, "box");
        ui.Frame();

        // ⚠ What lets a screenshot be compared exactly rather than perceptually. A GPU suite cannot
        // claim this across drivers; this renderer can, and the whole tolerance story rests on it.
        Assert.Equal(ui.Capture().Pixels, ui.Capture().Pixels);
    }

    [Fact]
    public void A_png_survives_being_written_and_read_back() {
        using var ui = Opened(".box { width: 24px; height: 12px; background-color: #3b82f6; border-radius: 5px; }");
        ui.Create("div", ui.Document.Root, null, "box");
        ui.Frame();

        var image = ui.Capture();
        var decoded = PngCodec.Decode(PngCodec.Encode(image));

        Assert.Equal(image.Width, decoded.Width);
        Assert.Equal(image.Height, decoded.Height);
        Assert.Equal(image.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Comparison_counts_pixels_over_a_threshold_and_says_where_the_worst_one_is() {
        var a = new Bitmap(2, 1, [0, 0, 0, 255, 0, 0, 0, 255]);
        var b = new Bitmap(2, 1, [0, 0, 0, 255, 40, 0, 0, 255]);

        var exact = ImageComparer.Compare(a, b, ImageTolerance.Exact);

        Assert.False(exact.Matches);
        Assert.Equal(1, exact.DifferingPixels);
        Assert.Equal(40, exact.WorstChannel);
        Assert.Equal(1, exact.WorstX);

        // ⚠ And a mean-squared error over this image would be tiny — one channel of one pixel out of
        // two — which is exactly the bright-artefact-in-a-corner case the metric is chosen to catch.
        Assert.True(ImageComparer.Compare(a, a, ImageTolerance.Exact).Matches);
    }

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Testing.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }
}
