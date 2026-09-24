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

    /// <summary>
    ///     A mixed <c>(min-width: 80px) and style(--variant: primary)</c> query, counted on three
    ///     frames that move each half in turn (#273).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each half of the frame holds a 100-wide slot, and in each slot is a size container 60
    ///         or 90 wide, then an <c>.inner</c> that declares <c>--variant: secondary</c>, then the
    ///         50 × 20 label. The label's parent therefore always says <c>secondary</c>. Only the
    ///         container can say <c>primary</c>, and only a container 90 wide passes the size half.
    ///     </para>
    ///     <para>
    ///         Frame one: both containers say <c>primary</c> and only the left is wide, so 1 000 green
    ///         texels on the left and 1 000 red on the right. Frame two swaps the widths, which moves
    ///         only the size half: the green moves right. Frame three takes <c>primary</c> off the right
    ///         container, which moves only the style half, past the <c>.inner</c> whose inherited
    ///         portion does not move: no green at all, 2 000 red. A query that asked the parent paints
    ///         no green in any frame, and one that ignored either half paints green where it should not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_mixed_query_colours_the_label_only_when_its_size_container_passes_both_halves() {
        using var ui = UiTest.Create(200, 100, new UiTestOptions { Background = new Color4(0f, 0f, 0f, 1f) });

        ui.Load(
            """
            root     { width: 200px; height: 100px; flex-direction: row; align-items: flex-start; }
            .slot    { width: 100px; height: 100px; align-items: flex-start; }
            .box     { container-type: inline-size; width: 60px; height: 100px; align-items: flex-start; }
            .box.wide { width: 90px; }
            .inner   { width: 60px; height: 100px; align-items: flex-start; --variant: secondary; }
            .primary { --variant: primary; }
            .label   { width: 50px; height: 20px; background-color: #ff0000; }
            @container (min-width: 80px) and style(--variant: primary) { .label { background-color: #00ff00; } }
            """
        );

        var left = ui.Create("div", ui.Create("div", ui.Document.Root, null, "slot"), null, "box", "wide", "primary");
        ui.Create("div", ui.Create("div", left, null, "inner"), null, "label");

        var right = ui.Create("div", ui.Create("div", ui.Document.Root, null, "slot"), null, "box", "primary");
        ui.Create("div", ui.Create("div", right, null, "inner"), null, "label");

        ui.Frame();
        var first = Count(ui.Capture(), "VIXEN_MIXED_STYLE_QUERY_FIRST");

        left.RemoveClass("wide");
        right.AddClass("wide");
        ui.Frame();
        var second = Count(ui.Capture(), "VIXEN_MIXED_STYLE_QUERY_SECOND");

        right.RemoveClass("primary");
        ui.Frame();
        var third = Count(ui.Capture(), "VIXEN_MIXED_STYLE_QUERY_THIRD");

        Assert.Equal((1000, 0, 1000), first);
        Assert.Equal((0, 1000, 1000), second);
        Assert.Equal((0, 0, 2000), third);

        // Last, so that a sheet refusing the block still leaves its three frames on disk to look at.
        Assert.Empty(ui.Document.Styles.Loader.Diagnostics);
    }

    /// <summary>
    ///     <c>(min-width: 80px) or style(--variant: primary)</c>, counted on three frames in which each
    ///     half alone turns a label green (#273).
    /// </summary>
    /// <remarks>
    ///     The same slots, boxes and <c>.inner</c> override as the <c>and</c> test above. Frame one:
    ///     the left container is wide and says <c>secondary</c>, the right is narrow and says
    ///     <c>primary</c>. Each passes one half only, so both labels are green: 1 000 texels in each
    ///     half, no red. Frame two narrows the left and takes <c>primary</c> off the right, so neither
    ///     half holds anywhere: 2 000 red. Frame three gives the narrow left <c>primary</c>: green on the
    ///     left only. Before this landed the sheet was refused at load and every frame was 2 000 red.
    /// </remarks>
    [Fact]
    public void An_or_mixed_query_colours_the_label_when_either_half_holds() {
        using var ui = UiTest.Create(200, 100, new UiTestOptions { Background = new Color4(0f, 0f, 0f, 1f) });

        ui.Load(
            """
            root     { width: 200px; height: 100px; flex-direction: row; align-items: flex-start; }
            .slot    { width: 100px; height: 100px; align-items: flex-start; }
            .box     { container-type: inline-size; width: 60px; height: 100px; align-items: flex-start; }
            .box.wide { width: 90px; }
            .inner   { width: 60px; height: 100px; align-items: flex-start; --variant: secondary; }
            .primary { --variant: primary; }
            .label   { width: 50px; height: 20px; background-color: #ff0000; }
            @container (min-width: 80px) or style(--variant: primary) { .label { background-color: #00ff00; } }
            """
        );

        var left = ui.Create("div", ui.Create("div", ui.Document.Root, null, "slot"), null, "box", "wide");
        ui.Create("div", ui.Create("div", left, null, "inner"), null, "label");

        var right = ui.Create("div", ui.Create("div", ui.Document.Root, null, "slot"), null, "box", "primary");
        ui.Create("div", ui.Create("div", right, null, "inner"), null, "label");

        ui.Frame();
        var first = Count(ui.Capture(), "VIXEN_OR_STYLE_QUERY_FIRST");

        left.RemoveClass("wide");
        right.RemoveClass("primary");
        ui.Frame();
        var second = Count(ui.Capture(), "VIXEN_OR_STYLE_QUERY_SECOND");

        left.AddClass("primary");
        ui.Frame();
        var third = Count(ui.Capture(), "VIXEN_OR_STYLE_QUERY_THIRD");

        Assert.Equal((1000, 1000, 0), first);
        Assert.Equal((0, 0, 2000), second);
        Assert.Equal((1000, 0, 1000), third);
        Assert.Empty(ui.Document.Styles.Loader.Diagnostics);
    }

    /// <summary>
    ///     ⚠ A height query under an <c>inline-size</c> container asks the <c>size</c> container above
    ///     it, counted on the frame (#1429).
    /// </summary>
    /// <remarks>
    ///     Each slot is a <c>size</c> container, 100 tall on the left and 40 on the right, holding an
    ///     <c>inline-size</c> container that holds the label. The rule asks <c>(min-height: 60px)</c>.
    ///     The inner box cannot answer a height, so CSS Containment 3 § 5.1 skips it and the slot
    ///     answers: green on the left, red on the right, 1 000 texels each. Before the fix the walk
    ///     stopped at the inner box and both labels were red.
    /// </remarks>
    [Fact]
    public void A_height_query_skips_an_inline_size_container_for_the_size_container_above() {
        using var ui = UiTest.Create(200, 100, new UiTestOptions { Background = new Color4(0f, 0f, 0f, 1f) });

        ui.Load(
            """
            root     { width: 200px; height: 100px; flex-direction: row; align-items: flex-start; }
            .slot    { container-type: size; width: 100px; height: 100px; align-items: flex-start; }
            .slot.short { height: 40px; }
            .inline  { container-type: inline-size; width: 80px; align-items: flex-start; }
            .label   { width: 50px; height: 20px; background-color: #ff0000; }
            @container (min-height: 60px) { .label { background-color: #00ff00; } }
            """
        );

        ui.Create("div", ui.Create("div", ui.Create("div", ui.Document.Root, null, "slot"), null, "inline"), null, "label");
        ui.Create("div", ui.Create("div", ui.Create("div", ui.Document.Root, null, "slot", "short"), null, "inline"), null, "label");

        ui.Frame();

        Assert.Equal((1000, 0, 1000), Count(ui.Capture(), "VIXEN_AXIS_SKIP_QUERY"));
        Assert.Empty(ui.Document.Styles.Loader.Diagnostics);
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
