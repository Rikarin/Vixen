// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What <c>help="…"</c> in a <c>.vxml</c> reaches.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assertion is the accessibility tree and not the element count</b>, because the
///         implementation this is guarding against is the one that draws a box and tells nobody.
///         <c>Tooltip.Attach</c> wires <c>AccessibleRelation.DescribedBy</c> as well as the hover
///         handler, and a markup spelling that only made a tooltip and set its text would look
///         right in every screenshot and be silent to every screen reader — which is the half a
///         hover is unable to deliver, since hovering is a gesture a screen-reader user does not
///         make.
///     </para>
///     <para>
///         ⚠ <b>The layering this proves is the seam and not the call.</b> <c>BuildContext</c> is in
///         <c>Vixen.Ui</c> and <c>Tooltip</c> is in <c>Vixen.Ui.Controls</c>, which references it —
///         so the generated file cannot name the type, and emitting
///         <c>global::Vixen.Ui.Controls.Tooltip.Attach(…)</c> would produce generated code that does
///         not compile in a project referencing only <c>Vixen.Ui</c>. What runs here is
///         <c>BuildContext.Describes</c>, filled from the control library's module initializer the
///         same way <c>on:click</c>'s meaning is.
///     </para>
/// </remarks>
public class HelpMarkupTests {
    /// <summary>A fixed <c>help</c> on a control is that control's accessible description.</summary>
    [Fact]
    public void A_help_attribute_describes_the_element_rather_than_only_hovering_over_it() {
        using var ui = Sheet(out var sheet);

        // ⚠ The description and not the tooltip's existence. A tooltip that exists, says the right
        // words and is attached to nothing passes every assertion about the element tree.
        Assert.Equal("Writes the scene to disk", sheet.Save.AccessibleDescription);

        // And it did not take the name over: a button says what it is and a description says what
        // it does.
        Assert.Equal("Save", sheet.Save.AccessibleName);
    }

    /// <summary>The tooltip it made is a root child, which is where every overlay goes.</summary>
    /// <remarks>
    ///     The draw list is document order, so a tooltip nested inside the button it describes would
    ///     be clipped by every <c>overflow: hidden</c> between the two and painted at that button's
    ///     stacking position.
    /// </remarks>
    [Fact]
    public void The_tooltip_a_help_attribute_makes_is_a_root_child() {
        using var ui = Sheet(out var sheet);

        var tips = ui.Document.Root.Children.OfType<Tooltip>().ToList();

        // One per `help`, in source order, each carrying the sentence its element is described by.
        Assert.Equal(["Writes the scene to disk", "Nothing has happened yet"], tips.Select(tip => tip.Label));
        Assert.All(tips, tip => Assert.Same(ui.Document.Root, tip.Parent));

        // ⚠ And none of them is under the element it describes, which is the arrangement that looks
        // right until something between the two clips.
        Assert.Empty(sheet.Save.Children.OfType<Tooltip>());
    }

    /// <summary>A <c>help</c> that reads a signal follows it.</summary>
    /// <remarks>
    ///     ⚠ <b>Attached once and written many times.</b> The attachment is a pointer subscription
    ///     and an accessible relation; re-running the whole of it per flush would add a second
    ///     handler and a second relation each time, so only the text is inside the effect. The
    ///     tooltip count is asserted after the change for exactly that reason.
    /// </remarks>
    [Fact]
    public void A_dynamic_help_follows_its_signal_without_attaching_a_second_tooltip() {
        using var ui = Sheet(out var sheet);

        Assert.Equal("Nothing has happened yet", sheet.Status.AccessibleDescription);

        sheet.Hint.Value = "Imported 4 meshes";
        ui.Document.Effects.Flush();

        Assert.Equal("Imported 4 meshes", sheet.Status.AccessibleDescription);
        Assert.Equal(2, ui.Document.Root.Children.OfType<Tooltip>().Count());
    }

    static UiTest Sheet(out HelpSheet sheet) {
        var ui = ControlHarness.Open(500f, 400f);

        sheet = new();

        BuildContext.BuildInto(sheet, ui.Document, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
