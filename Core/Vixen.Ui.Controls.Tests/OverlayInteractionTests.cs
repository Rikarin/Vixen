// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Popovers, tooltips, dialogs, drawers and menus — the things that come up over the rest.</summary>
/// <remarks>
///     ⚠ <b>The assertion that matters here is what a click can still reach.</b> An overlay is
///     defined by what it covers, so a dialog that opens without blocking the page behind it is a
///     dialog only in name — and no assertion about its own state can tell the difference.
///     <c>ShouldBeHittable</c> is what does, and its failures name the thing in the way.
/// </remarks>
public class OverlayInteractionTests {
    static UiTest Opened() =>
        ControlHarness.Open(
            400f,
            300f,
            """
            button { width: 100px; height: 32px; }
            popover, menu { width: 140px; height: 90px; }
            """
        );

    [Fact]
    public void A_popover_opens_against_its_anchor_and_light_dismisses() {
        using var ui = Opened();
        var anchor = ui.Add<Button>("more");
        var popover = ui.Add<Popover>("card");

        var states = new List<bool>();
        popover.OpenChanged += (_, open) => states.Add(open);

        ui.Get("#card").ShouldNotBeVisible();

        popover.Open(anchor);
        ui.Frame();

        ui.Get("#card").ShouldBeVisible();
        Assert.Same(anchor, popover.Anchor);
        Assert.Equal([true], states);

        // A click well away from it, which is what light dismiss means.
        ui.MovePointer(390f, 290f);
        ui.PressPointer();
        ui.ReleasePointer();
        ui.Frame();

        ui.Get("#card").ShouldNotBeVisible();
        Assert.Equal([true, false], states);
    }

    [Fact]
    public void Escape_closes_an_overlay_that_asked_for_it() {
        using var ui = Opened();
        var popover = ui.Add<Popover>("card");

        popover.Open();
        ui.Frame();
        ui.Get("#card").ShouldBeVisible();

        ui.PressKey(InputKey.Escape);
        ui.Frame();

        ui.Get("#card").ShouldNotBeVisible();
    }

    [Fact]
    public void An_overlay_that_declines_escape_and_light_dismiss_stays_open() {
        using var ui = Opened();
        var popover = ui.Add<Popover>("card");
        popover.CloseOnEscape = false;
        popover.LightDismiss = false;

        popover.Open();
        ui.Frame();

        ui.PressKey(InputKey.Escape);
        ui.MovePointer(390f, 290f);
        ui.PressPointer();
        ui.ReleasePointer();
        ui.Frame();

        // Which is what a modal step in a wizard needs: the only way out is the buttons it offers.
        ui.Get("#card").ShouldBeVisible();
    }

    [Fact]
    public void A_dialogs_backdrop_takes_the_clicks_meant_for_the_page_behind_it() {
        using var ui = Opened();
        var behind = ui.Add<Button>("behind");
        var dialog = ui.Add<Dialog>("confirm");

        var clicked = false;
        behind.Clicked += _ => clicked = true;

        // Reachable before.
        ui.Get("#behind").ShouldBeHittable().Click();
        Assert.True(clicked);

        clicked = false;
        dialog.Open();
        ui.Frame();

        // ⚠ And unreachable after, with the failure naming the backdrop. This is the assertion that
        // separates a dialog from a floating panel, and no property on either can make it.
        var failure = Assert.Throws<UiTestException>(() => ui.Get("#behind").Click());
        Assert.Contains("on top of", failure.Message, StringComparison.Ordinal);
        Assert.False(clicked);
    }

    [Fact]
    public void A_dialog_takes_the_focus_and_keeps_tab_inside_itself() {
        using var ui = Opened();
        var outside = ui.Add<Button>("outside");
        var dialog = ui.Add<Dialog>("confirm");

        var inside = dialog.Body.Add<Button>();
        var also = dialog.Body.Add<Button>();

        dialog.Open();
        ui.Frame();

        // Tab a good few times. Wherever it lands, it must never be the button behind the dialog —
        // a keyboard user who can tab out of a modal is a keyboard user who is stuck outside it.
        for (var i = 0; i < 6; i++) {
            ui.Tab();
            ui.Frame();
            Assert.NotSame(outside, ui.Document.Focused);
        }

        Assert.True(
            ReferenceEquals(ui.Document.Focused, inside)
            || ReferenceEquals(ui.Document.Focused, also)
            || IsInside(ui.Document.Focused, dialog),
            "the focus should have stayed inside the dialog"
        );
    }

    [Fact]
    public void A_drawer_opens_and_closes_the_same_way() {
        using var ui = Opened();
        var drawer = ui.Add<Drawer>("panel");

        drawer.Open();
        ui.Frame();
        ui.Get("#panel").ShouldBeVisible();

        drawer.Close();
        ui.Frame();
        ui.Get("#panel").ShouldNotBeVisible();
    }

    [Fact]
    public void A_tooltip_waits_for_its_delay_before_it_shows() {
        using var ui = Opened();
        var target = ui.Add<Button>("help");
        var tooltip = ui.Add<Tooltip>("hint");

        tooltip.Delay = TimeSpan.FromMilliseconds(400);
        tooltip.Attach(target);
        ui.Frame();

        ui.Get("#help").Hover();
        tooltip.Tick(ui.Now);
        ui.Frame();

        // ⚠ Not yet. A tooltip that appeared on the first frame of a hover would flash on every
        // pointer that crossed the control on its way somewhere else.
        ui.Get("#hint").ShouldNotBeVisible();

        ui.Advance(TimeSpan.FromMilliseconds(500));
        tooltip.Tick(ui.Now);
        ui.Frame();

        ui.Get("#hint").ShouldBeVisible();
    }

    [Fact]
    public void A_menu_item_click_closes_the_whole_chain() {
        using var ui = Opened();
        var menu = ui.Add<Menu>("actions");

        var cut = menu.AddItem("Cut");
        menu.AddItem("Copy");

        var invoked = false;
        cut.Clicked += _ => invoked = true;

        menu.Open();
        ui.Frame();
        ui.Get("#actions").ShouldBeVisible();

        ui.Get("menu-item").First().Click();

        Assert.True(invoked);
        ui.Get("#actions").ShouldNotBeVisible();
    }

    [Fact]
    public void A_context_menu_opens_where_it_was_asked_to() {
        using var ui = Opened();
        var menu = ui.Add<ContextMenu>("context");
        menu.AddItem("Rename");

        menu.OpenAt(220f, 140f);
        ui.Frame();

        var bounds = ui.Get("#context").Element.Bounds;

        ui.Get("#context").ShouldBeVisible();
        Assert.True(bounds.X >= 200f, $"expected it near the point it was opened at, got x={bounds.X}");
        Assert.True(bounds.Y >= 120f, $"expected it near the point it was opened at, got y={bounds.Y}");
    }

    [Fact]
    public void A_toast_goes_away_on_its_own_once_the_clock_moves() {
        using var ui = ControlHarness.Open(400f, 300f, "toast { width: 200px; height: 48px; }");
        var host = ui.Add<ToastHost>("toasts");

        // ⚠ `Show`, not `Add<Toast>`. The host tracks what it showed, and a toast added the other
        // way is drawn and dismissable by clicking but never expires — the same shape of footgun as
        // `RadioGroup.AddOption`, and worth stating because the failure looks like a broken timer.
        host.Show("Saved", duration: TimeSpan.FromMilliseconds(300));
        ui.Frame();

        ui.Get("toast").ShouldExist();

        // ⚠ Two ticks, and the first one is not a formality: a toast's clock starts on the first
        // tick it sees rather than when it was created, because when it was created is a time
        // nothing in the control knows. One tick after the duration would only start it.
        host.Tick(ui.Now);
        ui.Advance(TimeSpan.FromMilliseconds(400));
        host.Tick(ui.Now);
        ui.Frame();

        // ⚠ The clock is the test's, so this takes microseconds and cannot be flaky. A toast suite
        // written against a real clock is a suite that sleeps for a second per case.
        ui.Get("toast").ShouldNotExist();
    }

    static bool IsInside(UiElement? element, UiElement ancestor) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, ancestor)) {
                return true;
            }
        }

        return false;
    }
}
