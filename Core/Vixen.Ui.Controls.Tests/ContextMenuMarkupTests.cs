// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What <c>context-menu="@Menu"</c> in a <c>.vxml</c> reaches.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Asserted by dispatching the gesture, not by the attribute having bound without
///         error.</b> The failure this is guarding against is the one an attribute test cannot see:
///         a directive that evaluates its expression, calls nothing, and leaves a panel whose
///         right-click does nothing at all.
///     </para>
///     <para>
///         ⚠ <b>The spelling is an expression rather than a nested <c>&lt;ContextMenu&gt;</c>, and
///         the reason belongs to the binder rather than to the menu.</b> An overlay has to be a
///         child of the document root, and deciding that a tag needs re-parenting means knowing that
///         the tag names an overlay — the type resolution VXML deliberately does not do. A
///         <c>&lt;ContextMenu&gt;</c> written where it is used would compile, build, and open inside
///         the panel that declared it, clipped by everything above it.
///     </para>
/// </remarks>
public class ContextMenuMarkupTests {
    /// <summary>A secondary press inside the element opens the menu it names.</summary>
    [Fact]
    public void A_secondary_press_opens_the_menu_the_attribute_named() {
        using var ui = Sheet(out var sheet);

        Assert.False(sheet.Rows.IsOpen);

        Press(sheet.First, 120f, 90f);
        ui.Frame();

        Assert.True(sheet.Rows.IsOpen);
        Assert.Equal(["Rename", "Delete"], sheet.Rows.Items.Select(item => item.Label));
    }

    /// <summary>A primary press does not, which is the half that says the button was read.</summary>
    /// <remarks>
    ///     Without this the test passes for an attachment that opens the menu on any press — and
    ///     a panel whose left click opens a context menu is worse than one whose right click does
    ///     nothing.
    /// </remarks>
    [Fact]
    public void A_primary_press_leaves_it_closed() {
        using var ui = Sheet(out var sheet);

        sheet.First.Raise(
            new PointerEvent { Action = PointerAction.Pressed, Button = PointerButton.Primary, X = 120f, Y = 90f }
        );

        ui.Frame();
        Assert.False(sheet.Rows.IsOpen);
    }

    /// <summary>
    ///     ⚠ <b>One menu, two elements: the directive attaches and does not make.</b>
    /// </summary>
    /// <remarks>
    ///     That is the asymmetry with <c>help</c>, whose tooltip is the directive's to build and to
    ///     remove. A menu is a model — every hand-written caller here builds one from commands and
    ///     keeps it — so two rows naming the same expression share one menu, and the document holds
    ///     one <c>context-menu</c> rather than two.
    /// </remarks>
    [Fact]
    public void Two_elements_naming_one_menu_share_it_rather_than_each_getting_one() {
        using var ui = Sheet(out var sheet);

        Assert.Single(ui.Document.Root.Children.OfType<ContextMenu>());

        Press(sheet.Second, 200f, 150f);
        ui.Frame();

        Assert.True(sheet.Rows.IsOpen);
        Assert.Single(ui.Document.Root.Children.OfType<ContextMenu>());
    }

    static void Press(UiElement target, float x, float y) =>
        target.Raise(
            new PointerEvent { Action = PointerAction.Pressed, Button = PointerButton.Secondary, X = x, Y = y }
        );

    static UiTest Sheet(out ContextMenuSheet sheet) {
        var ui = ControlHarness.Open(500f, 400f);

        sheet = ui.Document.Create<ContextMenuSheet>(null, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
