// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
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
              listbox "Blend mode"
                option "Opaque"
                option "Cut-out" [selected]
            """,
            AccessibilitySnapshot.Render(select)
        );
    }

    [Fact]
    public void A_combo_box_puts_the_role_on_the_field_because_that_is_where_ARIA_puts_it() {
        using var fixture = new ControlFixture();

        var caption = fixture.Add<TextBlock>();
        caption.Text = "Shader";

        var combo = fixture.Add<ComboBox>();
        combo.Editor.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);
        combo.AddOption("lit", "Lit");
        combo.Value = "Lit";
        fixture.Update();

        // ⚠ **The outer element is nothing, and that is the arrangement.** ARIA 1.2's editable combo
        // box is a *text input* carrying `role="combobox"`: the input takes the focus, the input is
        // what `aria-expanded` is read from, and a role on the box drawn round the input and its
        // button would stand for neither of them. `Tabs` is the same decision one control over.
        Assert.False(combo.IsInAccessibilityTree);
        Assert.Equal(AccessibleRole.ComboBox, combo.Editor.Role);

        // Still a text field, which is the half a role assignment would have thrown away.
        Assert.True((combo.Editor.AccessibleState & AccessibleStates.Editable) != 0);
        Assert.Equal("Lit", combo.Editor.AccessibleValue);

        // Expandable always, expanded only when it is. See `Select`.
        Assert.True((combo.Editor.AccessibleState & AccessibleStates.Expandable) != 0);
        Assert.False((combo.Editor.AccessibleState & AccessibleStates.Expanded) != 0);

        Assert.Same(combo.List, combo.Editor.AccessibleRelationTarget(AccessibleRelation.Owns));

        Assert.Empty(AccessibilitySnapshot.Unnamed(fixture.Document.Root));

        Assert.Equal(
            """
            combobox "Shader" = "Lit" [expandable editable]
              listbox "Shader"
                option "Lit"
            button "Show suggestions"
            """,
            AccessibilitySnapshot.Render(combo)
        );
    }

    [Fact]
    public void A_tooltip_describes_what_it_is_attached_to_rather_than_only_appearing_over_it() {
        using var fixture = new ControlFixture();

        var save = fixture.Add<Button>();
        save.Label = "Save";

        var tip = fixture.Add<Tooltip>();
        tip.Label = "Writes the scene to disk";
        tip.Attach(save);

        fixture.Update();

        // ⚠ **A hover is a gesture a screen-reader user does not make.** Before this the sentence
        // existed, was on screen for anybody with a pointer, and was unreachable by anybody without
        // one. `AccessibleDescription` had a working relation path and no control in either
        // assembly fed it, which is this repository's commonest defect: a finished consumer nothing
        // calls.
        Assert.Equal("Writes the scene to disk", save.AccessibleDescription);
        Assert.Equal("Save", save.AccessibleName);

        // And it is read on demand, so the description is right before the tooltip has ever opened
        // and after its words change.
        tip.Label = "Saves everything";
        Assert.Equal("Saves everything", save.AccessibleDescription);
    }

    [Fact]
    public void An_alert_is_named_by_its_heading_and_described_by_its_sentence() {
        using var fixture = new ControlFixture();

        var alert = fixture.Add<Alert>();
        alert.Title = "Import failed";
        alert.Message = "The file is not a mesh.";
        fixture.Update();

        Assert.Equal(AccessibleRole.Alert, alert.Role);
        Assert.Equal("Import failed", alert.AccessibleName);
        Assert.Equal("The file is not a mesh.", alert.AccessibleDescription);

        // ⚠ The two are different elements and neither is the alert. Folding the message into the
        // name would make a screen reader read the whole thing as the alert's title, and building
        // both from the same element would say the sentence twice.
        Assert.Empty(AccessibilitySnapshot.Unnamed(alert));
        Assert.Equal("alert \"Import failed\"", AccessibilitySnapshot.Render(alert));
    }

    [Fact]
    public void An_expander_header_says_what_it_opens_and_whether_it_is_open() {
        using var fixture = new ControlFixture();

        var section = fixture.Add<Expander>();
        section.Label = "Transform";
        fixture.Update();

        Assert.Equal(AccessibleRole.Button, section.Header.Role);
        Assert.Equal("Transform", section.Header.AccessibleName);
        Assert.Same(section.Content, section.Header.AccessibleRelationTarget(AccessibleRelation.Controls));

        Assert.True((section.Header.AccessibleState & AccessibleStates.Expandable) != 0);
        Assert.False((section.Header.AccessibleState & AccessibleStates.Expanded) != 0);

        section.IsExpanded = true;
        fixture.Update();

        // ⚠ Nothing wrote a flag. The state is read from `ElementState.Checked`, which the expander
        // was already setting on its header for the cascade, so there is no second copy of "is it
        // open" and no way for the two to disagree.
        Assert.True((section.Header.AccessibleState & AccessibleStates.Expanded) != 0);

        // The expander itself is a box round two things and is not in the tree.
        Assert.False(section.IsInAccessibilityTree);
    }

    [Fact]
    public void A_scroll_bar_says_which_way_it_runs_and_how_far_down_it_is() {
        using var fixture = new ControlFixture();

        var view = fixture.Add<ScrollView>();
        view.VerticalBar.ContentSize = 4f;
        view.VerticalBar.ViewportSize = 1f;
        view.VerticalBar.Value = 1.5f;
        fixture.Update();

        Assert.Equal(AccessibleRole.ScrollBar, view.VerticalBar.Role);

        // ⚠ The one name in the set read from the catalogue on every get rather than assigned in
        // `OnCreated` — a scroll bar has no words on screen and no caption to be `LabelledBy`, so a
        // literal here would be an English announcement nobody can see to report.
        Assert.Equal(ControlStrings.ScrollBarVertical.Text, view.VerticalBar.AccessibleName);
        Assert.Equal(ControlStrings.ScrollBarHorizontal.Text, view.HorizontalBar.AccessibleName);

        Assert.Equal("0.5", view.VerticalBar.AccessibleValue);

        // The scroll view itself is a box: content and two bars.
        Assert.False(view.IsInAccessibilityTree);
    }

    [Fact]
    public void A_slider_carries_a_number_and_takes_its_name_from_the_words_beside_it() {
        using var fixture = new ControlFixture();

        var slider = Labelled<Slider>(fixture.Document.Root, "Intensity");
        slider.Maximum = 4f;
        slider.Value = 1f;
        fixture.Update();

        Assert.Equal(AccessibleRole.Slider, slider.Role);
        Assert.Equal("Intensity", slider.AccessibleName);
        Assert.Equal("1", slider.AccessibleValue);

        Assert.Empty(AccessibilitySnapshot.Unnamed(slider));
    }

    [Fact]
    public void An_indeterminate_progress_bar_is_busy_rather_than_nought_per_cent() {
        using var fixture = new ControlFixture();

        var bar = fixture.Add<ProgressBar>();
        bar.Value = 0.25f;
        fixture.Update();

        Assert.Equal(AccessibleRole.ProgressBar, bar.Role);
        Assert.Equal("0.25", bar.AccessibleValue);

        bar.IsIndeterminate = true;
        fixture.Update();

        // ⚠ ARIA omits `aria-valuenow` for a job whose length is unknown, and this is why: a screen
        // reader reading "nought per cent" for a job that is running is the failure the omission
        // exists to prevent.
        Assert.Null(bar.AccessibleValue);
        Assert.Equal(AccessibleStates.Busy, bar.NativeState());
    }

    [Fact]
    public void A_row_names_the_editor_that_was_put_in_it() {
        using var fixture = new ControlFixture();

        var list = fixture.Add<KeyValueList>();
        var row = list.AddRow("Cast shadows");
        var box = row.Content<CheckBox>();

        fixture.Update();

        // ⚠ A `CheckBox` put in a row has no words of its own — its label was never set, because
        // the row's key *is* the label. One relation, added where the editor is created, is what
        // stops an inspector being a column of unnamed fields beside a column of text.
        Assert.Equal("Cast shadows", box.AccessibleName);
        Assert.Empty(AccessibilitySnapshot.Unnamed(list));
    }

    /// <summary>Every control in the assembly that draws itself, held to the one rule that cannot pass vacuously.</summary>
    /// <remarks>
    ///     ⚠ <b>The gate that keeps the population honest as it grows.</b> A per-control test
    ///     asserts what its author thought to look at; this asserts that nothing anywhere in the
    ///     set is a widget with no name or a tab stop with no role. The two controls that
    ///     deliberately report no name — a <c>TextField</c> and its subclasses, and a
    ///     <c>Slider</c> — are here <i>with</i> the caption they are supposed to be given, because
    ///     an unlabelled one failing is the behaviour rather than a gap.
    /// </remarks>
    [Fact]
    public void Every_control_in_one_window_has_a_role_and_a_name() {
        using var fixture = new ControlFixture();

        var root = fixture.Document.Root;

        Labelled<TextBox>(root, "Project name");
        Labelled<TextArea>(root, "Notes");
        Labelled<NumericInput>(root, "Samples");
        Labelled<SearchBox>(root, "Filter");
        Labelled<Slider>(root, "Intensity");
        Labelled<RangeSlider>(root, "Exposure range");

        var select = Labelled<Select>(root, "Blend mode");
        select.AddOption("opaque", "Opaque");

        var multi = Labelled<MultiSelect>(root, "Layers");
        multi.AddOption("water", "Water");

        var combo = root.Add<ComboBox>();
        combo.Editor.AddAccessibleRelation(AccessibleRelation.LabelledBy, Caption(root, "Shader"));
        combo.AddOption("lit", "Lit");

        root.Add<Button>().Label = "Save";
        root.Add<IconButton>().Label = "Close";
        root.Add<ToggleButton>().Label = "Bold";
        root.Add<CheckBox>().Label = "Overwrite";
        root.Add<Switch>().Label = "Verbose";
        root.Add<Link>().Label = "Read the guide";

        var radios = root.Add<RadioGroup>();
        radios.AddOption("low", "Low");
        radios.AddOption("high", "High");

        var expander = root.Add<Expander>();
        expander.Label = "Transform";

        var accordion = root.Add<Accordion>();
        accordion.AddSection("Lighting");

        var tabs = root.Add<Tabs>();
        tabs.AddTab("General");

        var crumbs = root.Add<Breadcrumb>();
        crumbs.AddStep("Assets");
        crumbs.AddStep("Meshes");

        var pages = root.Add<Pagination>();
        pages.PageCount = 4;

        var bar = root.Add<MenuBar>();
        var file = bar.AddMenu("File");
        file.AddItem("Save");
        file.AddSubmenu("Open recent");

        var radial = root.Add<RadialMenu>();
        radial.AddItem("Move");

        var alert = root.Add<Alert>();
        alert.Title = "Import failed";
        alert.Message = "The file is not a mesh.";

        var empty = root.Add<EmptyState>();
        empty.Title = "Nothing here";
        empty.Description = "Add a mesh to begin.";

        var toast = root.Add<ToastHost>().Show("Saved");

        var dialog = root.Add<Dialog>();
        dialog.Title = "Delete asset";

        var drawer = root.Add<Drawer>();
        drawer.AccessibleName = "Filters";

        var rows = root.Add<KeyValueList>();
        rows.AddRow("Cast shadows").Content<CheckBox>();

        var tip = root.Add<Tooltip>();
        tip.Label = "Writes the scene to disk";
        tip.Attach(root.Children[0]);

        var image = root.Add<Image>();
        image.Description = "A stone wall";

        var avatar = root.Add<Avatar>();
        avatar.Name = "Ada Lovelace";

        root.Add<ScrollView>();
        root.Add<ProgressBar>();
        root.Add<Spinner>();
        root.Add<Separator>();
        root.Add<Badge>().Text = "3";
        root.Add<Panel>();
        root.Add<Card>();
        root.Add<Skeleton>();
        root.Add<TextBlock>().Text = "Some words";

        fixture.Update();

        Assert.Empty(AccessibilitySnapshot.Unnamed(root));

        // ⚠ And the window is not vacuous: it has to actually contain widgets for the line above to
        // mean anything, and a control that stopped reporting a role would quietly shrink this.
        Assert.True(Widgets(root) >= 24, $"only {Widgets(root)} widget-role elements in the window");

        _ = toast;
    }

    static int Widgets(UiElement root) {
        var count = root.IsInAccessibilityTree ? 1 : 0;

        foreach (var child in root.Children) {
            count += Widgets(child);
        }

        return count;
    }

    static TextBlock Caption(UiElement parent, string text) {
        var caption = parent.Add<TextBlock>();
        caption.Text = text;

        return caption;
    }


    /// <summary>Every control the assembly can build, held to the rule a per-control test cannot state.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A test over the <i>type list</i> rather than over a window, because a window is
    ///         a list somebody has to remember to add to.</b> Doc 46 § A2's population is one
    ///         virtual per control across two assemblies, and the failure it invites is the control
    ///         nobody thought about — added next month, focusable because <c>Control</c> is
    ///         focusable by default, and silent to a screen reader because nobody wrote four lines.
    ///         This finds it by construction: reflection over the assembly, every public control
    ///         with a parameterless constructor, one rule.
    ///     </para>
    ///     <para>
    ///         <b>The rule is <c>AccessibilitySnapshot.Unnamed</c>'s first clause</b> — a control the
    ///         keyboard can reach must be in the accessibility tree, because a tab stop that is not
    ///         is a place a screen-reader user lands on silence. The naming half is deliberately not
    ///         asserted here: a bare control has no caption and several of them report <c>null</c>
    ///         on purpose. The reference-window tests are where names are held down.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_control_the_keyboard_can_reach_is_in_the_accessibility_tree() {
        using var fixture = new ControlFixture();

        var silent = new List<string>();
        var built = 0;

        // ⚠ Through `Make<T>` rather than `UiElement.Add<T>` directly: `Add`'s last parameter is a
        // `ReadOnlySpan<string>`, which reflection cannot pass at all. There is no `Add(Type)`,
        // deliberately — every other caller knows what it is adding.
        var make = typeof(AccessibilityTreeTests).GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var type in typeof(Button).Assembly.GetTypes()) {
            if (!type.IsPublic || type.IsAbstract || !typeof(Control).IsAssignableFrom(type)) {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null) {
                continue;
            }

            var control = (Control) make.MakeGenericMethod(type).Invoke(null, [fixture.Document.Root])!;
            fixture.Update();
            built++;

            if (control.Focusable && control.Role == AccessibleRole.None) {
                silent.Add($"{type.Name} is a tab stop and is not in the accessibility tree");
            }
        }

        // ⚠ First: an assembly whose reflection found nothing satisfies the assertion below
        // perfectly, and a filter that quietly stopped matching is exactly how that happens.
        Assert.True(built >= 40, $"only {built} controls were built");
        Assert.Empty(silent);
    }

    static Control Make<T>(UiElement parent) where T : Control, new() => parent.Add<T>();

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
              listbox "Blend mode"
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
