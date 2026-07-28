// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a control takes with it when it goes.</summary>
/// <remarks>
///     <para>
///         An overlay is a child of the <i>root</i> rather than of the control that opened it, forced
///         by painting order: a popup inside the button that opened it is clipped by every
///         <c>overflow: hidden</c> between the two. The cost is that removing the control does not
///         remove the popup, because they are not related — and until <c>UiElement.OnRemoved</c>
///         existed there was nowhere to pay it.
///     </para>
///     <para>
///         ⚠ <b>Both halves leak and only one of them is visible.</b> The popup itself is a closed
///         element drawing nothing, so a leaked one costs memory and a style slot and shows nothing on
///         screen. The two capture handlers <see cref="Overlay" /> registers on the root are worse:
///         they close over the removed overlay, they run for every pointer event and every key in the
///         whole application, and what they do when they run is ask a removed element questions.
///     </para>
/// </remarks>
public class OverlayLifetimeTests {
    [Fact]
    public void Removing_a_select_removes_the_list_it_parented_on_the_root() {
        using var fixture = new ControlFixture();

        var select = fixture.Document.Root.Add<Select>();
        select.AddOption("one");
        select.AddOption("two");

        var list = select.List;
        fixture.Document.Update();

        Assert.False(list.IsRemoved);
        Assert.Same(fixture.Document.Root, list.Parent);

        fixture.Document.Remove(select);

        Assert.True(list.IsRemoved);
    }

    [Fact]
    public void Removing_a_menu_bar_removes_every_menu_it_dropped() {
        using var fixture = new ControlFixture();

        var bar = fixture.Document.Root.Add<MenuBar>();
        var file = bar.AddMenu("File");
        var submenu = file.AddSubmenu("Recent");

        fixture.Document.Update();
        fixture.Document.Remove(bar);

        // The submenu is a root child of a root child's making, so this also asserts the recursion:
        // the bar removes its menus, and a menu removes its own submenus.
        Assert.True(file.IsRemoved);
        Assert.True(submenu.IsRemoved);
    }

    [Fact]
    public void A_pointer_event_after_the_removal_does_not_reach_the_overlay() {
        using var fixture = new ControlFixture();

        var select = fixture.Document.Root.Add<Select>();
        select.AddOption("one");
        fixture.Document.Update();

        // Open it, so the light-dismiss handler is past its `IsOpen` guard and will actually do
        // something when it next runs. A closed overlay's handler returns immediately and hides the
        // leak completely, which is why this test opens the thing before removing it.
        fixture.Click(select);
        fixture.Document.Update();
        Assert.True(select.List.IsOpen);

        fixture.Document.Remove(select);

        // With the handlers left on the root, this press reaches `Overlay.Dismiss` on a removed
        // element, which asks it for its `Document` — and a removed element throws rather than
        // answering. The symptom is an exception from a click on the far side of the window, several
        // interactions after the control that caused it stopped existing.
        var exception = Record.Exception(() => fixture.Press(400f, 300f));
        Assert.Null(exception);
    }

    [Fact]
    public void A_key_after_the_removal_does_not_reach_it_either() {
        using var fixture = new ControlFixture();

        var select = fixture.Document.Root.Add<Select>();
        select.AddOption("one");
        fixture.Document.Update();

        fixture.Click(select);
        fixture.Document.Update();

        fixture.Document.Remove(select);

        // The same leak through the other handler — `Escaped` rather than `Dismiss`. Two
        // registrations, two removals, and a test that only pressed the pointer would let a
        // half-finished `OnRemoved` through.
        var exception = Record.Exception(() => fixture.Type(InputKey.Escape));
        Assert.Null(exception);
    }
}
