// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TextureGraph.Drawing;
using Xunit;

namespace Tests;

/// <summary>Patina #139's <c>Source/Vector</c>: an SVG read and filled, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>The oracle is area and position, never a picture.</b> A rectangle covering half a frame covers half the
///         texels; a circle covers <c>πr²</c>; a stroke of width <c>w</c> along a length <c>L</c> covers <c>wL</c>.
///         Each is false for nearly every way of getting a transform, a fit or a winding wrong.
///     </para>
///     <para>
///         ⚠ <b>Every measure is over ink, so an empty picture would divide by nothing.</b>
///         <see cref="A_filled_square_covers_what_its_frame_says" /> is the guard: it asserts a non-zero area first.
///     </para>
/// </remarks>
public class TextureVectorTests {
    const int Side = 64;

    static readonly TextureCanvas Canvas = new(Side, Side, false, false);

    /// <summary>
    ///     ⚠ Spelled with its fit, because <c>new()</c> of a record struct is the zeroed struct and not its primary
    ///     constructor's defaults: a scale of nought, which draws nothing.
    /// </summary>
    static readonly TextureVectorPlacement Centred = new(TextureVectorFit.Contain);

    static float[] Coverage(string svg, TextureVectorPlacement? placement = null, TextureCanvas? canvas = null) =>
        TextureVector.Rasterize(TextureVector.Read(svg), canvas ?? Canvas, placement ?? Centred, true);

    static float Area(float[] coverage) => coverage.Sum();

    static string Document(string body, string frame = "0 0 100 100") =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{frame}\">{body}</svg>";

    [Fact]
    public void A_filled_square_covers_what_its_frame_says() {
        var half = Coverage(Document("<rect x=\"0\" y=\"0\" width=\"50\" height=\"100\"/>"));

        Assert.Equal(Side * Side / 2f, Area(half), 1f);

        // The left half, exactly: the frame's x of 50 lands on texel 32.
        Assert.Equal(1f, half[(10 * Side) + 31], 3);
        Assert.Equal(0f, half[(10 * Side) + 32], 3);
    }

    [Fact]
    public void A_circle_covers_pi_r_squared() {
        var disc = Coverage(Document("<circle cx=\"50\" cy=\"50\" r=\"40\"/>"));
        var radius = 40f * Side / 100f;

        Assert.Equal(MathF.PI * radius * radius, Area(disc), MathF.PI * radius * radius * 0.005f);
    }

    [Fact]
    public void Contain_cover_and_stretch_lay_a_wide_frame_three_ways() {
        // A frame twice as wide as it is tall, filled whole.
        const string Wide = "<rect x=\"0\" y=\"0\" width=\"200\" height=\"100\"/>";
        var svg = Document(Wide, "0 0 200 100");

        Assert.Equal(Side * Side / 2f, Area(Coverage(svg, new(TextureVectorFit.Contain))), 1f);
        Assert.Equal(Side * Side, Area(Coverage(svg, new(TextureVectorFit.Cover))), 1f);
        Assert.Equal(Side * Side, Area(Coverage(svg, new(TextureVectorFit.Stretch))), 1f);

        // Contained, it sits in the middle band: rows 16 to 47.
        var contained = Coverage(svg, new(TextureVectorFit.Contain));

        Assert.Equal(0f, contained[(15 * Side) + 10], 3);
        Assert.Equal(1f, contained[(16 * Side) + 10], 3);
        Assert.Equal(1f, contained[(47 * Side) + 10], 3);
        Assert.Equal(0f, contained[(48 * Side) + 10], 3);
    }

    [Fact]
    public void Scale_and_position_move_the_frame_about_its_middle() {
        var svg = Document("<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\"/>");
        var quarter = Coverage(svg, new(TextureVectorFit.Contain, 0.5f, 0.25f, 0.25f));

        Assert.Equal(Side * Side / 4f, Area(quarter), 1f);
        Assert.Equal(1f, quarter[(5 * Side) + 5], 3);
        Assert.Equal(0f, quarter[(40 * Side) + 40], 3);
    }

    [Fact]
    public void Even_odd_punches_the_overlap_of_two_same_wound_squares_and_non_zero_fills_it() {
        const string Two = "M10 10 H90 V90 H10 Z M30 30 H70 V70 H30 Z";
        var nonZero = Coverage(Document($"<path d=\"{Two}\"/>"));
        var evenOdd = Coverage(Document($"<path fill-rule=\"evenodd\" d=\"{Two}\"/>"));
        var outer = 80f * Side / 100f;
        var inner = 40f * Side / 100f;

        Assert.Equal(outer * outer, Area(nonZero), 6f);
        Assert.Equal((outer * outer) - (inner * inner), Area(evenOdd), 6f);
    }

    [Fact]
    public void A_stroke_covers_its_width_times_its_length_and_a_line_has_no_fill() {
        var line = Coverage(Document("<line x1=\"10\" y1=\"50\" x2=\"90\" y2=\"50\" stroke=\"black\" stroke-width=\"10\"/>"));
        var length = 80f * Side / 100f;
        var width = 10f * Side / 100f;

        Assert.Equal(length * width, Area(line), 1.5f);

        // A closed square stroked, against its outer square less its inner one: the joins are filled, not cancelled.
        var square = Coverage(Document("<rect x=\"20\" y=\"20\" width=\"60\" height=\"60\" fill=\"none\" stroke=\"black\" stroke-width=\"10\"/>"));
        var outside = 70f * Side / 100f;
        var inside = 50f * Side / 100f;

        Assert.Equal((outside * outside) - (inside * inside), Area(square), 3f);
    }

    /// <summary>
    ///     A stroke that turns both ways covers everything each of its straight pieces covers: its joins are filled,
    ///     whichever way the tessellator wound them.
    /// </summary>
    [Theory]
    [InlineData("miter")]
    [InlineData("round")]
    [InlineData("bevel")]
    public void A_zigzag_stroke_covers_every_one_of_its_pieces(string join) {
        float[] points = [10, 50, 30, 15, 50, 85, 70, 15, 90, 50];
        var zigzag = Coverage(
            Document(
                $"<polyline points=\"{string.Join(' ', points)}\" fill=\"none\" stroke=\"black\" stroke-width=\"8\" stroke-linejoin=\"{join}\"/>"
            )
        );

        for (var at = 0; at + 3 < points.Length; at += 2) {
            var piece = Coverage(
                Document(
                    $"<line x1=\"{points[at]}\" y1=\"{points[at + 1]}\" x2=\"{points[at + 2]}\" y2=\"{points[at + 3]}\" stroke=\"black\" stroke-width=\"8\"/>"
                )
            );

            Assert.True(piece.Sum() > 50f);

            for (var texel = 0; texel < piece.Length; texel++) {
                Assert.True(zigzag[texel] >= piece[texel] - 0.02f, $"texel {texel} is {zigzag[texel]} in the zigzag and {piece[texel]} in piece {at / 2}");
            }
        }
    }

    [Fact]
    public void A_transform_a_use_and_a_group_place_the_same_square() {
        var direct = Coverage(Document("<rect x=\"60\" y=\"10\" width=\"20\" height=\"20\"/>"));
        var moved = Coverage(Document("<g transform=\"translate(50 0)\"><rect x=\"10\" y=\"10\" width=\"20\" height=\"20\"/></g>"));
        var used = Coverage(
            Document("<defs><rect id=\"r\" x=\"10\" y=\"10\" width=\"20\" height=\"20\"/></defs><use href=\"#r\" x=\"50\"/>")
        );
        var scaled = Coverage(Document("<rect transform=\"scale(2)\" x=\"30\" y=\"5\" width=\"10\" height=\"10\"/>"));

        Assert.True(Area(direct) > 100f);

        for (var at = 0; at < direct.Length; at++) {
            Assert.Equal(direct[at], moved[at], 3);
            Assert.Equal(direct[at], used[at], 3);
            Assert.Equal(direct[at], scaled[at], 3);
        }
    }

    [Fact]
    public void Colours_composite_over_one_another_as_the_file_paints_them() {
        var svg = Document(
            "<style>.top { fill: #00f; opacity: 0.5 }</style>"
            + "<rect width=\"100\" height=\"100\" fill=\"red\"/>"
            + "<rect class=\"top\" x=\"50\" width=\"50\" height=\"100\"/>"
        );

        var picture = TextureVector.Straight(TextureVector.Rasterize(TextureVector.Read(svg), Canvas, Centred, false));

        static (byte R, byte G, byte B, byte A) At(byte[] bytes, int x, int y) {
            var at = ((y * Side) + x) * 4;

            return (bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]);
        }

        Assert.Equal((255, 0, 0, 255), At(picture, 10, 10));
        Assert.Equal((128, 0, 128, 255), At(picture, 50, 10));

        // An unpainted texel is transparent, so a logo laid on a layer covers only itself.
        var logo = TextureVector.Straight(
            TextureVector.Rasterize(TextureVector.Read(Document("<circle cx=\"50\" cy=\"50\" r=\"10\" fill=\"#00ff00\"/>")), Canvas, Centred, false)
        );

        Assert.Equal((byte)0, At(logo, 2, 2).A);
        Assert.Equal((0, 255, 0, 255), At(logo, 32, 32));
    }

    [Fact]
    public void A_drawing_wraps_across_the_edge_of_a_picture_that_tiles() {
        var svg = Document("<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\"/>");
        var tiling = Coverage(svg, new(TextureVectorFit.Contain, 0.5f, 1f, 0.5f), new TextureCanvas(Side, Side, true, true));
        var clamped = Coverage(svg, new(TextureVectorFit.Contain, 0.5f, 1f, 0.5f));

        // A square centred on the right edge: half of it overhangs, and comes back in at the left only when it wraps.
        Assert.Equal(Side * Side / 4f, Area(tiling), 1f);
        Assert.Equal(Side * Side / 8f, Area(clamped), 1f);
        Assert.Equal(1f, tiling[(32 * Side) + 2], 3);
        Assert.Equal(0f, clamped[(32 * Side) + 2], 3);
    }

    [Theory]
    [InlineData("<linearGradient id=\"g\"/><rect width=\"10\" height=\"10\" fill=\"url(#g)\"/>", "gradient")]
    [InlineData("<text x=\"0\" y=\"10\">Hello</text>", "<text>")]
    [InlineData("<image href=\"a.png\" width=\"10\" height=\"10\"/>", "<image>")]
    [InlineData("<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>", "filter")]
    [InlineData("<rect width=\"10\" height=\"10\" fill=\"chartreuse\"/>", "chartreuse")]
    [InlineData("<path d=\"M 0 0 X 5 5\"/>", "path")]
    public void What_is_not_drawn_is_refused_by_name(string body, string named) {
        var failure = Assert.Throws<InvalidDataException>(() => TextureVector.Read(Document(body)));

        Assert.Contains(named, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_frame_or_not_an_svg_is_refused() {
        Assert.Throws<InvalidDataException>(() => TextureVector.Read("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect/></svg>"));
        Assert.Throws<InvalidDataException>(() => TextureVector.Read("<html/>"));
        Assert.Throws<InvalidDataException>(() => TextureVector.Read("not xml"));

        // Width and height with no viewBox are a frame from the origin.
        var sized = TextureVector.Read("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20mm\" height=\"10mm\"/>");

        Assert.Equal(20f * 96f / 25.4f, sized.FrameWidth, 3);
    }
}
