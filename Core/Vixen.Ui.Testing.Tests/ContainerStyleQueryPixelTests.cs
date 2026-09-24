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

    /// <summary>
    ///     A named <c>@container card style()</c> query, counted on the frame and followed through a
    ///     live change (#273).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both cards are named <c>card</c> and both labels sit under an <c>.inner</c> that
    ///         declares <c>--variant: secondary</c>. The label's parent therefore always says
    ///         <c>secondary</c>, and an unnamed query, or a named one answered from the parent, paints
    ///         both labels red. Only the card that says <c>primary</c> may turn its label green, so
    ///         the frame holds 1 000 green texels, all of them in that card's half.
    ///     </para>
    ///     <para>
    ///         Then <c>primary</c> moves to the other card and the frame is taken again: the green
    ///         moves to the other half. That second frame is the invalidation edge, because the
    ///         <c>.inner</c> between each card and its label has an inherited portion that did not
    ///         move. Without the edge the first label stays green and the second stays red.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_named_style_query_colours_the_label_under_the_named_card_and_follows_it() {
        using var ui = UiTest.Create(200, 100, new UiTestOptions { Background = new Color4(0f, 0f, 0f, 1f) });

        ui.Load(
            """
            root     { width: 200px; height: 100px; flex-direction: row; align-items: flex-start; }
            .card    { width: 100px; height: 100px; align-items: flex-start; container-name: card; }
            .inner   { width: 100px; height: 100px; align-items: flex-start; --variant: secondary; }
            .primary { --variant: primary; }
            .label   { width: 50px; height: 20px; background-color: #ff0000; }
            @container card style(--variant: primary) { .label { background-color: #00ff00; } }
            """
        );

        var first = ui.Create("div", ui.Document.Root, null, "card", "primary");
        ui.Create("div", ui.Create("div", first, null, "inner"), null, "label");

        var second = ui.Create("div", ui.Document.Root, null, "card");
        ui.Create("div", ui.Create("div", second, null, "inner"), null, "label");

        ui.Frame();
        var before = Count(ui.Capture(), "VIXEN_NAMED_STYLE_QUERY_BEFORE");

        first.RemoveClass("primary");
        second.AddClass("primary");
        ui.Frame();
        var after = Count(ui.Capture(), "VIXEN_NAMED_STYLE_QUERY_AFTER");

        Assert.Equal((1000, 0, 1000), before);
        Assert.Equal((0, 1000, 1000), after);
    }

    /// <summary>Green texels in the left half, green in the right half, and red anywhere.</summary>
    static (int LeftGreen, int RightGreen, int Red) Count(Bitmap picture, string dumpVariable) {
        var dump = Environment.GetEnvironmentVariable(dumpVariable);

        if (!string.IsNullOrEmpty(dump)) {
            PngCodec.Save(dump, picture);
        }

        var (left, right, red) = (0, 0, 0);

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                var at = picture.Offset(x, y);
                var (r, g, b) = (picture.Pixels[at], picture.Pixels[at + 1], picture.Pixels[at + 2]);

                if (g > 200 && r < 50 && b < 50) {
                    left += x < picture.Width / 2 ? 1 : 0;
                    right += x >= picture.Width / 2 ? 1 : 0;
                }

                red += r > 200 && g < 50 && b < 50 ? 1 : 0;
            }
        }

        return (left, right, red);
    }
}
