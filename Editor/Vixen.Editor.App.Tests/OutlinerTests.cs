// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The outliner as the panel people live in rather than as a list of names.</summary>
/// <remarks>
///     ⚠ <b>Doc 20's B1 is a list of things the hierarchy owes, and the two here are the ones that
///     were invisible.</b> Selecting an entity anywhere but the tree left the tree showing whatever
///     had been clicked last, because the rows are rebuilt from the scene and the highlight is the
///     tree's own state; and a filter typed into a panel with no filter box is a feature nobody
///     could reach.
/// </remarks>
public class OutlinerTests {
    [Fact]
    public void Selecting_an_entity_anywhere_highlights_its_row() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        var crate = fixture.Scene.Entities.First(entity => fixture.Scene.NameOf(entity) == "Crate");

        // Not through the tree: this is what a viewport click, a command or an undo does.
        fixture.Scene.Selection.Set([crate]);
        fixture.Frames(2);

        var selected = Assert.Single(fixture.Hierarchy.Selection);
        Assert.Equal("Crate", selected.Text);
    }

    [Fact]
    public void Restoring_a_multiple_selection_does_not_collapse_it_to_one() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        var scene = fixture.Scene;
        var picked = scene.Entities.Where(entity => scene.NameOf(entity) is "Crate" or "Barrel").ToList();

        Assert.Equal(2, picked.Count);

        scene.Selection.Set(picked);
        fixture.Frames(2);

        // ⚠ `Select` raises `SelectionChanged`, and the handler writes the tree's selection back into
        // the document — so restoring three rows unguarded sets the document's selection to the
        // first one and the other two vanish on the next frame.
        Assert.Equal(2, fixture.Hierarchy.Selection.Count);
        Assert.Equal(2, scene.Selection.Count);
    }

    [Fact]
    public void The_filter_keeps_a_matching_row_and_the_parents_it_hangs_from() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        Filter(fixture, "barrel");
        fixture.Frames(2);

        var names = Rows(fixture.Hierarchy);

        Assert.Contains("Barrel", names);

        // ⚠ Its ancestors survive with it. A filter that dropped a non-matching parent would take
        // the matching child with it, which is the one row the user was looking for.
        Assert.Contains("Ground", names);
        Assert.Contains("Scene Root", names);

        Assert.DoesNotContain("Main Camera", names);
    }

    [Fact]
    public void Clearing_the_filter_brings_everything_back() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        Filter(fixture, "barrel");
        fixture.Frames(2);

        Filter(fixture, "   ");
        fixture.Frames(2);

        Assert.Contains("Main Camera", Rows(fixture.Hierarchy));
    }

    [Fact]
    public void A_drag_onto_another_row_reparents_the_entity_undoably() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        var scene = fixture.Scene;
        var world = scene.World;

        var camera = scene.Entities.First(entity => scene.NameOf(entity) == "Main Camera");
        var ground = scene.Entities.First(entity => scene.NameOf(entity) == "Ground");

        Assert.NotEqual(ground, Hierarchy.ParentOf(world, camera));

        // What `TreeView` raises after a drag has landed: the node has already moved in the tree and
        // the document has not heard about it yet.
        Move(fixture, "Main Camera", "Ground");
        fixture.Frames(2);

        Assert.Equal(ground, Hierarchy.ParentOf(world, camera));

        Assert.True(fixture.Shell.Commands.Execute("edit.undo"));
        fixture.Frames(2);

        Assert.NotEqual(ground, Hierarchy.ParentOf(world, camera));
    }

    [Fact]
    public void A_drag_that_would_make_a_cycle_leaves_the_tree_showing_the_truth() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        var scene = fixture.Scene;
        var world = scene.World;
        var ground = scene.Entities.First(entity => scene.NameOf(entity) == "Ground");
        var before = Hierarchy.ParentOf(world, ground);

        // Ground onto its own child. The tree moves the node, the document refuses, and the rebuild
        // is what puts the row back where it belongs — otherwise the outliner shows a hierarchy the
        // scene does not have.
        Move(fixture, "Ground", "Crate");
        fixture.Frames(3);

        Assert.Equal(before, Hierarchy.ParentOf(world, ground));
        Assert.Contains("Ground", Rows(fixture.Hierarchy));
    }

    [Fact]
    public void Dragging_one_of_several_selected_rows_moves_all_of_them() {
        using var fixture = EditorSession.Start();

        fixture.Open("hierarchy");

        var scene = fixture.Scene;
        var world = scene.World;

        var crate = scene.Entities.First(entity => scene.NameOf(entity) == "Crate");
        var barrel = scene.Entities.First(entity => scene.NameOf(entity) == "Barrel");
        var root = scene.Entities.First(entity => scene.NameOf(entity) == "Scene Root");

        scene.Selection.Set([crate, barrel]);
        fixture.Frames(2);

        Move(fixture, "Crate", "Scene Root");
        fixture.Frames(2);

        // The rule the context menu already follows: dragging one of five selected rows and having
        // four stay behind is the behaviour nobody means.
        Assert.Equal(root, Hierarchy.ParentOf(world, crate));
        Assert.Equal(root, Hierarchy.ParentOf(world, barrel));
    }

    static void Filter(EditorSession fixture, string text) {
        var box = Find<SearchBox>(fixture.Document.Root)
            ?? throw new InvalidOperationException("the outliner has no filter box");

        box.Value = text;
    }

    /// <summary>Drags a node onto another one, as the tree's own gesture would end.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <c>TreeView.MoveNode</c> rather than by synthesising a drag.</b> A drag is a
    ///     press, a slop threshold, several moves and a release, and reproducing it here would be a
    ///     test of the gesture recogniser — which has its own. What this is about is what the
    ///     application does with the <c>Moved</c> event at the end of one.
    /// </remarks>
    static void Move(EditorSession fixture, string what, string onto) {
        var tree = fixture.Hierarchy;

        var node = Node(tree, what);
        var target = Node(tree, onto);

        tree.MoveNode(node, target, DropPosition.Into);
    }

    static TreeNode Node(TreeView tree, string text) =>
        Descendants(tree.Root).FirstOrDefault(node => node.Text == text)
        ?? throw new InvalidOperationException($"no node called '{text}'");

    static List<string> Rows(TreeView tree) =>
        [.. Descendants(tree.Root).Select(node => node.Text ?? string.Empty)];

    static IEnumerable<TreeNode> Descendants(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    static T? Find<T>(Vixen.Ui.UiElement element) where T : Vixen.Ui.UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
