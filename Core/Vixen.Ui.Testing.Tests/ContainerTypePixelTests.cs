// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>What <c>container-type</c> puts on the screen, counted rather than compared.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The geometry tests in <c>Vixen.Ui.Tests.ContainerTypeContainmentTests</c> read the
///         layout store; this reads the picture.</b> Two panels side by side hold the same 120 × 30
///         body. The left one is a plain flex item and is as wide as its body. The right one declares
///         <c>container-type: inline-size</c>, so it is contained and takes no width from the body:
///         it is its own 10 px of padding wide, and the body, still laid out and painted, hangs out of
///         it. Its <c>@container (max-width: 100px)</c> rule turns that body green, which it can only
///         do if the query read the contained box.
///     </para>
///     <para>
///         Closed forms, so there is no reference image to bootstrap: 120 × 30 of blue background
///         shows under the control's red body, the contained panel shows exactly its 10 × 60 of
///         orange padding, and exactly one 120 × 30 body is green. Before <c>container-type</c>
///         applied containment the right-hand panel was 130 wide — 4 200 orange texels, and the body
///         red because 130 is not at most 100.
///     </para>
///     <para>
///         ⚠ <b>The padding is not decoration.</b> A contained panel with no padding is nought wide,
///         and <c>DrawListBuilder.Emit</c> returns before the <i>children</i> of any zero-sized
///         element — so the overflowing body, which CSS paints, would not be drawn at all. That is a
///         defect of its own in the draw list, older than this and not about containers; the padding
///         keeps this oracle from depending on it.
///     </para>
/// </remarks>
public class ContainerTypePixelTests {
    const int Width = 400;
    const int Height = 100;

    static Bitmap Rendered() {
        using var ui = UiTest.Create(Width, Height, new UiTestOptions { Background = new Color4(0f, 0f, 0f, 1f) });

        ui.Load(
            $$"""
            root   { width: {{Width}}px; height: {{Height}}px; flex-direction: row; align-items: flex-start; }
            .plain { background-color: #0000ff; height: 60px; margin-right: 100px; }
            .query { background-color: #ff8000; height: 60px; padding-left: 10px; container-type: inline-size; }
            .body  { width: 120px; height: 30px; flex-shrink: 0; background-color: #ff0000; }
            @container (max-width: 100px) { .body { background-color: #00ff00; } }
            """
        );

        var plain = ui.Create("div", ui.Document.Root, null, "plain");
        ui.Create("div", plain, null, "body");

        var query = ui.Create("div", ui.Document.Root, null, "query");
        ui.Create("div", query, null, "body");

        ui.Frame();

        var picture = ui.Capture();

        // Where to write the picture, for a person to look at; the assertions never need it.
        var dump = Environment.GetEnvironmentVariable("VIXEN_CONTAINER_PIXELS");

        if (!string.IsNullOrEmpty(dump)) {
            PngCodec.Save(dump, picture);
        }

        return picture;
    }

    /// <summary>The four fills, told apart by which channels are lit rather than by exact bytes.</summary>
    /// <remarks>
    ///     By channel rather than by value because the orange's green channel is a mid value, and
    ///     where a mid value lands after the renderer's transfer function is not this test's question.
    ///     Every edge in the scene is on a whole pixel, so no texel is a blend of two fills.
    /// </remarks>
    enum Fill { Other, Red, Orange, Green, Blue }

    static Fill Classify(byte r, byte g, byte b) =>
        (r > 200, g > 200, b > 200, g > 16 && g < 240) switch {
            (true, false, false, false) => Fill.Red,
            (true, false, false, true) => Fill.Orange,
            (false, true, false, _) => Fill.Green,
            (false, false, true, false) => Fill.Blue,
            _ => Fill.Other
        };

    static int Count(in Bitmap image, Fill fill) {
        var count = 0;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var at = image.Offset(x, y);

                if (Classify(image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]) == fill) {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void A_query_container_draws_no_width_its_contents_asked_for() {
        var picture = Rendered();

        // The control: a 120 × 60 blue panel with a 120 × 30 red body over its top half.
        Assert.Equal(120 * 30, Count(picture, Fill.Blue));
        Assert.Equal(120 * 30, Count(picture, Fill.Red));

        // ⚠ The contained panel is its padding and nothing its body asked for, and the body is still
        // drawn — green, because the query answered about the 10-wide box.
        Assert.Equal(10 * 60, Count(picture, Fill.Orange));
        Assert.Equal(120 * 30, Count(picture, Fill.Green));
    }
}
