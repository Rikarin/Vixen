// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Panels scroll unless they fill their box, and no panel ends up with two scrollbars.</summary>
/// <remarks>
///     ⚠ <b>The opt-out is the load-bearing half, which is why most of this file tests it rather than
///     the default.</b> A panel that failed to scroll is a panel with content below the fold; a
///     viewport that scrolled is every click landing somewhere the user did not aim, which is not
///     visible at all until somebody reports that selection is broken.
/// </remarks>
public class PanelScrollingTests {
    [Fact]
    public void A_panel_scrolls_by_default() {
        using var fixture = EditorSession.Start();

        fixture.Open("console");
        fixture.Frames(2);

        var panel = Panel(fixture, "console");

        Assert.True(panel.Scrolls);
        Assert.True(panel.HasClass("scrolls"));
    }

    /// <summary>
    ///     ⚠ The one in the brief that is a correctness bug rather than an annoyance. A viewport turns
    ///     a document coordinate into a render pixel with <c>(y - AbsoluteTop) * RenderScale</c>, so a
    ///     panel offset it does not know about is a constant error in every pick.
    /// </summary>
    [Fact]
    public void The_scene_viewport_does_not_scroll_and_its_picks_do_not_move() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var panel = Panel(fixture, "scene");
        var control = Find<Viewport>(panel) ?? throw new InvalidOperationException("the scene panel has no viewport");

        Assert.False(panel.Scrolls);
        Assert.Equal(0f, panel.ScrollTop);

        var aimed = (X: control.AbsoluteLeft + 40f, Y: control.AbsoluteTop + 40f);
        var before = control.ToRender(aimed.X, aimed.Y);

        // A scroll the panel refuses. `ScrollTo` clamps against `MaximumScroll`, which an opted-out
        // panel keeps at zero — so this is the whole of the opt-out being asked to do its job.
        panel.ScrollTo(200f);
        fixture.Frames(2);

        Assert.Equal(0f, panel.ScrollTop);
        Assert.Equal(before.X, control.ToRender(aimed.X, aimed.Y).X, 0.01f);
        Assert.Equal(before.Y, control.ToRender(aimed.X, aimed.Y).Y, 0.01f);

        // ⚠ And the same assertion with the opt-out taken away, because an assertion that a number did
        // not change is worth nothing until the thing that would change it is shown to work. Turning
        // scrolling on and giving the panel something to scroll must move the pick — if it does not,
        // the test above is passing for the wrong reason.
        panel.Scrolls = true;

        var filler = panel.Add("filler");
        filler.SetStyle("height", "4000px");

        fixture.Frames(2);
        panel.ScrollTo(200f);
        fixture.Frames(2);

        Assert.Equal(200f, panel.ScrollTop, 0.5f);
        Assert.NotEqual(before.Y, control.ToRender(aimed.X, aimed.Y).Y, 0.5f);
    }

    /// <summary>Every panel whose content already scrolls has exactly one scroll region, not two.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted by walking the tree rather than by reading <see cref="DockPanel.Scrolls" />.</b>
    ///     The property says what the panel intends; the count says what the user gets, and the failure
    ///     this guards is two bars side by side with a wheel that moves whichever one the pointer
    ///     happens to be over.
    /// </remarks>
    [Theory]
    [InlineData("inspector")]
    [InlineData("history")]
    [InlineData("profiler")]
    [InlineData("memory")]
    public void A_panel_whose_content_scrolls_has_one_scroll_region(string id) {
        using var fixture = EditorSession.Start();

        fixture.Open(id);
        fixture.Frames(3);

        var panel = Panel(fixture, id);

        Assert.False(panel.Scrolls);
        Assert.Null(panel.Bar);

        // Nested, not merely plural: two scroll views side by side in one panel are two independent
        // regions and are fine. One inside another is the defect.
        Assert.Empty(Nested(panel));
    }

    [Fact]
    public void A_panel_that_fills_its_box_grows_no_bar_however_tall_its_content_measures() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var panel = Panel(fixture, "scene");

        Assert.False(panel.Scrolls);
        Assert.False(panel.HasClass("scrolls"));
        Assert.Null(panel.Bar);
    }

    /// <summary>Scroll views that have another scroll view above them inside the same panel.</summary>
    static List<ScrollView> Nested(DockPanel panel) {
        var found = new List<ScrollView>();

        foreach (var view in Descendants(panel).OfType<ScrollView>()) {
            for (var walk = view.Parent; walk is not null && !ReferenceEquals(walk, panel); walk = walk.Parent) {
                if (walk is ScrollView) {
                    found.Add(view);
                    break;
                }
            }
        }

        return found;
    }

    static DockPanel Panel(EditorSession fixture, string id) =>
        Descendants(fixture.Document.Root).OfType<DockPanel>().FirstOrDefault(panel => panel.Id == id)
        ?? throw new InvalidOperationException($"the '{id}' panel is not open");

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
