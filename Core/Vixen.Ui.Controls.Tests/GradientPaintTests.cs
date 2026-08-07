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
            + " background-image: linear-gradient(to bottom, #ff0000 0%, #ffffff 50%, #0000ff 100%); }"
        );

        var bitmap = ui.Capture();

        foreach (var y in (int[]) [4, 20, 36]) {
            var offset = bitmap.Offset(20, y);

            Assert.True(bitmap.Pixels[offset + 1] > 200, $"y={y} is not green");
            Assert.True(bitmap.Pixels[offset] < 60, $"y={y} has red in it");
            Assert.True(bitmap.Pixels[offset + 2] < 60, $"y={y} has blue in it");
        }
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
