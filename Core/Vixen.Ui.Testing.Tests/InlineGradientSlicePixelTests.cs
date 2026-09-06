// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>A two-line span's gradient runs one ramp across the whole box, which is <c>slice</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing in the draw list can see this and no fixture with a flat colour can either.</b>
///         <c>Vixen.Ui.Tests.InlineFragmentPaintingTests</c> pins the two rectangles a fragmented span
///         paints, and both implementations emit exactly those two — the difference is entirely in
///         where along the ramp each rectangle thinks it sits, which is a number in the side buffer
///         that only the fragment stage reads. And a <c>background-color</c> is identical either way,
///         which is why every fixture over there uses one and why this divergence was recorded rather
///         than caught.
///     </para>
///     <para>
///         ⚠ <b>The oracle is the unbroken box itself rather than a value anybody computed.</b> CSS
///         Display §2.2 defines <c>slice</c> — the initial <c>box-decoration-break</c> — as painting
///         the box as though it had never been broken and then cutting it at the breaks, so the right
///         answer is by definition the picture a single 90×40 box with the same gradient produces.
///         Comparing against that says nothing about the interpolation space, the ramp's easing or the
///         antialiasing at the edges: whatever those do, they must do the same thing on both sides.
///     </para>
///     <para>
///         ⚠ <b>A black-to-white ramp down a 40-point union, so the wrong answer is the maximum
///         distance from the right one.</b> The second fragment's first row is the ramp's midpoint
///         under <c>slice</c> and its <i>start</i> under <c>clone</c> — near white against near black,
///         a couple of hundred levels apart in a channel that is 0 at one end and 255 at the other,
///         which is what makes this measurable rather than a matter of tolerance.
///     </para>
/// </remarks>
public class InlineGradientSlicePixelTests {
    const int Width = 128;
    const int Height = 64;

    /// <summary>The union of the span's fragments: 30 + 60 on the first line, 60 on the second.</summary>
    const int UnionWidth = 90;
    const int UnionHeight = 40;

    /// <summary>Where the break is, and therefore the row the two readings disagree most about.</summary>
    const int Break = 20;

    /// <summary>A column inside both fragments — the second is only sixty wide.</summary>
    const int Column = 10;

    const string Ramp = "linear-gradient(to bottom, #000000, #ffffff)";

    static readonly Color4 Background = new(0f, 0f, 0f, 1f);

    /// <summary>
    ///     A span of three inline blocks that wraps after the second, with the ramp on the span.
    /// </summary>
    /// <remarks>
    ///     ⚠ The ragged second line is the fixture, exactly as in <c>InlineFragmentPaintingTests</c>:
    ///     30 + 60 fills the first line to ninety and the third box wraps to sixty, so the union is
    ///     ninety wide and thirty points of its second row belong to no fragment at all. Two full
    ///     lines would make the union and the fragments the same rectangle and every assertion below
    ///     would pass against an implementation that never heard of a fragment.
    /// </remarks>
    static Bitmap Fragmented() {
        using var ui = UiTest.Create(Width, Height, new UiTestOptions { Background = Background });

        ui.Load(
            $$"""
            root  { width: {{Width}}px; height: {{Height}}px; align-items: flex-start; }
            #box  { display: block; width: 100px; height: {{UnionHeight}}px; }
            #run  { display: inline; background-image: {{Ramp}}; }
            .cell { display: inline-block; height: 20px; }
            #a    { width: 30px; }
            #b    { width: 60px; }
            #c    { width: 60px; }
            """
        );

        var box = ui.Create("div", ui.Document.Root, "box");
        var run = ui.Create("div", box, "run");

        ui.Create("div", run, "a", "cell");
        ui.Create("div", run, "b", "cell");
        ui.Create("div", run, "c", "cell");

        ui.Frame();

        return ui.Capture();
    }

    /// <summary>The same ramp on a single unbroken 90×40 box, which is what <c>slice</c> means.</summary>
    static Bitmap Unbroken() {
        using var ui = UiTest.Create(Width, Height, new UiTestOptions { Background = Background });

        ui.Load(
            $$"""
            root { width: {{Width}}px; height: {{Height}}px; align-items: flex-start; }
            #box { display: block; width: {{UnionWidth}}px; height: {{UnionHeight}}px;
                   background-image: {{Ramp}}; }
            """
        );

        ui.Create("div", ui.Document.Root, "box");
        ui.Frame();

        return ui.Capture();
    }

    /// <summary>The ramp does not restart at the break: it is the unbroken box's, row for row.</summary>
    /// <remarks>
    ///     The interior rows only. The first and last row of the box are where the coverage of the
    ///     box's own edge is being antialiased, and they are the same on both pictures — but they say
    ///     nothing about the ramp, so leaving them out keeps the assertion about the one thing.
    /// </remarks>
    [Fact]
    public void A_fragmented_spans_ramp_is_the_unbroken_boxs_ramp() {
        var fragmented = Fragmented();
        var unbroken = Unbroken();

        for (var y = 1; y < UnionHeight - 1; y++) {
            var mine = fragmented.Pixels[fragmented.Offset(Column, y)];
            var theirs = unbroken.Pixels[unbroken.Offset(Column, y)];

            Assert.True(
                Math.Abs(mine - theirs) <= 2,
                $"row {y} reads {mine} where the unbroken box reads {theirs}"
            );
        }
    }

    /// <summary>And the ramp is continuous across the break rather than starting again below it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The assertion the comparison above cannot make, because it needs no second
    ///         picture.</b> The comparison is only ever as right as its control: a change that moved
    ///         both boxes together — the unbroken one included — would leave it green. This is stated
    ///         as an order over one picture instead. A ramp that was never broken descends a column
    ///         monotonically from one end to the other, and the single row a per-fragment ramp gets
    ///         wrong is the one where it goes back up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here says what a midpoint <i>is</i>, deliberately.</b> The row below the
    ///         break reads 58 rather than 128 on this fixture, because the ramp's interpolation space
    ///         and the capture's encoding both sit between the declaration and the byte — so an
    ///         assertion written on 128 would have been measuring those two rather than the break.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_ramp_does_not_restart_below_the_break() {
        var image = Fragmented();

        for (var y = 2; y < UnionHeight - 1; y++) {
            var above = image.Pixels[image.Offset(Column, y - 1)];
            var here = image.Pixels[image.Offset(Column, y)];

            Assert.True(
                here >= above - 1,
                $"row {y} reads {here} under a row reading {above}, so the ramp restarted at the break"
            );
        }

        // ⚠ And it is a ramp at all: a column of one flat colour is monotonic too, and so is a box
        // that painted nothing over a black canvas. Both ends of a black-to-white ramp are as far
        // apart as an 8-bit channel goes.
        Assert.True(
            image.Pixels[image.Offset(Column, UnionHeight - 2)] - image.Pixels[image.Offset(Column, 1)] > 128,
            "the span did not paint a ramp at all"
        );
    }
}
