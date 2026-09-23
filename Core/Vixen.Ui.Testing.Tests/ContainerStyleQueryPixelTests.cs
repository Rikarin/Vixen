// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>What an unnamed <c>@container style()</c> query puts on the screen, counted.</summary>
/// <remarks>
///     Two cards with the same 50 × 20 label; only the first declares <c>--variant: primary</c>, and
///     the style query turns a label green under exactly that parent. So the frame holds one green
///     label and one red one — 1 000 texels of each — and a query that answered for every card, or for
///     none, or about the label's own value, paints two of one colour. Before the loader read the raw
///     prelude, ExCSS's "not all" refused the block and both labels were red.
/// </remarks>
public class ContainerStyleQueryPixelTests {
    [Fact]
    public void A_style_query_colours_only_the_label_under_the_matching_parent() {
        using var ui = UiTest.Create(200, 100, new UiTestOptions { Background = new Color4(0f, 0f, 0f, 1f) });

        ui.Load(
            """
            root     { width: 200px; height: 100px; flex-direction: row; align-items: flex-start; }
            .card    { width: 100px; height: 100px; align-items: flex-start; }
            .primary { --variant: primary; }
            .label   { width: 50px; height: 20px; background-color: #ff0000; }
            @container style(--variant: primary) { .label { background-color: #00ff00; } }
            """
        );

        var primary = ui.Create("div", ui.Document.Root, null, "card", "primary");
        ui.Create("div", primary, null, "label");

        var plain = ui.Create("div", ui.Document.Root, null, "card");
        ui.Create("div", plain, null, "label");

        ui.Frame();

        var picture = ui.Capture();
        var dump = Environment.GetEnvironmentVariable("VIXEN_STYLE_QUERY_PIXELS");

        if (!string.IsNullOrEmpty(dump)) {
            PngCodec.Save(dump, picture);
        }

        var (red, green) = (0, 0);

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                var at = picture.Offset(x, y);
                var (r, g, b) = (picture.Pixels[at], picture.Pixels[at + 1], picture.Pixels[at + 2]);

                red += r > 200 && g < 50 && b < 50 ? 1 : 0;
                green += g > 200 && r < 50 && b < 50 ? 1 : 0;
            }
        }

        Assert.Equal(50 * 20, green);
        Assert.Equal(50 * 20, red);
    }
}
