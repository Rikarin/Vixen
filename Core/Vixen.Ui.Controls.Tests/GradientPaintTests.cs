// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A gradient declared in CSS, rasterised, and read back a pixel at a time.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half the draw-list tests cannot reach.</b> <c>GradientTests</c> proves a
///         <c>linear-gradient(…)</c> becomes the right <see cref="BoxStyle" />; it says nothing about
///         whether the shader draws one. Those are two claims, and between them sits the whole
///         side-buffer indexing scheme — a command pointing at the wrong record draws a perfectly
///         valid gradient belonging to another box, which every assertion about <c>Boxes</c> would
///         pass.
///     </para>
///     <para>
///         ⚠ <b>Pixels asserted directly rather than compared with a committed picture.</b> A
///         baseline would answer "did this change", and the question here is "is this a gradient at
///         all" — which wants a ramp that is monotonic and ends where it should, not a file. It also
///         means this test cannot be made to pass by accepting a new screenshot.
///     </para>
/// </remarks>
public class GradientPaintTests {
    static UiTest Painted(string css, float width = 40f, float height = 40f) {
        var ui = UiTest.Create(width, height);
        ui.Load($"root {{ width: {width}px; height: {height}px; }} .probe {{ width: {width}px; height: {height}px; }} {css}");
        ui.Create("div", null, "probe", "probe");
        ui.Frame();

        return ui;
    }

    /// <summary>A tiled layer paints the same ramp twice across the box, and one that is not tiled does not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The repeating and the non-repeating case are one test, because either alone is
    ///         satisfied by the wrong implementation.</b> A shader that ignored <c>background-size</c>
    ///         entirely draws one ramp across the box, which passes any assertion phrased as "the left
    ///         half ramps" — and a shader that clipped every layer passes the <c>no-repeat</c> half
    ///         while breaking every gradient in the interface. What separates them is a comparison
    ///         between two points a whole period apart.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The period is the assertion, not the endpoints.</b> On a 40-pixel box a 50% tile is
    ///         twenty wide, so x=8 and x=28 sit at the same place in their respective tiles and must
    ///         be the same colour — while x=8 and x=18 must not, because that is the ramp running.
    ///         A layer that simply stretched to the box would fail the first and pass the second.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_sized_layer_repeats_across_the_box_unless_it_is_told_not_to() {
        using var tiled = Painted(
            ".probe { background-image: linear-gradient(to right, #ff0000, #0000ff); background-size: 50% 100%; }"
        );

        var repeated = tiled.Capture();

        static int Red(Vixen.Core.Imaging.Bitmap bitmap, int x) => bitmap.Pixels[bitmap.Offset(x, 20)];

        // One period apart, and therefore the same place in two different tiles.
        Assert.InRange(Red(repeated, 28) - Red(repeated, 8), -3, 3);

        // Half a period apart, and therefore visibly along the ramp. Without this the row above is
        // satisfied by a flat fill.
        Assert.True(
            Red(repeated, 8) - Red(repeated, 18) > 60,
            $"the tile does not ramp: {Red(repeated, 8)} at x=8 and {Red(repeated, 18)} at x=18"
        );

        // ⚠ A green fill *under* the layer, because "not painted" has to be read as a colour rather
        // than as an alpha: the captured frame is composited over an opaque surface, so the alpha
        // channel is 255 wherever the element is, painted layer or not.
        using var once = Painted(
            ".probe { background-color: #00ff00; background-image: linear-gradient(to right, #ff0000, #0000ff);"
            + " background-size: 50% 100%; background-repeat: no-repeat; }"
        );

        var single = once.Capture();

        // The first tile is still the ramp.
        Assert.True(Red(single, 8) - Red(single, 18) > 60, "the first tile does not ramp");

        // ⚠ And the second half is *not painted at all* rather than clamped to the ramp's end colour,
        // which is what CSS means by a layer that does not repeat — the colour underneath shows
        // through. Clamping is the reading a shader gets for free by doing nothing, and it leaves blue
        // here; tiling anyway leaves the ramp. Only "no layer" leaves green.
        var outside = single.Offset(30, 20);

        Assert.True(
            single.Pixels[outside + 1] > 200 && single.Pixels[outside + 2] < 60,
            "outside the tile is not the colour underneath: "
            + $"({single.Pixels[outside]}, {single.Pixels[outside + 1]}, {single.Pixels[outside + 2]})"
        );

        // And inside it the layer is on top of that colour rather than beside it.
        var inside = single.Offset(2, 20);

        Assert.True(
            single.Pixels[inside + 1] < 100,
            $"inside the tile still shows the colour underneath: green {single.Pixels[inside + 1]}"
        );
    }

    /// <summary>An explicit centre moves the round gradient's bright spot off the middle of the box.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted as a comparison between two points rather than as an absolute, because the
    ///     failure this is aimed at is the centre being <i>ignored</i>.</b> A radial gradient that
    ///     dropped its <c>at</c> is still a perfectly good radial gradient — red in the middle, blue at
    ///     the corners — and every endpoint assertion about it passes. What cannot survive is the
    ///     near corner being redder than the far one.
    /// </remarks>
    [Fact]
    public void An_explicit_centre_moves_the_bright_spot() {
        using var ui = Painted(".probe { background-image: radial-gradient(at 0% 0%, #ff0000, #0000ff); }");

        var bitmap = ui.Capture();
        int near = bitmap.Pixels[bitmap.Offset(4, 4)];
        int far = bitmap.Pixels[bitmap.Offset(35, 35)];
        int middle = bitmap.Pixels[bitmap.Offset(20, 20)];

        // ⚠ The gap between the two corners is the assertion, and an absolute threshold would not
        // be. A radial gradient that dropped its `at` is centred, and a centred one is very nearly the
        // *same* colour at both corners — so this number is a hundred and something with the centre
        // honoured and near zero without it, where "is the top left red" is a threshold somebody tunes.
        Assert.True(near - far > 100, $"the corners barely differ: red {near} then {far}");

        // And the middle sits between them, which is what says the ramp moved rather than inverted.
        Assert.InRange(middle, far + 10, near - 10);
    }

    /// <summary>Red at the top, blue at the bottom, and every step in between in order.</summary>
    /// <remarks>
    ///     ⚠ Monotonicity is the assertion that distinguishes a gradient from the two flat fills that
    ///     would satisfy "starts red, ends blue" — a shader that picked a colour per half would pass
    ///     an endpoint check and fail this one.
    /// </remarks>
    [Fact]
    public void A_declared_gradient_paints_a_monotonic_ramp() {
        using var ui = Painted(".probe { background-image: linear-gradient(to bottom, #ff0000, #0000ff); }");

        var bitmap = ui.Capture();
        var previousRed = 256;
        var previousBlue = -1;

        for (var y = 2; y < 38; y++) {
            var offset = bitmap.Offset(20, y);
            int red = bitmap.Pixels[offset];
            int blue = bitmap.Pixels[offset + 2];

            Assert.True(red <= previousRed, $"red rose at y={y}: {previousRed} then {red}");
            Assert.True(blue >= previousBlue, $"blue fell at y={y}: {previousBlue} then {blue}");

            previousRed = red;
            previousBlue = blue;
        }

        // And it genuinely traverses, rather than creeping: the ends are the stops.
        Assert.True(bitmap.Pixels[bitmap.Offset(20, 2)] > 200, "the top is not red");
        Assert.True(bitmap.Pixels[bitmap.Offset(20, 37) + 2] > 200, "the bottom is not blue");
    }

    /// <summary>The axis is the one that was asked for, and not its opposite or its transpose.</summary>
    /// <remarks>
    ///     ⚠ <b>Four directions, because each wrong answer passes three of the other tests.</b> A
    ///     flipped sign, a swapped pair of axes and a transposed one all draw a real gradient of the
    ///     right two colours — the only thing that catches them is asking which *edge* ended up red.
    /// </remarks>
    [Theory]
    [InlineData("to bottom", 20, 2, 20, 37)]
    [InlineData("to top", 20, 37, 20, 2)]
    [InlineData("to right", 2, 20, 37, 20)]
    [InlineData("to left", 37, 20, 2, 20)]
    public void The_start_colour_lands_on_the_edge_the_direction_came_from(
        string direction,
        int startX,
        int startY,
        int endX,
        int endY
    ) {
        using var ui = Painted($".probe {{ background-image: linear-gradient({direction}, #ff0000, #0000ff); }}");

        var bitmap = ui.Capture();

        var start = bitmap.Offset(startX, startY);
        var end = bitmap.Offset(endX, endY);

        Assert.True(bitmap.Pixels[start] > 200, $"{direction}: the start edge is not red");
        Assert.True(bitmap.Pixels[start + 2] < 60, $"{direction}: the start edge is too blue");
        Assert.True(bitmap.Pixels[end + 2] > 200, $"{direction}: the end edge is not blue");
        Assert.True(bitmap.Pixels[end] < 60, $"{direction}: the end edge is too red");
    }

    /// <summary>A refused gradient leaves the flat colour showing, not a half-drawn ramp.</summary>
    /// <remarks>
    ///     ⚠ The visible half of <see cref="GradientRefusal" />. A three-stop gradient approximated by
    ///     its ends would paint a red-to-blue sweep here and look entirely reasonable; what the engine
    ///     must do instead is leave the green alone.
    /// </remarks>
    [Fact]
    public void A_refused_gradient_shows_the_background_colour_underneath() {
        using var ui = Painted(
            ".probe { background-color: #00ff00;"
            + " background-image: repeating-linear-gradient(to bottom, #ff0000, #0000ff); }"
        );

        var bitmap = ui.Capture();

        foreach (var y in (int[]) [4, 20, 36]) {
            var offset = bitmap.Offset(20, y);

            Assert.True(bitmap.Pixels[offset + 1] > 200, $"y={y} is not green");
            Assert.True(bitmap.Pixels[offset] < 60, $"y={y} has red in it");
            Assert.True(bitmap.Pixels[offset + 2] < 60, $"y={y} has blue in it");
        }
    }

    /// <summary>A middle stop is painted in the middle, and is not either end.</summary>
    /// <remarks>
    ///     ⚠ <b>Green is chosen because neither end can produce it.</b> A two-stop red-to-blue ramp is
    ///     magenta-ish in the middle and never green, so a shader that quietly dropped the middle stop
    ///     — the approximation this whole feature replaced a refusal with — cannot pass this by
    ///     interpolating harder.
    /// </remarks>
    [Fact]
    public void A_middle_stop_is_painted_in_the_middle() {
        using var ui = Painted(
            ".probe { background-image: linear-gradient(to bottom, #ff0000, #00ff00, #0000ff); }"
        );

        var bitmap = ui.Capture();
        var middle = bitmap.Offset(20, 20);

        Assert.True(bitmap.Pixels[middle + 1] > 200, "the middle is not green");
        Assert.True(bitmap.Pixels[middle] < 60, "the middle has red in it");
        Assert.True(bitmap.Pixels[middle + 2] < 60, "the middle has blue in it");

        // ⚠ And the ends are still the ends, at a looser threshold than the two-stop tests use, for a
        // reason that is a property of the shader rather than of this gradient: `t` is
        // `dot / reach * 0.5 + 0.5`, so at the *centre of the outermost pixel* it is 0.0125 and not 0.
        // A middle stop halves each span, which doubles how far that lands from the end colour — so
        // the top is a strong red rather than a pure one, and demanding purity here would be
        // demanding a pixel the geometry does not contain.
        Assert.True(bitmap.Pixels[bitmap.Offset(20, 2)] > 150, "the top is not red");
        Assert.True(bitmap.Pixels[bitmap.Offset(20, 2) + 1] < 110, "the top is already green");
        Assert.True(bitmap.Pixels[bitmap.Offset(20, 37) + 2] > 150, "the bottom is not blue");
    }

    /// <summary>Stop positions move the ramp, and everything outside them is flat.</summary>
    /// <remarks>
    ///     ⚠ <b>The flat regions are the assertion, not the transition.</b> A ramp compressed into the
    ///     middle fifth and a full-width ramp both start red and end blue; what tells them apart is
    ///     that a third of the way down is <i>still exactly red</i>, which a full-width ramp never is.
    /// </remarks>
    [Fact]
    public void Stop_positions_flatten_the_ends_and_compress_the_ramp() {
        using var ui = Painted(
            ".probe { background-image: linear-gradient(to bottom, #ff0000 40%, #0000ff 60%); }"
        );

        var bitmap = ui.Capture();

        foreach (var y in (int[]) [4, 14]) {
            Assert.Equal(255, bitmap.Pixels[bitmap.Offset(20, y)]);
            Assert.Equal(0, bitmap.Pixels[bitmap.Offset(20, y) + 2]);
        }

        foreach (var y in (int[]) [26, 36]) {
            Assert.Equal(0, bitmap.Pixels[bitmap.Offset(20, y)]);
            Assert.Equal(255, bitmap.Pixels[bitmap.Offset(20, y) + 2]);
        }
    }

    /// <summary>A radial gradient runs out from the centre, and reaches its end at the corner.</summary>
    /// <remarks>
    ///     ⚠ <b>The corner-versus-side comparison is the whole test.</b> Red in the middle and blue at
    ///     the edge is true of <c>farthest-side</c>, <c>farthest-corner</c> and a plain circle alike.
    ///     CSS's default is <c>farthest-corner</c>, which means the ramp is still going at the edge
    ///     midpoint and finished at the corner — so the corner has to be <i>bluer than the side</i>,
    ///     and a shader that forgot the root-two scale draws a picture that looks completely right
    ///     until this is asked.
    /// </remarks>
    [Fact]
    public void A_radial_gradient_ends_at_the_corner_and_not_at_the_edge() {
        using var ui = Painted(".probe { background-image: radial-gradient(#ff0000, #0000ff); }");

        var bitmap = ui.Capture();

        var centre = bitmap.Offset(20, 20);
        var side = bitmap.Offset(38, 20);
        var corner = bitmap.Offset(38, 38);

        Assert.True(bitmap.Pixels[centre] > 200, "the centre is not red");
        Assert.True(bitmap.Pixels[centre + 2] < 60, "the centre has blue in it");
        Assert.True(bitmap.Pixels[corner + 2] > bitmap.Pixels[side + 2], "the corner is not past the side");

        // Round, not square: two points the same distance out are the same colour, whichever way
        // they lie. A shader that used a `dot` would fail this while passing everything above.
        Assert.Equal(bitmap.Pixels[bitmap.Offset(20, 4) + 2], bitmap.Pixels[bitmap.Offset(4, 20) + 2]);
    }

    /// <summary>⚠ A <c>circle</c> ending is round on a box that is not, and an <c>ellipse</c> is not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The box is 80 by 40 and that is the whole test.</b> On a square box every ending
    ///         keyword this pass added draws a circle, so a square probe cannot tell <c>circle</c>
    ///         from <c>ellipse</c> at all — and the implementation that fails here is the plausible
    ///         one: treating <c>circle</c> as a spelling of <c>ellipse</c>, which passes every
    ///         assertion in <c>GradientTests</c> that names an ellipse and every picture on a square.
    ///     </para>
    ///     <para>
    ///         The oracle is closed form rather than eyeballed. Fifteen pixels out along each axis is
    ///         the same distance, so on a circle it is the same colour; on the ellipse the horizontal
    ///         reach is twice the vertical, so the same fifteen pixels is half as far along the ramp.
    ///         With <c>closest-side</c> on this box the ellipse's reaches are (28.28, 14.14) and the
    ///         circle's are (14.14, 14.14) — so the two vertical readings agree and the two horizontal
    ///         ones must not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the pair is asserted against the default as well, because "it is round" is
    ///         true of the ending that was already there.</b> A <c>closest-side</c> that quietly
    ///         painted <c>farthest-corner</c> — which is what a refused layer used to become one
    ///         family up — is round too, and reaches the same colour at both points. What separates
    ///         it is that its ramp is a factor of root two shorter everywhere inside the box.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_circle_ending_is_round_on_a_box_that_is_not() {
        using var round = Painted(
            ".probe { background-image: radial-gradient(circle closest-side, #ff0000, #0000ff); }",
            80f,
            40f
        );

        using var oval = Painted(
            ".probe { background-image: radial-gradient(ellipse closest-side, #ff0000, #0000ff); }",
            80f,
            40f
        );

        using var wide = Painted(".probe { background-image: radial-gradient(#ff0000, #0000ff); }", 80f, 40f);

        static int Blue(UiTest ui, int x, int y) {
            var bitmap = ui.Capture();
            return bitmap.Pixels[bitmap.Offset(x, y) + 2];
        }

        // Fifteen out along each axis, from a centre at (40, 20).
        Assert.InRange(Blue(round, 55, 20) - Blue(round, 40, 35), -3, 3);

        Assert.True(
            Blue(oval, 40, 35) - Blue(oval, 55, 20) > 40,
            $"the ellipse is round: {Blue(oval, 55, 20)} across and {Blue(oval, 40, 35)} down"
        );

        // And neither is the default ending, whose ramp reaches the corner rather than the near side.
        Assert.True(
            Blue(oval, 40, 35) - Blue(wide, 40, 35) > 40,
            $"closest-side is painting farthest-corner: {Blue(oval, 40, 35)} against {Blue(wide, 40, 35)}"
        );
    }

    /// <summary>A conic gradient sweeps clockwise from twelve o'clock, which is CSS's convention.</summary>
    /// <remarks>
    ///     ⚠ <b>Four points around the box, because every wrong convention passes fewer than four.</b>
    ///     Starting at three o'clock rotates the picture a quarter turn; sweeping anticlockwise mirrors
    ///     it; and screen space being y-down means the naive <c>atan2(y, x)</c> does both at once. All
    ///     three still draw something that is unmistakably a conic gradient.
    /// </remarks>
    [Fact]
    public void A_conic_gradient_sweeps_clockwise_from_the_top() {
        using var ui = Painted(".probe { background-image: conic-gradient(#ff0000, #0000ff); }");

        var bitmap = ui.Capture();

        int Blue(int x, int y) => bitmap.Pixels[bitmap.Offset(x, y) + 2];

        var top = Blue(20, 4);
        var right = Blue(36, 20);
        var bottom = Blue(20, 36);
        var left = Blue(4, 20);

        Assert.True(top < right, $"the sweep does not start at the top: {top} then {right}");
        Assert.True(right < bottom, $"the sweep is not clockwise: {right} then {bottom}");
        Assert.True(bottom < left, $"the sweep does not continue past the bottom: {bottom} then {left}");
    }

    /// <summary>What the interpolation space actually changes, measured at the one pixel it shows.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Black to white, because it turns the choice into one number.</b> The midpoint of a
    ///         white ramp is the definition of the space: in linear RGB it is 0.5, in sRGB it is the
    ///         encoded half that decodes to 0.214, and in Oklab it is the lightness half that decodes
    ///         to 0.125. The capture stores linear bytes with no encode — see
    ///         <c>SoftwareUiRasterizer</c>'s own remark about that — so the three land at roughly 128,
    ///         55 and 32, which no tolerance can confuse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default is sRGB and that is CSS's answer, not the engine's.</b> Vixen paints in
    ///         linear RGB and the shader lerped there before there was a choice; a hand-written
    ///         <c>.vcss</c> rule with no hint should match a browser, and <c>in srgb-linear</c> is how
    ///         you ask for what the engine used to do unconditionally.
    ///     </para>
    /// </remarks>
    [Theory]
    // No hint at all is sRGB, which is CSS's rule and *not* what this engine painted before.
    [InlineData("to bottom", 45, 70)]
    [InlineData("to bottom in srgb", 45, 70)]
    // `srgb-linear` is CSS's name for the componentwise linear lerp the shader used to do always.
    [InlineData("to bottom in srgb-linear", 110, 145)]
    [InlineData("to bottom in oklab", 22, 45)]
    public void The_interpolation_space_moves_the_midpoint(string prelude, int least, int most) {
        using var ui = Painted(
            $".probe {{ background-image: linear-gradient({prelude}, #000000, #ffffff); }}"
        );

        var bitmap = ui.Capture();
        int middle = bitmap.Pixels[bitmap.Offset(20, 20)];

        Assert.InRange(middle, least, most);

        // The ends do not move whatever the space is, which is what makes the midpoint the whole
        // measurement rather than one sample of a shifted ramp. Loose at the white end because `t` at
        // the outermost pixel's centre is 0.9875 and not 1, and the three curves separate fastest
        // exactly where they are steepest.
        Assert.True(bitmap.Pixels[bitmap.Offset(20, 2)] < 30, "the top is not black");
        Assert.True(bitmap.Pixels[bitmap.Offset(20, 37)] > 200, "the bottom is not white");
    }

    /// <summary>Two elements with different gradients get their own, not each other's.</summary>
    /// <remarks>
    ///     ⚠ <b>The side buffer is indexed, and an index is a thing that can be off by one.</b> Every
    ///     other test here draws a single box, where an offset that is always zero is
    ///     indistinguishable from a correct one. This is the cheapest arrangement that tells them
    ///     apart.
    /// </remarks>
    [Fact]
    public void Two_gradients_in_one_frame_do_not_share_a_record() {
        using var ui = UiTest.Create(80f, 20f);

        // ⚠ Side by side and not stacked: the root lays its children out in a *row*, so two
        // full-width boxes put the second one off the edge of the viewport — where it draws
        // perfectly and this test reads the background instead. Found the hard way.
        ui.Load(
            "root { width: 80px; height: 20px; }"
            + ".left, .right { width: 40px; height: 20px; }"
            + ".left { background-image: linear-gradient(to bottom, #ff0000, #ff0000); }"
            + ".right { background-image: linear-gradient(to bottom, #0000ff, #0000ff); }"
        );

        ui.Create("div", null, "left", "left");
        ui.Create("div", null, "right", "right");
        ui.Frame();

        var bitmap = ui.Capture();

        var left = bitmap.Offset(20, 10);
        var right = bitmap.Offset(60, 10);

        Assert.True(bitmap.Pixels[left] > 200, "the first element is not red");
        Assert.True(bitmap.Pixels[left + 2] < 60, "the first element has blue in it");
        Assert.True(bitmap.Pixels[right + 2] > 200, "the second element is not blue");
        Assert.True(bitmap.Pixels[right] < 60, "the second element has red in it");
    }
}
