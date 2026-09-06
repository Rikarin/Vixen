// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The ported component panel, written down in four states.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A committed dump rather than a wave note.</b> Nine rows of the panel ledger claim
///         "byte-identical in N dumped states" and only a handful of test files ever dumped a tree;
///         every other comparison was run once, read and deleted. This is the comparison, kept —
///         and it is the evidence for the last row on that ledger.
///     </para>
///     <para>
///         ⚠ <b>The header is one subject, because the header is what changed.</b> The port's claim
///         is that <c>slot="header"</c> plus two <c>order</c> rules put the icon, the label and the
///         remove button exactly where <c>Document.Move(glyph, 1)</c> used to. The tree dump below
///         is in <i>document</i> order — chevron, label, icon, button, which is what appending to a
///         slot produces — and its <i>rectangles</i> are in painted order. Both halves are the
///         claim: the markup does not reorder and the stylesheet does.
///     </para>
///     <para>
///         ⚠ <b>And a flags dump, because a tree dump is blind.</b> <c>Label</c> lives in a part the
///         control owns, <c>IsExpanded</c> is a property and <c>Number</c> is a drawer's; none of
///         them appears in a tree. Wave 7 shipped a panel that matched byte-for-byte in six states
///         while carrying a binding that could not work, which is what <c>UiTest.Flags</c> exists
///         for.
///     </para>
///     <para>
///         ⚠ <b>Every state is reached through the interface.</b> The collapse is a click on the
///         header, the reorder is a real pointer drag, and the removal is a click on the button the
///         markup put in the header — so what is exercised is the routed <c>on:</c> legs the port
///         introduced, and not a model somebody poked. Wave 7's dumps only ever drove the model,
///         which is the leg that cannot fail.
///     </para>
/// </remarks>
public sealed class ComponentsViewDumpTests {
    /// <summary>The header a foldout is built with, in full.</summary>
    [Fact]
    public void The_header_is_the_one_the_hand_written_loop_built() {
        using var editor = Crate(out var components);

        Assert.Equal(
            """
            <expander-header .size-md .variant-default> 0,1 320×36
              <icon .size-md .variant-default> 6,12 12×12
              <label> 54,7 117×22 "Primitive Shape"
              <icon .component-icon .size-md .variant-default> 26,10 16×16
              <icon-button .remove-component .size-sm .variant-subtle> 294,7 20×20
                <icon .size-md .variant-default> 4,4 12×12
                <label> 0,0 0×0 "Remove Component"
            """,
            editor.Ui.Tree(components.Sections[0].Header)
        );
    }

    /// <summary>
    ///     ⚠ <b>The icon is <i>painted</i> between the chevron and the label, which the dump above
    ///     shows only in its numbers.</b> A slot appends, so the document order is chevron, label,
    ///     icon, button and the tree walks children — the four rectangles are the only thing that
    ///     says the <c>order</c> rules took effect. This is the assertion that goes red if somebody
    ///     deletes them and leaves the markup alone, which a byte comparison of the tree would not.
    /// </summary>
    [Fact]
    public void The_slotted_icon_is_painted_between_the_chevron_and_the_label() {
        using var editor = Crate(out var components);
        var header = components.Sections[0].Header;

        var chevron = header.Children.First(child => child is Icon && !child.HasClass("component-icon"));
        var glyph = header.Children.First(child => child.HasClass("component-icon"));
        var label = header.Children.First(child => child.Tag == "label");
        var remove = header.Children.First(child => child.HasClass("remove-component"));

        Assert.True(
            chevron.AbsoluteLeft < glyph.AbsoluteLeft
            && glyph.AbsoluteLeft < label.AbsoluteLeft
            && label.AbsoluteLeft < remove.AbsoluteLeft,
            $"chevron {chevron.AbsoluteLeft}, icon {glyph.AbsoluteLeft}, "
            + $"label {label.AbsoluteLeft}, remove {remove.AbsoluteLeft}"
        );
    }

    /// <summary>What the tree cannot see, in four states, each reached through the interface.</summary>
    [Fact]
    public void The_flags_dump_follows_the_panel_through_four_states() {
        using var editor = Crate(out var components);

        Assert.Equal(Open, Flags(editor, components));

        // ── Collapsed, by clicking the header the markup filled ──────────────
        editor.Click(components.Sections[0].Header);
        editor.Settle();

        Assert.Equal(Shut, Flags(editor, components));

        // ── Open again, which is a state a person can get back to ────────────
        editor.Click(components.Sections[0].Header);
        editor.Settle();

        Assert.Equal(Reopened, Flags(editor, components));

        // ── Reordered, by a real pointer drag on the second header ───────────
        var handle = components.Sections[1].Header.Bounds;
        var target = components.Sections[0].Header.Bounds;

        editor.Ui
            .At(handle.X + (handle.Width * 0.5f), handle.Y + (handle.Height * 0.5f))
            .DragTo(target.X + (target.Width * 0.5f), target.Y + 2f);

        editor.Settle();

        Assert.Equal(Swapped, Flags(editor, components));
    }

    /// <summary>
    ///     ⚠ <b>The remove button in the header removes the component and nothing else.</b> It is a
    ///     button inside the strip that toggles the foldout, so its click bubbles through
    ///     <c>Expander.Chosen</c> on the way out — which is what <c>on:click.stop</c> is on it for.
    ///     Without the modifier the section it is removing shuts as it goes, and so does any other
    ///     one whose header a stray click reaches.
    /// </summary>
    [Fact]
    public void The_remove_button_takes_the_component_off_and_leaves_the_other_open() {
        using var editor = Crate(out var components);

        var remove = components.Sections[1].Header.Children.First(child => child.HasClass("remove-component"));

        editor.Click(remove);
        editor.Settle();

        var section = Assert.Single(components.Sections);

        Assert.Equal("Primitive Shape", section.Label);
        Assert.True(section.IsExpanded, "the surviving foldout shut when its neighbour was removed");
    }

    /// <summary>
    ///     ⚠ <b>A drag that begins in a foldout's <i>body</i> reorders nothing, which is the half of
    ///     this gesture that has no visible success.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The subscription is <c>on:dragstart.slot-header</c>, so the handler is on
    ///         <c>Expander.Header</c> and a gesture begun below it is not on that route at all — it
    ///         replaces eleven lines that walked up from <c>DragEvent.Source</c> asking the same
    ///         question. ⚠ <b>Both spellings answer the same way on the day they work</b>, so a test
    ///         that only dragged headers would pass over a subscription that had quietly gone back to
    ///         the whole foldout, and the panel would reorder itself whenever anybody dragged a
    ///         numeric field.
    ///     </para>
    ///     <para>
    ///         The drop indicator is asserted too, because a drag that armed the indicator and then
    ///         declined to move anything is a third state — a line that appears under a gesture that
    ///         does nothing, which is the panel lying about what a release will do.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_drag_that_begins_inside_a_body_reorders_nothing() {
        using var editor = Crate(out var components);

        var before = components.Sections.Select(section => section.Label ?? string.Empty).ToArray();

        Assert.Equal(["Primitive Shape", "Light"], before);

        // A row in the *second* foldout's body — not its header — dragged up over the first one,
        // which is the movement that reorders when it starts one strip higher.
        var row = Descendants(components.Sections[1])
            .First(child => child.Tag == "inspector-row" && !IsInside(child, components.Sections[1].Header));

        var target = components.Sections[0].Header.Bounds;

        editor.Ui
            .At(row.AbsoluteLeft + (row.Width * 0.5f), row.AbsoluteTop + (row.Height * 0.5f))
            .DragTo(target.X + (target.Width * 0.5f), target.Y + 2f);

        editor.Settle();

        Assert.Equal(before, components.Sections.Select(section => section.Label ?? string.Empty).ToArray());
        Assert.True(components.DropIndicator.HasClass("hidden"), "the drop line was armed by a drag that moved nothing");
    }

    /// <summary>
    ///     ⚠ <b>The line lands where the drop lands, measured mid-gesture rather than after it.</b>
    /// </summary>
    /// <remarks>
    ///     A drop indicator is the only thing telling somebody where a release will put the foldout,
    ///     so an indicator that is right most of the time is worse than none — and once the pointer
    ///     is up there is nothing left to compare it with. This holds the pointer over the first
    ///     header, reads the line, and then releases and checks the section landed at the line: two
    ///     readings of one gesture, which is the only arrangement that can catch them disagreeing.
    /// </remarks>
    [Fact]
    public void The_drop_line_is_where_the_release_puts_the_foldout() {
        using var editor = Crate(out var components);

        var handle = components.Sections[1].Header.Bounds;
        var top = components.Sections[0].Bounds;

        editor.Ui.MovePointer(handle.X + (handle.Width * 0.5f), handle.Y + (handle.Height * 0.5f));
        editor.Ui.PressPointer();
        editor.Ui.Frame();

        // Four steps, because the recogniser decides a drag has begun by watching the pointer move
        // and one jump gives it a single sample.
        for (var step = 1; step <= 4; step++) {
            var t = step / 4f;
            editor.Ui.MovePointer(
                handle.X + (handle.Width * 0.5f),
                handle.Y + (handle.Height * 0.5f) + ((top.Y + 2f - handle.Y - (handle.Height * 0.5f)) * t)
            );

            editor.Ui.Frame();
        }

        Assert.False(components.DropIndicator.HasClass("hidden"), "no line was shown for a drag in progress");

        // The gap above the first foldout, so the line sits on its top edge.
        var line = components.DropIndicator.AbsoluteTop;

        Assert.Equal(top.Y, line, 1);

        editor.Ui.ReleasePointer();
        editor.Ui.Frame();
        editor.Settle();

        Assert.Equal(["Light", "Primitive Shape"], components.Sections.Select(section => section.Label ?? string.Empty).ToArray());
        Assert.Equal(line, components.Sections[0].Bounds.Y, 1);
        Assert.True(components.DropIndicator.HasClass("hidden"), "the line outlived the drop");
    }

    static bool IsInside(UiElement element, UiElement ancestor) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (ReferenceEquals(walk, ancestor)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The state the panel opens in: two foldouts, both open, nothing focused.</summary>
    const string Open =
        """
        <expander .component .open .size-md .variant-default> State=Open IsExpanded=True Label="Primitive Shape"
        <expander-header .size-md .variant-default> State=Checked Label="Primitive Shape"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Cube"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <icon-button .size-sm .variant-subtle> Label="Pick"
        <icon-button .size-sm .variant-subtle> Label="Clear"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <expander .component .open .size-md .variant-default> State=Open IsExpanded=True Label="Light"
        <expander-header .size-md .variant-default> State=Checked Label="Light"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Point"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <color-input .size-md .variant-default> Value=(1, 1, 1, 1)
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="127.324" Number=127.324
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Candela"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="10.000" Number=10
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <button .add-component .size-md .variant-default> Label="Add Component"
        """;

    /// <summary>The first one collapsed, and the header that took the click has the focus.</summary>
    const string Shut =
        """
        <components .size-md .variant-default> State=FocusWithin
        <component-list> State=FocusWithin
        <expander .component .size-md .variant-default> State=FocusWithin Label="Primitive Shape"
        <expander-header .size-md .variant-default> State=Focus, FocusWithin Label="Primitive Shape"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Cube"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <icon-button .size-sm .variant-subtle> Label="Pick"
        <icon-button .size-sm .variant-subtle> Label="Clear"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <expander .component .open .size-md .variant-default> State=Open IsExpanded=True Label="Light"
        <expander-header .size-md .variant-default> State=Checked Label="Light"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Point"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <color-input .size-md .variant-default> Value=(1, 1, 1, 1)
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="127.324" Number=127.324
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Candela"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="10.000" Number=10
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <button .add-component .size-md .variant-default> Label="Add Component"
        """;

    /// <summary>Open again. Not byte-identical with the first state, and must not be.</summary>
    const string Reopened =
        """
        <components .size-md .variant-default> State=FocusWithin
        <component-list> State=FocusWithin
        <expander .component .open .size-md .variant-default> State=FocusWithin, Open IsExpanded=True Label="Primitive Shape"
        <expander-header .size-md .variant-default> State=Focus, Checked, FocusWithin Label="Primitive Shape"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Cube"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <icon-button .size-sm .variant-subtle> Label="Pick"
        <icon-button .size-sm .variant-subtle> Label="Clear"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <expander .component .open .size-md .variant-default> State=Open IsExpanded=True Label="Light"
        <expander-header .size-md .variant-default> State=Checked Label="Light"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Point"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <color-input .size-md .variant-default> Value=(1, 1, 1, 1)
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="127.324" Number=127.324
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Candela"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="10.000" Number=10
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <button .add-component .size-md .variant-default> Label="Add Component"
        """;

    /// <summary>After the drag. The focus moved with the element rather than staying at index 0.</summary>
    const string Swapped =
        """
        <components .size-md .variant-default> State=FocusWithin
        <component-list> State=FocusWithin
        <expander .component .open .size-md .variant-default> State=FocusWithin, Open IsExpanded=True Label="Light"
        <expander-header .size-md .variant-default> State=Focus, Checked, FocusWithin Label="Light"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Point"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <color-input .size-md .variant-default> Value=(1, 1, 1, 1)
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="127.324" Number=127.324
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Candela"
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="10.000" Number=10
        <icon-button .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <numeric-input .size-md .variant-default> State=Valid Value="0.000" Number=0
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <expander .component .open .size-md .variant-default> State=Open IsExpanded=True Label="Primitive Shape"
        <expander-header .size-md .variant-default> State=Checked Label="Primitive Shape"
        <icon-button .remove-component .size-sm .variant-subtle> Label="Remove Component"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <select .size-md .variant-default> State=Valid Value="Cube"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <inspector-row .size-md .variant-default> Label=Vixen.Ui.UiElement
        <icon-button .size-sm .variant-subtle> Label="Pick"
        <icon-button .size-sm .variant-subtle> Label="Clear"
        <icon-button .hidden .size-sm .variant-subtle> Label="Reset"
        <button .add-component .size-md .variant-default> Label="Add Component"
        """;
    /// <summary>The panel's flags, with the pointer parked first.</summary>
    /// <remarks>
    ///     ⚠ <b><c>State</c> is pointer-dependent and three of these states are reached by
    ///     clicking.</b> A reading taken with the pointer still resting on the header it just
    ///     pressed carries <c>Hover</c> and one taken elsewhere does not — a property of the thing
    ///     being measured rather than of the measurement, and the same difference wave 6 recorded
    ///     between a pooled row's hover and a keyed one's. Parking is what makes the four dumps
    ///     comparable with each other.
    /// </remarks>
    static string Flags(EditorSession editor, ComponentsView components) {
        editor.Ui.At(2f, 2f).Hover();
        editor.Settle();

        return editor.Ui.Flags(components);
    }

    /// <summary>An editor showing a crate with two components on it, settled.</summary>
    static EditorSession Crate(out ComponentsView components) {
        var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Open("inspector");
        editor.Settle();

        var found = Descendants(editor.Panel("inspector")).OfType<ComponentsView>().FirstOrDefault()
            ?? throw editor.Fail("the inspector has no components section");

        // The crate has a mesh shape; a light as well is two foldouts, which is what a reorder and a
        // removal both need.
        Lights.Attach(editor.Scene.World, editor.Scene.Selection[0], LightKind.Point);

        found.Show(editor.Scene.Selection[0]);
        editor.Settle();

        components = found;
        return editor;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
