// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The scene hierarchy, held to what the hand-written binding did and to where it lays out.</summary>
public sealed class SceneHierarchyViewTests {
    /// <summary>
    ///     ⚠ The tree the scene editor's own factory builds fills its tab, to the pixel the
    ///     hand-written binding produced.
    /// </summary>
    /// <remarks>
    ///     The A/B a port of a view owes. The numbers are the pre-port build's, measured through the
    ///     production path — <c>SceneEditorFactory.CreateView</c>, a <c>document-tabs</c> set in a
    ///     1200×800 document with all four sheets loaded — and they are exact rather than a floor
    ///     because the risk this port carries is a wrapper element with no rule behind it: the tree
    ///     went straight into the tab panel before and sits inside a <c>scene-hierarchy</c> now, and
    ///     an unstyled element lays its children out in a row and grows to nothing. That failure
    ///     reads on screen as "the hierarchy is empty" and in every count as success.
    /// </remarks>
    [Fact]
    public void The_tree_the_factory_builds_fills_its_tab() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);

        scene.Add("Root", LocalTransform.At(Vector3.Zero));

        var tabs = new SceneEditorFactory(_ => world).CreateView(scene, harness.Ui.Document.Root);

        harness.Ui.Frames(2);

        var tree = Descendant(tabs);

        Assert.Equal(1200f, tree.Width);
        Assert.Equal(773f, tree.Height);
    }

    /// <summary>The rows are the document's roots and their children, in document order.</summary>
    [Fact]
    public void The_rows_are_the_documents_roots_and_their_children() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);
        var parent = scene.Add("Parent", LocalTransform.At(Vector3.Zero));

        scene.Add("Child", LocalTransform.At(Vector3.Zero), parent);
        scene.Add("Sibling", LocalTransform.At(Vector3.Zero));

        var view = Open(harness, scene);

        harness.Ui.Frame();

        Assert.Equal(2, view.RootCount);
        Assert.Equal(["Parent", "Sibling"], view.Tree.Root.Children.Select(node => node.Text));
        Assert.Equal(["Child"], view.Tree.Root.Children[0].Children.Select(node => node.Text));
    }

    /// <summary>A structure change rebuilds, and the roots come back expanded.</summary>
    [Fact]
    public void A_structure_change_rebuilds_and_re_expands_the_roots() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);
        var parent = scene.Add("Parent", LocalTransform.At(Vector3.Zero));

        var view = Open(harness, scene);

        harness.Ui.Frame();
        Assert.Equal(1, view.RootCount);

        scene.Add("Later", LocalTransform.At(Vector3.Zero), parent);
        harness.Ui.Frame();

        Assert.Equal(1, view.RootCount);
        Assert.True(view.Tree.Root.Children[0].IsExpanded);
        Assert.Equal(["Later"], view.Tree.Root.Children[0].Children.Select(node => node.Text));
    }

    /// <summary>
    ///     ⚠ A rename moves one row's text and does not rebuild, so an expansion deeper than the
    ///     roots survives it.
    /// </summary>
    /// <remarks>
    ///     The model decision <a href="https://github.com/Rikarin/Vixen/issues/1322">#1322</a> names.
    ///     <c>Rebuild</c> re-expands the roots and nothing below them, so a tree rebuilt on a rename
    ///     would collapse everything the user had opened — the same cost the document's refusal to
    ///     raise <c>StructureChanged</c> for a transform edit or a rename exists to avoid.
    /// </remarks>
    [Fact]
    public void A_rename_keeps_an_expansion_deeper_than_the_roots() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);
        var parent = scene.Add("Parent", LocalTransform.At(Vector3.Zero));
        var child = scene.Add("Child", LocalTransform.At(Vector3.Zero), parent);

        scene.Add("Grandchild", LocalTransform.At(Vector3.Zero), child);

        var view = Open(harness, scene);

        harness.Ui.Frame();

        var opened = view.Tree.Root.Children[0].Children[0];

        view.Tree.Expand(opened);
        harness.Ui.Frame();

        Assert.True(opened.IsExpanded);

        scene.Rename(parent, "Renamed");
        harness.Ui.Frame();

        Assert.Equal("Renamed", view.Tree.Root.Children[0].Text);
        Assert.True(
            view.Tree.Root.Children[0].Children[0].IsExpanded,
            "the rename rebuilt the tree and lost the expansion below the roots"
        );
    }

    /// <summary>Selecting a row writes the document's selection.</summary>
    [Fact]
    public void Selecting_a_row_writes_the_documents_selection() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);

        scene.Add("First", LocalTransform.At(Vector3.Zero));

        var second = scene.Add("Second", LocalTransform.At(Vector3.Zero));
        var view = Open(harness, scene);

        harness.Ui.Frame();

        view.Tree.SelectedNodes = [view.Tree.Root.Children[1]];
        harness.Ui.Frame();

        Assert.Equal([second], scene.Selection.Items);
    }

    /// <summary>
    ///     ⚠ A view whose element has left the document stops listening, without anybody remembering
    ///     to say so.
    /// </summary>
    /// <remarks>
    ///     The hand-written binding had a <c>Detach()</c> and the scene editor's own factory never
    ///     called it — it wrote <c>_ = new SceneHierarchyView(scene, panel)</c> and kept no handle,
    ///     so the one production path that reopens a panel could not have. A tree that stays
    ///     subscribed rebuilds itself on every structure change for the life of the document, once
    ///     per reopen, for a tree nobody can see.
    /// </remarks>
    [Fact]
    public void A_view_that_has_left_the_document_stops_listening() {
        using var harness = new ViewHarness();
        using var world = new World("Scene");

        var scene = Scene(harness, world);

        scene.Add("Root", LocalTransform.At(Vector3.Zero));

        var view = Open(harness, scene);

        harness.Ui.Frame();
        Assert.Equal(1, view.RootCount);

        Close(view);
        harness.Ui.Frame();

        scene.Add("Added after the panel closed", LocalTransform.At(Vector3.Zero));
        harness.Ui.Frame();

        Assert.Equal(1, view.RootCount);
    }

    static SceneDocument Scene(ViewHarness harness, World world) =>
        new(harness.Project.Project, world, AssetId.Empty, "Level");

    /// <summary>Opens the hierarchy on a panel of its own.</summary>
    static SceneHierarchyView Open(ViewHarness harness, SceneDocument scene) {
        var view = harness.Ui.Document.Root.Add<SceneHierarchyView>();

        view.Show(scene);

        return view;
    }

    static void Close(SceneHierarchyView view) => view.Remove();

    /// <summary>The one tree under an element.</summary>
    static TreeView Descendant(UiElement root) {
        if (root is TreeView found) {
            return found;
        }

        foreach (var child in root.Children) {
            if (Descendant(child) is { } nested) {
                return nested;
            }
        }

        return null!;
    }
}
