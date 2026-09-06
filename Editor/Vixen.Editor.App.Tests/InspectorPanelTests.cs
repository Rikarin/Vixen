// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Cameras;
using Vixen.Rendering.Ecs;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The inspector panel as a shape on screen, rather than as a model.</summary>
/// <remarks>
///     Everything here failed in a way that no test over <c>InspectorView</c>, <c>ComponentsView</c>
///     or <c>InspectorField</c> could have caught, because each of those was right on its own: an
///     edit that reaches the field only when the field is told, foldouts stacked by whatever
///     <c>flex-direction</c> a part with no rule inherits, and a panel that is as long as its
///     contents because nothing above it says otherwise.
/// </remarks>
public class InspectorPanelTests {
    /// <summary>
    ///     ⚠ <b>Clicking away is how most edits end, and it wrote nothing.</b> The name box only
    ///     committed on Enter, so typing a name and then reaching for the hierarchy — or the
    ///     viewport, or any other field — left the new name on screen and the old one in the
    ///     document. That reads as the rename having worked and the outliner being broken.
    /// </summary>
    [Fact]
    public void A_name_typed_in_the_inspector_is_committed_when_the_field_loses_focus() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.Open("inspector");
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var box = NameBox(editor);

        editor.Document.Focus(box);
        box.Value = "Key Light";
        editor.Settle();

        // Still the old name: a string is not a slider, and a keystroke is not a commit.
        Assert.Equal("Directional Light", editor.Scene.NameOf(Named(editor, "Directional Light")));

        editor.Document.Focus(null);
        editor.Settle();

        Assert.Contains("Key Light", Labels(editor.Hierarchy));
        Assert.DoesNotContain("Directional Light", Labels(editor.Hierarchy));
    }

    /// <summary>⚠ One undo entry for the rename, not none and not one per character.</summary>
    [Fact]
    public void That_commit_is_one_undo_step() {
        using var editor = EditorSession.Start();

        editor.Open("hierarchy");
        editor.Open("inspector");
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var box = NameBox(editor);

        editor.Document.Focus(box);
        box.Value = "Key Light";
        editor.Document.Focus(null);
        editor.Settle();

        editor.Run("edit.undo");
        editor.Settle();

        Assert.Contains("Directional Light", Labels(editor.Hierarchy));
    }

    /// <summary>
    ///     ⚠ <b>A part with no rule of its own lays its children out in a <i>row</i>.</b>
    ///     <c>LayoutStyle.Default</c> is a column, which is what a tree with no stylesheet gets, but
    ///     every styled element is built from the CSS initial instead — so <c>component-list</c>,
    ///     which nothing styled, put three foldouts side by side at a third of the panel each.
    /// </summary>
    [Fact]
    public void The_component_foldouts_are_stacked_down_the_panel() {
        using var editor = EditorSession.Start();

        var sections = ThreeComponents(editor);

        Assert.Equal(3, sections.Count);

        for (var index = 1; index < sections.Count; index++) {
            var above = sections[index - 1];
            var below = sections[index];

            // A pixel of slack, because the layout results are snapped to the device's grid and two
            // stacked boxes can meet a rounding step apart. What this is separating is one foldout
            // under another from three of them across the panel.
            Assert.True(
                below.AbsoluteTop >= above.AbsoluteTop + above.Height - 1f,
                $"'{below.Label}' starts at {below.AbsoluteTop} rather than below '{above.Label}', "
                + $"which ends at {above.AbsoluteTop + above.Height}."
            );

            Assert.Equal(above.AbsoluteLeft, below.AbsoluteLeft, 1);
            Assert.Equal(above.Width, below.Width, 1);
        }
    }

    /// <summary>
    ///     ⚠ <b>The rows and the components scroll together, and the search box does not.</b> Two
    ///     scroll regions in one panel leave half the answer off screen whichever one you move, and
    ///     a filter field that scrolls away is unreachable exactly when the panel is long enough to
    ///     need it.
    /// </summary>
    [Fact]
    public void The_rows_and_the_components_share_one_scroll_region_under_a_fixed_header() {
        using var editor = EditorSession.Start();

        ThreeComponents(editor);

        var inspector = editor.Inspector;
        var components = Components(editor);

        Assert.Same(inspector.Scroll.Content, inspector.Body.Parent);
        Assert.Same(inspector.Scroll.Content, components.Parent);

        // The header is outside it, so nothing that scrolls can take the search box with it.
        Assert.Same(inspector, inspector.Header.Parent);
    }

    /// <summary>
    ///     ⚠ <b>Content longer than the panel scrolls rather than growing the panel.</b> Without a
    ///     floor of zero on the scroll view's height, a flex item's automatic minimum is its content
    ///     — so the region is as tall as everything in it, the bar never appears, and the last
    ///     component is off the bottom of the window with no way to reach it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The region is given a height rather than the window being shrunk.</b> Only three
    ///     component types are registered, so an entity carrying all of them still fits a default
    ///     window — and the docking host does not follow a smaller one, which is its own gap and not
    ///     this one. What has to be true is the relationship: content longer than the region moves
    ///     under it, rather than pushing the panel's bottom off the screen.
    /// </remarks>
    [Fact]
    public void A_region_shorter_than_its_contents_scrolls_rather_than_growing() {
        using var editor = EditorSession.Start();

        ThreeComponents(editor);

        var inspector = editor.Inspector;
        var scroll = inspector.Scroll;
        var header = inspector.Header.AbsoluteTop;

        // `flex-grow` as well as the height, or the region is stretched back to the panel by the
        // rule that makes it fill one.
        scroll.SetStyle("flex-grow", "0");
        scroll.SetStyle("height", "200px");
        editor.Settle();

        Assert.Equal(200f, scroll.Height, 1);

        Assert.True(
            scroll.MaximumTop > 0f,
            $"the content came to {scroll.Content.Height} in a region {scroll.Height} tall, so it "
            + "shrank to fit and there is nothing to scroll."
        );

        scroll.ScrollTop = 10_000f;
        editor.Settle();

        Assert.Equal(scroll.MaximumTop, scroll.ScrollTop, 1);

        // And the search box did not go anywhere with it.
        Assert.Equal(header, inspector.Header.AbsoluteTop, 1);
    }

    /// <summary>Selects an entity carrying three components and returns their foldouts, in order.</summary>
    static IReadOnlyList<Expander> ThreeComponents(EditorSession editor) {
        editor.Open("hierarchy");
        editor.Open("inspector");
        editor.ExpandAll(editor.Hierarchy);
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var light = Named(editor, "Directional Light");

        editor.Scene.World.Add(light, new Camera());
        editor.Scene.World.Add(light, new PrimitiveShape());
        editor.Settle();

        // The view rebuilds from a selection change rather than from the world, so the entity has to
        // be selected again after being given the two components.
        editor.ClickRow(editor.Hierarchy, "Crate");
        editor.Settle();
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        return Components(editor).Sections;
    }

    static ComponentsView Components(EditorSession editor) =>
        Descendants(editor.Panel("inspector")).OfType<ComponentsView>().FirstOrDefault()
        ?? throw editor.Fail("the inspector has no components section");

    static TextBox NameBox(EditorSession editor) {
        var row = editor.Inspector.Rows.FirstOrDefault(candidate => candidate.Field.Member.Name == "Name")
            ?? throw editor.Fail("the inspector is not showing a Name row");

        return Descendants(row).OfType<TextBox>().FirstOrDefault()
            ?? throw editor.Fail("the Name row has no text box in it");
    }

    static Entity Named(EditorSession editor, string name) =>
        editor.Scene.Entities.First(entity => editor.Scene.NameOf(entity) == name);

    static List<string> Labels(TreeView tree) =>
        [.. EditorSession.NodesOf(tree).Select(node => node.Text ?? string.Empty)];

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
