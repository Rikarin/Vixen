// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
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

        Assert.NotNull(Row(editor.Hierarchy, "Main Camera").Node!.Icon);
        Assert.NotNull(Row(editor.Hierarchy, "Directional Light").Node!.Icon);

        // And they differ, which is the whole point of having one.
        Assert.NotSame(
            Row(editor.Hierarchy, "Main Camera").Node!.Icon,
            Row(editor.Hierarchy, "Directional Light").Node!.Icon
        );
    }

    /// <summary>A double-click in the content browser edits the name, as it does in the outliner.</summary>
    [Fact]
    public void Double_clicking_an_asset_row_opens_the_rename_editor() {
        using var editor = EditorSession.Start();

        editor.Open("project");
        editor.ExpandAll(editor.Assets);
        editor.DoubleClickRow(editor.Assets, "Main.vxscene");

        Assert.NotNull(Find<TextBox>(editor.Assets));
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

        Assert.NotNull(Find<ColorPicker>(light));
    }

    /// <summary>The browser's view mode is still what it was after a restart.</summary>
    [Fact]
    public void The_grid_toggle_survives_a_restart() {
        using var editor = EditorSession.Start();

        editor.Open("project");

        Descendants(editor.Panel("project")).OfType<ButtonBase>().First(button => button.Label == "Grid").Activate();
        editor.Frames(2);

        editor.Restart();
        editor.Open("project");
        editor.Frames(2);

        var grid = Descendants(editor.Panel("project")).OfType<ToggleButton>().First(button => button.Label == "Grid");

        Assert.True(grid.IsChecked, "the browser came back as a tree after being left as a grid");
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

        // Reopening the panel runs its factory again, which is where the arrangement used to go.
        editor.Close("inspector");
        editor.Open("inspector");
        editor.Frames(2);

        var reopened = Descendants(editor.Panel("inspector")).OfType<ComponentsView>().Single();

        reopened.Show(editor.Scene.Selection[0]);
        editor.Frames(2);

        Assert.Equal(before[^1], reopened.Sections[0].Label);
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
