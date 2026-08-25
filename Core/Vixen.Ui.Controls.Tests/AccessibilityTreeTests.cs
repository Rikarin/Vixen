// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Doc 09's promised ARIA-role snapshot, written.</summary>
/// <remarks>
///     <para>
///         <b>The acceptance criterion of doc 46 § A2 is that this file can exist</b>, and the
///         criterion doc 46 took it from is doc 09's own Testing table: <i>"Per control: keyboard
///         interaction matrix, ARIA-role snapshot, virtualisation […] and a golden image."</i> Before
///         this there was no role, no name, no value and no relation anywhere in <c>Vixen.Ui</c>, so
///         the promise could not be kept by anybody.
///     </para>
///     <para>
///         ⚠ <b>A snapshot test passes beautifully against an empty tree, which is why the first
///         assertion in every test here is not the snapshot.</b> <c>Render</c> of a document with no
///         accessibility at all is the empty string, and an expectation of the empty string matches
///         it — so each test asserts <see cref="AccessibilitySnapshot.Unnamed" /> is empty first
///         (every widget has a role <i>and</i> a name) and only then that the tree is the shape it
///         should be. Dropping a role or dropping a name turns the first one red on its own; that is
///         recorded in the commit message with the output.
///     </para>
///     <para>
///         ⚠ <b>And none of it references anything under <c>Editor/</c>.</b> This is a
///         <c>Vixen.Ui.Controls</c> test project, which is the application doc 46's acceptance lines
///         are written about.
///     </para>
/// </remarks>
public class AccessibilityTreeTests {
    /// <summary>A labelled field: the caption is an element, and the field points at it.</summary>
    /// <remarks>
    ///     ⚠ <b>Two elements and one relation, which is the whole of what a field's name is.</b> A
    ///     <see cref="TextField" /> deliberately answers <c>null</c> to
    ///     <c>NativeAccessibleName</c> — its placeholder is a hint rather than a name and vanishes
    ///     when there is a value — so this is the supported way to name one, and a field nobody did
    ///     this to is caught by <see cref="AccessibilitySnapshot.Unnamed" />.
    /// </remarks>
    static T Labelled<T>(UiElement parent, string caption) where T : Control, new() {
        var text = parent.Add<TextBlock>();
        text.Text = caption;

        var field = parent.Add<T>();
        field.AddAccessibleRelation(AccessibleRelation.LabelledBy, text);

        return field;
    }

    [Fact]
    public void A_button_is_a_button_and_its_label_is_its_name() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = "Save";

        Assert.Equal(AccessibleRole.Button, button.Role);
        Assert.Equal("Save", button.AccessibleName);
        Assert.Null(button.AccessibleValue);

        // Nothing assigned a role or a name on this button. Both come from the type.
        Assert.Empty(AccessibilitySnapshot.Unnamed(button));
        Assert.Equal("button \"Save\"", AccessibilitySnapshot.Render(button));
    }

    [Fact]
    public void A_disabled_button_says_so_without_the_control_tracking_it() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = "Save";

        Assert.False((button.AccessibleState & AccessibleStates.Disabled) != 0);
        Assert.True((button.AccessibleState & AccessibleStates.Focusable) != 0);

        button.Disabled = true;
        fixture.Update();

        // ⚠ `Button` has no accessibility code for this at all — the bit is derived from
        // `ElementState.Disabled`, which `Control.Disabled` was already setting for the cascade. That
        // is the reason `AccessibleState` is computed rather than stored: fifty controls cannot each
        // forget it.
        Assert.True((button.AccessibleState & AccessibleStates.Disabled) != 0);
        Assert.False((button.AccessibleState & AccessibleStates.Focusable) != 0);
    }

    [Fact]
    public void A_field_is_editable_carries_its_value_and_is_named_by_the_words_beside_it() {
        using var fixture = new ControlFixture();

        var field = Labelled<TextBox>(fixture.Document.Root, "Project name");
        field.Value = "Vixen";
        fixture.Update();

        Assert.Equal(AccessibleRole.TextBox, field.Role);
        Assert.Equal("Project name", field.AccessibleName);
        Assert.Equal("Vixen", field.AccessibleValue);
        Assert.True((field.AccessibleState & AccessibleStates.Editable) != 0);
        Assert.False((field.AccessibleState & AccessibleStates.ReadOnly) != 0);

        field.ReadOnly = true;
        fixture.Update();

        // ⚠ Both, not one instead of the other: a read-only field is still a field. See the property's
        // own remarks about the conflation with `Disabled`.
        Assert.True((field.AccessibleState & AccessibleStates.Editable) != 0);
        Assert.True((field.AccessibleState & AccessibleStates.ReadOnly) != 0);
    }

    [Fact]
    public void An_unlabelled_field_is_caught_rather_than_given_a_plausible_name() {
        using var fixture = new ControlFixture();

        var field = fixture.Add<TextBox>();
        field.Placeholder = "0.00";
        fixture.Update();

        // The placeholder is not a name. Every toolkit that used it produced forms of four fields
        // all called "0.00", and a field that has been filled in has no placeholder left at all.
        Assert.Null(field.AccessibleName);

        var offenders = AccessibilitySnapshot.Unnamed(field);
        Assert.Equal(["<textbox> is a textbox and has no accessible name"], offenders);
    }

    [Fact]
    public void A_checkbox_reports_ticked_and_half_ticked_as_different_things() {
        using var fixture = new ControlFixture();

        var box = fixture.Add<CheckBox>();
        box.Label = "Overwrite existing";
        fixture.Update();

        Assert.Equal(AccessibleRole.CheckBox, box.Role);
        Assert.Equal(AccessibleStates.None, box.NativeState());

        box.IsChecked = true;
        fixture.Update();
        Assert.Equal(AccessibleStates.Checked, box.NativeState());

        box.IsIndeterminate = true;
        fixture.Update();

        // ⚠ `Mixed` replaces `Checked` rather than joining it: `aria-checked` is one value with three
        // settings, and "ticked and half-ticked" is not one of them.
        Assert.Equal(AccessibleStates.Mixed, box.NativeState());
    }

    [Fact]
    public void A_toggle_button_is_pressed_where_a_checkbox_is_ticked() {
        using var fixture = new ControlFixture();

        var bold = fixture.Add<ToggleButton>();
        bold.Label = "Bold";
        bold.IsChecked = true;
        fixture.Update();

        // Both set `ElementState.Checked` for `:checked`, and they mean different things to somebody
        // listening — which is why the accessible state is the control's to compute and not a
        // derivation from the cascade's flags.
        Assert.Equal(AccessibleRole.Button, bold.Role);
        Assert.Equal(AccessibleStates.Pressed, bold.NativeState());

        var box = fixture.Add<CheckBox>();
        box.Label = "Bold";
        box.IsChecked = true;
        fixture.Update();

        Assert.Equal(AccessibleStates.Checked, box.NativeState());
    }

    [Fact]
    public void A_tab_says_which_panel_it_controls_and_the_panel_says_which_tab_names_it() {
        using var fixture = new ControlFixture();

        var tabs = fixture.Add<Tabs>();
        var general = tabs.AddTab("General");
        var advanced = tabs.AddTab("Advanced");
        fixture.Update();

        // ⚠ The pairing no walk over `Parent` can recover: a tab is in the strip and its panel is in
        // the panel area, and they are siblings' children rather than parent and child.
        Assert.Same(general.Panel, general.AccessibleRelationTarget(AccessibleRelation.Controls));
        Assert.Same(advanced.Panel, advanced.AccessibleRelationTarget(AccessibleRelation.Controls));

        // And the other way, which is a different statement: the panel's only words are the tab's.
        Assert.Same(general, general.Panel.AccessibleRelationTarget(AccessibleRelation.LabelledBy));
        Assert.Equal("General", general.Panel.AccessibleName);

        Assert.Equal(AccessibleStates.Selected, general.NativeState());
        Assert.Equal(AccessibleStates.None, advanced.NativeState());

        // The `Tabs` control itself is not in the tree, and neither is the box holding the panels.
        Assert.False(tabs.IsInAccessibilityTree);
        Assert.False(tabs.Panels.IsInAccessibilityTree);
        Assert.True(tabs.Strip.IsInAccessibilityTree);

        Assert.Empty(AccessibilitySnapshot.Unnamed(tabs));

        Assert.Equal(
            """
            tablist
              tab "General" [selected]
              tab "Advanced"
            tabpanel "General"
            tabpanel "Advanced"
            """,
            AccessibilitySnapshot.Render(tabs)
        );
    }

    [Fact]
    public void A_select_owns_the_list_that_is_not_its_child_and_points_at_the_chosen_option() {
        using var fixture = new ControlFixture();

        var select = Labelled<Select>(fixture.Document.Root, "Blend mode");
        select.AddOption("opaque", "Opaque");
        select.AddOption("cutout", "Cut-out");
        select.Value = "cutout";
        fixture.Update();

        // ⚠ The list is a child of the document *root* — an overlay inside the field that opens it
        // would be clipped by every scrolling ancestor between the two — so `Owns` is the only thing
        // that says the options belong to this control.
        Assert.Same(select.List, select.AccessibleRelationTarget(AccessibleRelation.Owns));
        Assert.NotSame(select, select.List.Parent);

        // The focus stays on the field while the list is open, so what a screen reader announces has
        // to be named separately. That is what `aria-activedescendant` is.
        Assert.Same(select.Selected, select.AccessibleRelationTarget(AccessibleRelation.ActiveDescendant));

        Assert.Equal("Blend mode", select.AccessibleName);
        Assert.Equal("Cut-out", select.AccessibleValue);

        Assert.Empty(AccessibilitySnapshot.Unnamed(select));

        // The rendered tree puts the owned list under the combo box rather than where the elements
        // are, which is the picture the relation exists to produce.
        Assert.Equal(
            """
            combobox "Blend mode" = "Cut-out" [expandable]
              listbox
                option "Opaque"
                option "Cut-out" [selected]
            """,
            AccessibilitySnapshot.Render(select)
        );
    }

    [Fact]
    public void A_container_is_walked_through_rather_than_announced() {
        using var fixture = new ControlFixture();

        var panel = fixture.Add<Panel>();
        var card = panel.Add<Card>();
        var save = card.Body.Add<Button>();
        save.Label = "Save";
        fixture.Update();

        Assert.False(panel.IsInAccessibilityTree);
        Assert.False(card.IsInAccessibilityTree);

        // ⚠ Three layout elements and one button, and the snapshot is one line at depth nought. A
        // tree that reported the containers would read a form as a stack of nested groups, which is
        // how an accessibility tree comes to be complete and useless.
        Assert.Equal("button \"Save\"", AccessibilitySnapshot.Render(panel));
    }

    [Fact]
    public void A_form_of_seven_controls_has_a_role_and_a_name_on_every_one_of_them() {
        using var fixture = new ControlFixture();

        var form = fixture.Add<Panel>();

        var name = Labelled<TextBox>(form, "Project name");
        name.Value = "Vixen";

        var mode = Labelled<Select>(form, "Blend mode");
        mode.AddOption("opaque", "Opaque");
        mode.Value = "opaque";

        var overwrite = form.Add<CheckBox>();
        overwrite.Label = "Overwrite existing";

        var verbose = form.Add<Switch>();
        verbose.Label = "Verbose output";

        var bold = form.Add<ToggleButton>();
        bold.Label = "Bold";

        var docs = form.Add<Link>();
        docs.Label = "Read the guide";

        var save = form.Add<Button>();
        save.Label = "Save";

        fixture.Update();

        // ⚠ **The assertion that cannot pass vacuously.** Seven interactive controls, every one with
        // a widget role and a non-empty accessible name, and the only accessibility line written by
        // this test is the two `LabelledBy` relations — everything else is the control set saying
        // what it is.
        Assert.Empty(AccessibilitySnapshot.Unnamed(form));

        Assert.Equal(
            """
            textbox "Project name" = "Vixen" [editable]
            combobox "Blend mode" = "Opaque" [expandable]
              listbox
                option "Opaque" [selected]
            checkbox "Overwrite existing"
            switch "Verbose output"
            button "Bold"
            link "Read the guide"
            button "Save"
            """,
            AccessibilitySnapshot.Render(form)
        );
    }
}

/// <summary>Reads the states a control computes for itself, without the ones the element adds.</summary>
/// <remarks>
///     <c>AccessibleState</c> folds in <c>Disabled</c>, <c>Focused</c> and <c>Focusable</c> from the
///     element, which is the point of it and is noise in a test about what a checkbox says. This
///     masks those three off so an assertion can be an equality rather than a bit test.
/// </remarks>
file static class StateProbe {
    public static AccessibleStates NativeState(this UiElement element) =>
        element.AccessibleState
        & ~AccessibleStates.Disabled
        & ~AccessibleStates.Focused
        & ~AccessibleStates.Focusable;
}
