// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The affordances a panel is <i>made</i> of, as against what it is looking at.</summary>
/// <remarks>
///     ⚠ <b>Every one of these was a control that existed, was clickable and did nothing visible</b>
///     — a chevron that hid itself and took the row's alignment with it, a toggle whose state was
///     written to the DOM and never drawn, a label that said the same word in both of a toggle's
///     states, and a view mode forgotten by the next launch. They are the cheapest possible bugs to
///     ship and the most expensive to describe, because "the button does not work" is what they all
///     look like.
/// </remarks>
public class EditorAffordanceTests {
    /// <summary>A leaf keeps the chevron's column, so the names down a tree share one left edge.</summary>
    /// <remarks>
    ///     ⚠ The old rule was <c>tree-row.leaf icon { display: none }</c>, which took the chevron out
    ///     of the flow — so a row shifted sideways the moment it gained or lost a child — and, being
    ///     a tag selector, took every <i>other</i> icon on the row with it, including the outliner's
    ///     own eye and padlock.
    /// </remarks>
    [Fact]
    public void A_row_with_no_children_lines_up_with_one_that_has() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        var parent = Row(editor.Hierarchy, "Ground");
        var leaf = Row(editor.Hierarchy, "Main Camera");

        Assert.True(parent.Node!.HasChildren);
        Assert.False(leaf.Node!.HasChildren);

        // Same depth, so the same indent — and therefore the same label position, which is only true
        // while the chevron keeps its box on both.
        Assert.Equal(parent.Node.Depth, leaf.Node.Depth);
        Assert.Equal(parent.Label.AbsoluteLeft, leaf.Label.AbsoluteLeft, 1);

        // The glyph is what goes, not the element.
        Assert.Null(leaf.Chevron.Geometry);
        Assert.NotNull(parent.Chevron.Geometry);
    }

    /// <summary>Every row carries a glyph saying what it is.</summary>
    [Fact]
    public void Rows_are_given_an_icon_for_what_they_hold() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);

        // ⚠ `Art` rather than `Icon` since doc 36 § D6: an outliner glyph is now whatever the
        // registry says the entity's most characteristic component looks like, which is a piece of
        // art with a paint per path rather than a single `PathBuilder` drawn in the row's colour.
        Assert.NotNull(Row(editor.Hierarchy, "Main Camera").Node!.Art);
        Assert.NotNull(Row(editor.Hierarchy, "Directional Light").Node!.Art);

        // And they differ, which is the whole point of having one.
        Assert.NotSame(
            Row(editor.Hierarchy, "Main Camera").Node!.Art,
            Row(editor.Hierarchy, "Directional Light").Node!.Art
        );
    }

    /// <summary>A double-click in the content browser opens the asset, as a browser's must.</summary>
    [Fact]
    public void Double_clicking_an_asset_row_opens_it() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);
        editor.DoubleClickRow(editor.Assets, "Main.vxscene");

        Assert.Null(Find<TextBox>(editor.Assets));
        Assert.Contains(editor.Panels, panel => panel.Id.StartsWith("asset.", StringComparison.Ordinal));
    }

    /// <summary>And clicking a row that is already the only one selected renames it.</summary>
    /// <remarks>
    ///     ⚠ <b>The gesture has to be the one that is not a double-click, because a browser's
    ///     double-click opens.</b> "Already selected before this press" is what tells them apart with
    ///     no timer: the first click of a double-click lands on a row that was not selected and arms
    ///     nothing. See <c>TreeView.RenameOnSecondClick</c>.
    /// </remarks>
    [Fact]
    public void Clicking_an_already_selected_asset_row_renames_it() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);

        editor.ClickRow(editor.Assets, "Main.vxscene");

        // The first click only selects — a row that was not selected arms nothing.
        Assert.Null(Find<TextBox>(editor.Assets));

        // ⚠ The pause is what makes it a *slow* double click, and in a harness a pause is not time
        // passing — it is the tap run ending. Without this the two clicks are taps one and two of
        // one run and the second opens the asset, which is the behaviour the other test asserts.
        editor.Document.Gestures.EndTapRun();
        editor.ClickRow(editor.Assets, "Main.vxscene");

        Assert.NotNull(Find<TextBox>(editor.Assets));
    }

    /// <summary>The outliner keeps double-click-to-rename, because a row there is a name.</summary>
    [Fact]
    public void Double_clicking_an_outliner_row_still_renames() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.DoubleClickRow(editor.Hierarchy, "Crate");

        Assert.NotNull(Find<TextBox>(editor.Hierarchy));
    }

    /// <summary>The browser's context menu offers the same Create entries the Assets menu does.</summary>
    [Fact]
    public void The_project_context_menu_can_create_things() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);
        editor.RightClickRow(editor.Assets, "Assets");

        var labels = editor.Ui.Get("menu-item").Elements
            .OfType<ButtonBase>()
            .Select(item => item.Label)
            .ToList();

        Assert.Contains("Create", labels);
    }

    /// <summary>The addressables window has a way in that does not require owning a group already.</summary>
    /// <remarks>
    ///     ⚠ The view was built and reachable only by double-clicking a <c>.vxgroup</c> — which a
    ///     project that has never made one does not have. That is what "no addressable UI" meant.
    /// </remarks>
    [Fact]
    public void The_addressables_panel_is_reachable_from_a_menu() {
        using var editor = EditorSession.Start();

        var command = EditorShell.PanelCommand("addressables");

        Assert.True(editor.Shell.Commands.TryGet(command, out _));

        var assets = editor.Shell.Menus.Menus.First(menu => menu.Title.Text == "Assets");

        Assert.Contains(command, Ids(assets));

        editor.Open("addressables");
        editor.Frames(2);

        Assert.NotNull(
            Descendants(editor.Panel("addressables"))
                .FirstOrDefault(element => element.Tag == "group-editor")
        );
    }

    /// <summary>An asset's address is editable and lands in the sidecar.</summary>
    [Fact]
    public void An_assets_address_can_be_typed_into_the_inspector() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);
        editor.ClickRow(editor.Assets, "Main.vxscene");

        editor.Open("inspector");
        editor.Frames(2);

        var row = editor.Inspector.Rows.FirstOrDefault(candidate => candidate.Field.Member.Name == "Address");

        Assert.NotNull(row);

        var box = Find<TextBox>(row!)!;

        Assert.False(box.ReadOnly, "the address is the one row on an asset that is meant to be typed in");

        // Typed and committed the way a person does: the drawer records on submit and on focus loss
        // rather than per keystroke, so assigning the property alone writes nothing.
        editor.Document.Focus(box);
        box.Value = "levels/main";

        editor.Document.Focus(null);
        editor.Frames(2);

        var sidecar = Path.Combine(editor.ProjectRoot, "Assets", "Scenes", "Main.vxscene.meta");

        Assert.True(File.Exists(sidecar));
        Assert.Contains("levels/main", File.ReadAllText(sidecar), StringComparison.Ordinal);
    }

    /// <summary>The gizmo's space toggle says which space it is in, and looks pressed when it is.</summary>
    /// <remarks>
    ///     ⚠ Both halves were missing and each hid the other: the label read "Local Space" in world
    ///     space too, and the <c>:checked</c> state the toolbar writes had no rule drawing it — so
    ///     the only way to find out what a drag would do was to drag.
    /// </remarks>
    [Fact]
    public void The_space_toggle_says_which_space_it_is_in() {
        using var editor = EditorSession.Start();

        editor.Open("scene");
        editor.Frames(2);

        var command = Command(editor, "scene.toggle-space");
        var pane = editor.Viewport!;

        pane.Gizmo.Space = GizmoSpace.World;

        Assert.Equal("World Space", command.CurrentTitle.Text);
        Assert.False(command.IsChecked);

        editor.Run("scene.toggle-space");

        Assert.Equal(GizmoSpace.Local, pane.Gizmo.Space);
        Assert.Equal("Local Space", command.CurrentTitle.Text);
        Assert.True(command.IsChecked);

        // ⚠ The id does not move with the label. It is what the keymap, the palette and the menu
        // model all name, and a command that renamed itself would break every one of them.
        Assert.True(editor.Shell.Commands.TryGet("scene.toggle-space", out _));
    }

    /// <summary>And the pivot toggle does the same.</summary>
    [Fact]
    public void The_pivot_toggle_says_where_the_pivot_is() {
        using var editor = EditorSession.Start();

        editor.Open("scene");
        editor.Frames(2);

        var command = Command(editor, "scene.toggle-pivot");
        var before = command.CurrentTitle.Text;

        editor.Run("scene.toggle-pivot");

        Assert.NotEqual(before, command.CurrentTitle.Text);
    }

    /// <summary>The window's strip no longer carries a second copy of the pane's controls.</summary>
    /// <remarks>
    ///     Two strips over one set of commands is not merely redundant: the window has one bar and a
    ///     split layout has four gizmo modes, so the copy that is not beside the viewport is the one
    ///     that is read wrong.
    /// </remarks>
    [Fact]
    public void The_window_toolbar_does_not_mirror_the_viewports() {
        using var editor = EditorSession.Start();

        editor.Open("scene");
        editor.Frames(2);

        var ids = editor.Shell.Toolbar.Items.Where(id => id is not null).ToList();

        Assert.DoesNotContain("scene.toggle-space", ids);
        Assert.DoesNotContain("scene.toggle-pivot", ids);
        Assert.DoesNotContain("scene.toggle-snap", ids);

        // The transport and the two verbs that write to disk stay: they are the window's.
        Assert.Contains("file.save", ids);
    }

    /// <summary>Clicking the strip over the scene does not clear what is selected under it.</summary>
    /// <remarks>
    ///     ⚠ <b>The chrome is a child of the viewport, so its events bubble through it.</b> The pane
    ///     listens with <c>handledEventsToo</c> — it has to, because the control marks every pointer
    ///     event handled — so a press on a toolbar button read as a press on the scene: it began a
    ///     rubber band over the toolbar and the release picked nothing, which cleared the selection.
    ///     The button worked and deselected the object it was about to act on.
    /// </remarks>
    [Fact]
    public void Pressing_a_button_over_the_viewport_keeps_the_selection() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");

        editor.Open("scene");
        editor.Frames(2);

        Assert.Single(editor.Scene.Selection);

        var pane = editor.Viewport!;
        var button = Descendants(pane.Control.Overlay)
            .OfType<ButtonBase>()
            .First(candidate => candidate.Label == "Rotate");

        var bounds = button.Bounds;

        editor.Ui.At(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f)).Click();
        editor.Frames(2);

        Assert.Equal(GizmoMode.Rotate, pane.Gizmo.Mode);
        Assert.Single(editor.Scene.Selection);
    }

    /// <summary>An undo puts the old value back in the panel that shows it.</summary>
    /// <remarks>
    ///     ⚠ The stack and the viewport both noticed; the inspector did not, because a row is read
    ///     from its target when it is built and after an edit it made itself. Nothing told it that
    ///     somebody else had written — so Ctrl+Z left the typed number on screen and looked like an
    ///     undo that had not happened.
    /// </remarks>
    [Fact]
    public void Undo_is_visible_in_the_inspector() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");

        var name = editor.Inspector.Rows.First(row => row.Field.Member.Name == "Name");
        var box = Find<TextBox>(name)!;

        // Typed and committed the way a person does — the drawer records on submit and on focus
        // loss rather than per keystroke, so assigning the property writes nothing to the stack.
        box.Value = "Renamed Crate";
        editor.Ui.Get("textbox").Where(element => ReferenceEquals(element, box), "the name field").Focus();
        editor.Document.Focus(null);
        editor.Frames(2);

        Assert.Equal("Renamed Crate", box.Value);
        Assert.Equal("Renamed Crate", editor.Scene.NameOf(editor.Scene.Selection[0]));

        // ⚠ One entry, not two. The property setter used to record a rename of its own from inside
        // the inspector's own command — see `SceneEntity.Name`.
        Assert.Single(editor.Scene.Stack.History);

        editor.Run("edit.undo");
        editor.Frames(2);

        Assert.Equal("Crate", editor.Scene.NameOf(editor.Scene.Selection[0]));

        // Re-read rather than held: `Reload` writes into the rows that exist now, and an inspector
        // that had rebuilt for any other reason would leave the old element behind saying anything.
        Assert.Equal(
            "Crate",
            Find<TextBox>(editor.Inspector.Rows.First(row => row.Field.Member.Name == "Name"))!.Value
        );
    }

    /// <summary>An undo puts a component's number back in the panel that shows it.</summary>
    /// <remarks>
    ///     ⚠ <b>The component foldouts are not the inspector's rows, and they are where a numeric
    ///     edit usually lands.</b> <c>SetComponentCommand</c> announces itself only when the *set* of
    ///     components changed — a value edit deliberately says nothing, so that a slider drag does
    ///     not rebuild the panel under the pointer — so the undo changed the world and the viewport
    ///     and left the old number on screen. Reloading the boxes is the other half of
    ///     <see cref="Undo_is_visible_in_the_inspector" />.
    /// </remarks>
    [Fact]
    public void Undo_is_visible_in_a_components_numbers() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Directional Light");

        editor.Open("inspector");
        editor.Frames(2);

        var components = Descendants(editor.Panel("inspector")).OfType<ComponentsView>().Single();

        // ⚠ By member rather than "the first numeric box in the section". The colour picker above it
        // is made of numeric fields of its own, and setting one of those edits the picker rather
        // than the component — a test that passed by moving a control nothing is recording.
        var intensity = Intensity(components);
        var before = intensity.Number;

        // The drawer writes on every change and the view commits one `SetComponentCommand` for it,
        // which is the path a scrub takes.
        intensity.Number = before + 4d;
        editor.Frames(2);

        Assert.Equal(before + 4d, Intensity(components).Number);
        Assert.Equal(before + 4f, editor.Scene.World.Get<Light>(editor.Scene.Selection[0]).Intensity);

        editor.Run("edit.undo");
        editor.Frames(2);

        Assert.Equal(before, editor.Scene.World.Get<Light>(editor.Scene.Selection[0]).Intensity);

        // Re-read rather than held, so the assertion is about what is on screen now.
        Assert.Equal(before, Intensity(components).Number);
    }

    /// <summary>A light's colour is a picker rather than a line of grey text.</summary>
    /// <remarks>
    ///     ⚠ <c>Light.Colour</c> is a <c>Color3</c>, and the registry only had a drawer for
    ///     <c>Color4</c> — so the one property people open a light to change fell through to the
    ///     read-only last resort.
    /// </remarks>
    [Fact]
    public void A_lights_colour_is_editable() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Directional Light");

        editor.Open("inspector");
        editor.Frames(2);

        var components = Descendants(editor.Panel("inspector")).OfType<ComponentsView>().Single();
        var light = components.Sections.Single(section => section.Label == "Light");

        // ⚠ A `ColorInput` and not a `ColorPicker`: the row is a swatch, and the picker is what
        // clicking it opens. The whole apparatus in the row is what made a material with four tints
        // taller than the panel.
        var input = Find<ColorInput>(light);

        Assert.NotNull(input);
        Assert.False(input.IsOpen, "the picker is open before anything was clicked");

        editor.Click(input);

        Assert.True(input.IsOpen, "clicking the swatch did not open the picker");

        // ⚠ In a popover on the document root rather than under the row. A panel that dropped out of
        // the field would be clipped by every scrolling ancestor between the two, which for a
        // property row in an inspector in a docked panel is three of them.
        Assert.Same(editor.Document.Root, input.Popup.Parent);

        editor.Click(input);
        Assert.False(input.IsOpen, "clicking the swatch again did not close the picker");
    }

    /// <summary>The browser's view mode is still what it was after a restart.</summary>
    [Fact]
    public void The_grid_toggle_survives_a_restart() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        var toggle = Descendants(editor.Panel("project")).OfType<ToggleButton>().First(button => button.Label == "Grid");

        // ⚠ Turned *off* rather than on, because the panel now opens on the grid — and a test that
        // pressed the toggle and asserted it came back pressed would pass just as well against a
        // preference that was never written at all.
        Assert.True(toggle.IsChecked, "the browser should open on the grid");

        toggle.Activate();
        editor.Frames(2);

        editor.Restart();
        editor.Open("project");
        editor.Frames(2);

        var grid = Descendants(editor.Panel("project")).OfType<ToggleButton>().First(button => button.Label == "Grid");

        Assert.False(grid.IsChecked, "the browser came back as a grid after being left as a tree");
    }

    /// <summary>The inspector's lock is a padlock whose shackle says which state it is in.</summary>
    /// <remarks>
    ///     ⚠ The word "Lock" was four times the width of the button beside it and read as a verb —
    ///     so the control said what pressing it does rather than what state it is in, which is the
    ///     one thing a toggle must not do. The glyph is still labelled for a screen reader.
    /// </remarks>
    [Fact]
    public void The_inspector_lock_is_an_icon_that_changes_with_its_state() {
        using var editor = EditorSession.Start();

        var inspector = editor.Inspector;
        var open = inspector.Lock.LeadingIcon.Geometry;

        Assert.NotNull(open);
        Assert.Equal("Lock", inspector.Lock.Label);

        inspector.IsLocked = true;
        editor.Frames(1);

        Assert.NotNull(inspector.Lock.LeadingIcon.Geometry);
        Assert.NotSame(open, inspector.Lock.LeadingIcon.Geometry);
    }

    /// <summary>A component foldout can be dragged above another, and the order outlives the panel.</summary>
    /// <remarks>
    ///     ⚠ The order is a view preference rather than a fact about the entity — an archetype is a
    ///     set and has no notion of a component being third — so it is held by the application and
    ///     written to the preferences file. See <c>ComponentsView.Order</c>.
    /// </remarks>
    [Fact]
    public void Component_foldouts_can_be_rearranged_and_stay_that_way() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Open("inspector");
        editor.Frames(2);

        var components = Descendants(editor.Panel("inspector")).OfType<ComponentsView>().Single();

        // The crate has a mesh shape; giving it a light as well is two sections to swap.
        Lights.Attach(editor.Scene.World, editor.Scene.Selection[0], LightKind.Point);

        components.Show(editor.Scene.Selection[0]);
        editor.Frames(2);

        var before = components.Sections.Select(section => section.Label ?? string.Empty).ToList();

        Assert.True(before.Count >= 2, "the entity has fewer than two components to rearrange");

        // Dragged by the header, which is the grab handle — and through the real gesture, because
        // that is the path that reports the new order to whoever has to remember it.
        var handle = components.Sections[1].Header.Bounds;
        var target = components.Sections[0].Header.Bounds;

        editor.Ui
            .At(handle.X + (handle.Width * 0.5f), handle.Y + (handle.Height * 0.5f))
            .DragTo(target.X + (target.Width * 0.5f), target.Y + 2f);

        editor.Frames(2);

        Assert.Equal(before[^1], components.Sections[0].Label);

        // ⚠ The arrangement is written down under the *alias*, not under what the foldout says. The
        // labels are written out — "Primitive Shape" — and the preferences file has always held
        // "PrimitiveShape"; a drop that recorded the label would silently reset every saved
        // arrangement the first time somebody upgraded. See `IComponentBridge.DisplayName`.
        Assert.Contains("PrimitiveShape", components.Order);
        Assert.DoesNotContain("Primitive Shape", components.Order);

        // Reopening the panel runs its factory again, which is where the arrangement used to go.
        editor.Close("inspector");
        editor.Open("inspector");
        editor.Frames(2);

        var reopened = Descendants(editor.Panel("inspector")).OfType<ComponentsView>().Single();

        reopened.Show(editor.Scene.Selection[0]);
        editor.Frames(2);

        Assert.Equal(before[^1], reopened.Sections[0].Label);
    }

    /// <summary>
    ///     ⚠ <b>A drag with no indicator is a drag you have to do twice.</b> The header faded and
    ///     nothing else moved, so the only way to find out whether Light lands above or below Mesh
    ///     Shape was to drop it and look. The line has to track the pointer and it has to agree with
    ///     where the drop actually puts it — a line that says "here" over a drop that lands one place
    ///     further down is the panel lying about what a release will do.
    /// </summary>
    [Fact]
    public void A_blue_line_shows_where_a_dragged_component_will_land() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Open("inspector");
        editor.Frames(2);

        var components = Descendants(editor.Panel("inspector")).OfType<ComponentsView>().Single();

        Lights.Attach(editor.Scene.World, editor.Scene.Selection[0], LightKind.Point);

        components.Show(editor.Scene.Selection[0]);
        editor.Frames(2);

        Assert.True(components.Sections.Count >= 2, "the entity has fewer than two components to rearrange");
        Assert.True(components.DropIndicator.HasClass("hidden"), "the line is showing before anything was dragged");

        var handle = components.Sections[1].Header.Bounds;
        var first = components.Sections[0].Bounds;

        // Held down over the top half of the first section, which is where it would land above it —
        // and not released, because the whole point is what the panel says *while* the drag is live.
        editor.Ui
            .At(handle.X + (handle.Width * 0.5f), handle.Y + (handle.Height * 0.5f))
            .Press();

        editor.Ui.MovePointer(first.X + (first.Width * 0.5f), first.Y + 2f);
        editor.Frames(2);

        Assert.False(components.DropIndicator.HasClass("hidden"), "no line while a foldout is being dragged");

        // On the first section's top edge, which is the gap it would go into.
        Assert.Equal(first.Y, components.DropIndicator.Bounds.Y, 1);

        // And past the bottom of the last one, the line goes to the end of the list rather than
        // stopping one short of it — which is the off-by-one a gap-based landing exists to avoid.
        var last = components.Sections[^1].Bounds;

        editor.Ui.MovePointer(last.X + (last.Width * 0.5f), last.Y + last.Height + 4f);
        editor.Frames(2);

        Assert.Equal(last.Y + last.Height, components.DropIndicator.Bounds.Y, 1);

        editor.Ui.ReleasePointer();
        editor.Frames(2);

        Assert.True(components.DropIndicator.HasClass("hidden"), "the line is still showing after the drop");
    }

    /// <summary>Every command id a menu names, submenus included.</summary>
    static IEnumerable<string> Ids(MenuGroup group) {
        foreach (var entry in group.Entries) {
            switch (entry) {
                case MenuCommand(var id):
                    yield return id;
                    break;

                case MenuSubmenu(var nested):
                    foreach (var id in Ids(nested)) {
                        yield return id;
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>The intensity box of the Light foldout, found by member rather than by position.</summary>
    static NumericInput Intensity(ComponentsView components) {
        var light = components.Sections.Single(section => section.Label == "Light");

        var row = Descendants(light)
            .OfType<InspectorRow>()
            .Single(candidate => candidate.Field.Member.Name == "Intensity");

        return Descendants(row).OfType<NumericInput>().First();
    }

    static EditorCommand Command(EditorSession editor, string id) {
        Assert.True(editor.Shell.Commands.TryGet(id, out var command));
        return command!;
    }

    static TreeRow Row(TreeView tree, string text) =>
        tree.Rows.First(row => !row.HasClass("parked") && row.Node?.Text == text);

    static T? Find<T>(UiElement element) where T : UiElement =>
        Descendants(element).OfType<T>().FirstOrDefault();

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
