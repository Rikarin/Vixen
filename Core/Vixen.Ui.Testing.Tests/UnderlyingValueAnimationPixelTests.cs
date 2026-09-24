// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>
///     A frame in the middle of an animation whose one end is the element's own value, measured in
///     pixels (#1381, #1382).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Pictures because the defects were pictures</b>: a spinner that stood still and a box
///         that snapped home, while <c>Animator.TryGetCurrent</c> reported a transition running. The
///         document tests in <c>Vixen.Ui.Tests</c> read <c>AbsoluteLeft</c> and the transform; these
///         read the rasterised frame, so a value that reached the style and not the draw list fails
///         here.
///     </para>
///     <para>
///         <b>Closed forms, measured as runs.</b> A white 100 × 40 box on black: its first white
///         column on a row through it is its left edge, and the length of the white run along a row
///         and down a column through its centre is its width and height as drawn — which a quarter
///         turn swaps. The frame interval is an eighth of a second, so every sampled time is exact.
///         <c>VIXEN_UNDERLYING_PIXELS</c> names a directory to save each frame into.
///     </para>
/// </remarks>
public class UnderlyingValueAnimationPixelTests {
    static readonly Color4 Black = new(0f, 0f, 0f, 1f);

    static UiTest Harness() =>
        UiTest.Create(300, 200, new UiTestOptions { Background = Black, FrameDelta = TimeSpan.FromSeconds(0.125) });

    static Bitmap Captured(UiTest ui, string name) {
        var picture = ui.Capture();
        var dump = Environment.GetEnvironmentVariable("VIXEN_UNDERLYING_PIXELS");

        if (!string.IsNullOrEmpty(dump)) {
            Directory.CreateDirectory(dump);
            PngCodec.Save(Path.Combine(dump, name + ".png"), picture);
        }

        return picture;
    }

    static bool White(Bitmap picture, int x, int y) {
        var at = picture.Offset(x, y);

        return picture.Pixels[at] > 200 && picture.Pixels[at + 1] > 200 && picture.Pixels[at + 2] > 200;
    }

    static int FirstWhite(Bitmap picture, int y) {
        for (var x = 0; x < picture.Width; x++) {
            if (White(picture, x, y)) {
                return x;
            }
        }

        return -1;
    }

    static int RowRun(Bitmap picture, int y) {
        var count = 0;

        for (var x = 0; x < picture.Width; x++) {
            count += White(picture, x, y) ? 1 : 0;
        }

        return count;
    }

    static int ColumnRun(Bitmap picture, int x) {
        var count = 0;

        for (var y = 0; y < picture.Height; y++) {
            count += White(picture, x, y) ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    ///     ⚠ <b>The <c>to</c>-only spinner is 40 wide and 100 tall a quarter of the way round.</b>
    /// </summary>
    /// <remarks>
    ///     Turning about its centre (150, 70), a quarter turn swaps the box's extents. Holding the one
    ///     stop drew a full turn — 100 wide and 40 tall, the box at rest.
    /// </remarks>
    [Fact]
    public void A_to_only_spinner_is_drawn_a_quarter_turned_a_quarter_of_the_way_through() {
        using var ui = Harness();

        ui.Load(
            """
            root { width: 300px; height: 200px; align-items: flex-start; }
            @keyframes spin { to { transform: rotate(360deg); } }
            .box { width: 100px; height: 40px; margin-left: 100px; margin-top: 50px;
                   background-color: #ffffff; transform-origin: 50px 20px;
                   animation-name: spin; animation-duration: 1s; animation-timing-function: linear;
                   animation-iteration-count: infinite; }
            """
        );

        ui.Create("div", ui.Document.Root, null, "box");

        ui.Frame();
        ui.Frames(2);

        var picture = Captured(ui, "spinner-quarter");

        Assert.InRange(RowRun(picture, 70), 39, 41);
        Assert.InRange(ColumnRun(picture, 150), 99, 101);
    }

    /// <summary>A <c>from</c>-only margin is drawn three quarters of the way back from 40 px at a quarter.</summary>
    [Fact]
    public void A_from_only_margin_is_drawn_part_way_home() {
        using var ui = Harness();

        ui.Load(
            """
            root { width: 300px; height: 200px; align-items: flex-start; }
            @keyframes slide { from { margin-left: 40px; } }
            .box { width: 100px; height: 40px; background-color: #ffffff;
                   animation-name: slide; animation-duration: 1s; animation-timing-function: linear; }
            """
        );

        ui.Create("div", ui.Document.Root, null, "box");

        ui.Frame();
        ui.Frames(2);

        var picture = Captured(ui, "from-only-quarter");

        Assert.Equal(30, FirstWhite(picture, 20));
        Assert.Equal(100, RowRun(picture, 20));
    }

    /// <summary>
    ///     ⚠ <b>A margin the cascade drops is drawn half way home half way through the fade back.</b>
    /// </summary>
    [Fact]
    public void A_dropped_margin_is_drawn_half_way_home() {
        using var ui = Harness();

        ui.Load(
            """
            root { width: 300px; height: 200px; align-items: flex-start; }
            .box { width: 100px; height: 40px; background-color: #ffffff;
                   transition-property: margin-left; transition-duration: 1s;
                   transition-timing-function: linear; }
            .box.moved { margin-left: 40px; }
            """
        );

        var box = ui.Create("div", ui.Document.Root, null, "box");

        ui.Frame();
        box.AddClass("moved");
        ui.Frames(12);

        Assert.Equal(40, FirstWhite(Captured(ui, "dropped-settled"), 20));

        box.RemoveClass("moved");
        ui.Frame();
        ui.Frames(4);

        var picture = Captured(ui, "dropped-half");

        Assert.Equal(20, FirstWhite(picture, 20));
        Assert.Equal(100, RowRun(picture, 20));
    }
}
