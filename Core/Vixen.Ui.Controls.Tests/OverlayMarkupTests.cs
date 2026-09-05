// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Where a tag nested inside a <c>&lt;Dialog&gt;</c> or a <c>&lt;Drawer&gt;</c> lands.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The two most application-shaped overlays in the set were the two that could not be
///         written in markup at all.</b> <c>Popover</c> overrides <c>UiElement.ContentHost</c>, which
///         is what makes <c>&lt;Popover&gt;</c>, <c>&lt;Select&gt;</c> and <c>&lt;ComboBox&gt;</c>
///         nesting work; <c>Dialog</c> and <c>Drawer</c> overrode neither, so a child landed on the
///         overlay element itself — a sibling of the backdrop and the surface.
///     </para>
///     <para>
///         ⚠ <b>That is not a cosmetic offset, and asserting membership would miss it.</b> The theme
///         lays <c>dialog</c> and <c>drawer</c> out across the whole viewport with the surface
///         positioned inside them, so the misplaced child still existed, still drew and still
///         measured — at the window's top-left corner, behind the backdrop. So these assert the
///         child's <i>parent</i>, by reference, and assert that the overlay's own children are still
///         exactly its two parts.
///     </para>
///     <para>
///         Written from a committed <c>.vxml</c> rather than from <c>BuildContext</c> calls, for
///         <c>DisclosureMarkupTests</c>' reason: the claim is that the language reaches the control,
///         and a hand-written <c>Build</c> body is the half that already worked.
///     </para>
/// </remarks>
public class OverlayMarkupTests {
    /// <summary>A tag written inside a <c>&lt;Dialog&gt;</c> is built in its body.</summary>
    [Fact]
    public void A_dialog_s_nested_markup_lands_in_its_body() {
        using var ui = Sheet(out var sheet);

        var dialog = sheet.Question;
        var note = Assert.Single(dialog.Body.Children);

        Assert.Equal("dialog-note", note.Tag);

        // ⚠ Reference identity on the parent, because "an element with this tag exists somewhere
        // under the dialog" is true of the broken arrangement too.
        Assert.Same(dialog.Body, note.Parent);

        // And nothing leaked onto the overlay element, which is the half a body-only assertion
        // misses: before the override, `dialog-note` was the third child here.
        Assert.Equal(["dialog-backdrop", "dialog-surface"], dialog.Children.Select(child => child.Tag));
    }

    /// <summary>And inside a <c>&lt;Drawer&gt;</c>, including a control rather than a bare part.</summary>
    /// <remarks>
    ///     Two children, so the claim is not a single-element coincidence, and one of them is a
    ///     <see cref="CheckBox" /> — a control reached through <c>ctx.Child&lt;T&gt;</c> rather than
    ///     a plain element, which is the other half of what a nested tag can be.
    /// </remarks>
    [Fact]
    public void A_drawer_s_nested_markup_lands_in_its_body_in_source_order() {
        using var ui = Sheet(out var sheet);

        var drawer = sheet.Filters;

        Assert.Equal(["drawer-note", "checkbox"], drawer.Body.Children.Select(child => child.Tag));
        Assert.All(drawer.Body.Children, child => Assert.Same(drawer.Body, child.Parent));

        var box = Assert.IsType<CheckBox>(drawer.Body.Children[1]);
        Assert.Equal("Only modified", box.Label);

        Assert.Equal(["drawer-backdrop", "drawer-surface"], drawer.Children.Select(child => child.Tag));
    }

    /// <summary>
    ///     ⚠ <b>And the body is inside the surface, which is the element the theme moves.</b>
    /// </summary>
    /// <remarks>
    ///     The consequence the issue is actually about: a drawer's surface is a strip against one
    ///     edge and the drawer itself is the whole window. Asserting the ancestry rather than a
    ///     rectangle keeps this off the wall clock and off the theme's exact numbers, while still
    ///     failing for the arrangement that put the author's content outside the panel.
    /// </remarks>
    [Fact]
    public void The_body_a_nested_tag_reaches_is_inside_the_surface_that_slides_in() {
        using var ui = Sheet(out var sheet);

        Assert.Same(sheet.Filters.Surface, sheet.Filters.Body.Parent);
        Assert.Same(sheet.Question.Surface, sheet.Question.Body.Parent);
    }

    static UiTest Sheet(out OverlaySheet sheet) {
        var ui = ControlHarness.Open(500f, 400f);

        sheet = new();

        BuildContext.BuildInto(sheet, ui.Document, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
