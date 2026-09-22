// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The drawn half of the debug view: outlines where the numbers say the element is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Asserted on the draw list rather than on a picture</b>, which is what makes these
///         exact: a stroke command carries the rectangle it strokes, so "the border box was outlined"
///         is an equality against the element's bounds rather than a pixel that happens to be
///         orange. The instrument for "how many" is <see cref="DiagnosticsOverlay.Outlines" />, for
///         the reason <c>DiagnosticsPanel.RowCount</c> exists — once other elements' commands are in
///         the list, "drew four outlines" and "drew none" are hard to tell apart from outside.
///     </para>
///     <para>
///         ⚠ <b>The overlay is in the document it describes</b>, unlike the panel tests next door,
///         because that is the only arrangement in which its coordinates mean anything. The pointer
///         is moved by dispatching a real event, so the element drawn is the one the document's own
///         hit test chose — and the overlay covers the whole surface, so the hit test looking
///         <i>through</i> it is the property being relied on.
///     </para>
/// </remarks>
public class DiagnosticsOverlayTests {
    const string Css = """
        root { width: 800px; height: 600px; flex-direction: row; align-items: flex-start; }
        .box { width: 100px; height: 50px; margin: 10px; border-width: 2px; padding: 5px; }
        """;

    /// <summary>The border outlines the last draw put in the list, by rectangle.</summary>
    static List<Rectangle> Strokes(UiDocument document) {
        var strokes = new List<Rectangle>();

        foreach (var command in document.Drawing.Commands) {
            if (command.Kind == DrawCommandKind.Border && command.Thickness == 1f) {
                strokes.Add(new Rectangle(command.X, command.Y, command.Width, command.Height));
            }
        }

        return strokes;
    }

    /// <summary>Hovering an element draws its four boxes where the aggregator says they are.</summary>
    [Fact]
    public void The_element_under_the_pointer_is_outlined_by_its_four_boxes() {
        using var fixture = new ControlFixture(css: Css);

        var box = fixture.Document.Root.Add("div", classNames: "box");
        var overlay = fixture.Add<DiagnosticsOverlay>();

        fixture.Update();

        Assert.Equal(0, overlay.Outlines);

        fixture.MoveOver(box);
        fixture.Update();

        Assert.Same(box, fixture.Document.Hovered);
        Assert.Equal(4, overlay.Outlines);

        var model = fixture.Document.Diagnostics.BoxOf(box);
        var strokes = Strokes(fixture.Document);

        Assert.Contains(model.Margin, strokes);
        Assert.Contains(model.Border, strokes);
        Assert.Contains(model.Padding, strokes);
        Assert.Contains(model.Content, strokes);

        // And the four are four: a box with a margin, a border and padding has four distinct
        // rectangles, so an overlay that drew `Bounds` four times would fail the three above.
        Assert.NotEqual(model.Margin, model.Border);
        Assert.NotEqual(model.Border, model.Padding);
        Assert.NotEqual(model.Padding, model.Content);
    }

    /// <summary>
    ///     ⚠ The overlay covers the whole surface and is never the element under the pointer itself,
    ///     which is the property everything else here rests on: an overlay the hit test could land on
    ///     would outline its own full-surface box for ever.
    /// </summary>
    [Fact]
    public void The_overlay_is_transparent_to_the_pointer() {
        using var fixture = new ControlFixture(css: Css);

        var box = fixture.Document.Root.Add("div", classNames: "box");
        var overlay = fixture.Add<DiagnosticsOverlay>();

        fixture.Update();

        // Over the whole document, as the theme rule says.
        Assert.Equal(new Rectangle(0f, 0f, 800f, 600f), overlay.Bounds);

        fixture.MoveOver(box);
        fixture.Update();

        Assert.NotSame(overlay, fixture.Document.Hovered);
        Assert.Same(box, fixture.Document.Hovered);

        // And over empty ground the answer is the root, not the overlay.
        fixture.MovePointer(700f, 500f);
        fixture.Update();

        Assert.Same(fixture.Document.Root, fixture.Document.Hovered);
    }

    /// <summary>Leaving the element takes the outlines away rather than leaving them where it was.</summary>
    [Fact]
    public void Leaving_the_element_draws_nothing() {
        using var fixture = new ControlFixture(css: Css);

        var box = fixture.Document.Root.Add("div", classNames: "box");
        var overlay = fixture.Add<DiagnosticsOverlay>();

        fixture.MoveOver(box);
        fixture.Update();

        Assert.Equal(4, overlay.Outlines);

        // Off the document entirely: nothing is hovered, so nothing is drawn.
        fixture.MovePointer(-10f, -10f);
        fixture.Update();

        Assert.Null(fixture.Document.Hovered);
        Assert.Equal(0, overlay.Outlines);
        Assert.Empty(Strokes(fixture.Document));
    }

    /// <summary>A probe wins over the pointer, and clearing it hands the pointer back.</summary>
    [Fact]
    public void A_probe_names_the_element_instead_of_the_pointer() {
        using var fixture = new ControlFixture(css: Css);

        var first = fixture.Document.Root.Add("div", classNames: "box");
        var second = fixture.Document.Root.Add("div", classNames: "box");
        var overlay = fixture.Add<DiagnosticsOverlay>();

        fixture.MoveOver(first);
        fixture.Update();

        var firstBoxes = fixture.Document.Diagnostics.BoxOf(first);
        var secondBoxes = fixture.Document.Diagnostics.BoxOf(second);

        Assert.Contains(firstBoxes.Border, Strokes(fixture.Document));

        overlay.Probe = new Vector2(
            second.AbsoluteLeft + (second.Width * 0.5f),
            second.AbsoluteTop + (second.Height * 0.5f)
        );

        fixture.Update();

        Assert.Contains(secondBoxes.Border, Strokes(fixture.Document));
        Assert.DoesNotContain(firstBoxes.Border, Strokes(fixture.Document));

        overlay.Probe = null;
        fixture.Update();

        Assert.Contains(firstBoxes.Border, Strokes(fixture.Document));
    }

    /// <summary>
    ///     The regions the last pass invalidated are washed where they were, in the build that
    ///     records them — and in the build that does not, nothing is drawn and the count says so.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both arms are real assertions, for <c>DiagnosticsPanelTests</c>' reason: the constant is
    ///     read through a local so neither arm is unreachable code, and the test asserts whichever
    ///     half this compilation is in.
    /// </remarks>
    [Fact]
    public void The_regions_the_last_pass_invalidated_are_washed() {
        using var fixture = new ControlFixture(css: Css);

        var box = fixture.Document.Root.Add("div", classNames: "box");
        var overlay = fixture.Add<DiagnosticsOverlay>();

        // A settled document with a class flipped on one element: one recorded region, that
        // element's box as it was.
        fixture.Update();
        box.AddClass("moved");
        fixture.Update();

        var records = UiDiagnostics.RecordsRegions;

        if (records) {
            Assert.True(overlay.Regions > 0, "the build records regions and the overlay washed none");

            var washes = new List<Rectangle>();

            foreach (var command in fixture.Document.Drawing.Commands) {
                if (command.Kind == DrawCommandKind.Rectangle && command.Thickness == 0f && command.Radius == 0f) {
                    washes.Add(new Rectangle(command.X, command.Y, command.Width, command.Height));
                }
            }

            Assert.Contains(box.Bounds, washes);
        } else {
            Assert.Equal(0, overlay.Regions);
        }

        // Off, for reading a box model on a document that is animating.
        overlay.ShowsRegions = false;
        box.AddClass("again");
        fixture.Update();

        Assert.Equal(0, overlay.Regions);
    }
}
