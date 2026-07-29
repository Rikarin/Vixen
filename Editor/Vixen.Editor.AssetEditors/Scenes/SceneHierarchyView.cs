// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Scenes;

/// <summary>A scene's entities as a tree: what is in it, what is selected, and what things are called.</summary>
/// <remarks>
///     <para>
///         <b>The half of doc 11's scene editor that is not a viewport.</b> A scene editor is "scene
///         view + hierarchy + inspector"; the viewport is <c>Vixen.Editor.SceneView</c>'s and the
///         inspector is <c>Vixen.Editor.Inspector</c>'s, and this is the third — written once, so
///         that the scene editor and the prefab editor are one tree over two documents.
///     </para>
///     <para>
///         <b>A class over a <see cref="TreeView" /> rather than a control deriving from one</b>, on
///         <c>ProjectBrowser</c>'s terms and because <see cref="TreeView" /> is sealed. What is here
///         is genuinely a binding: rows, selection, renames, and when to rebuild.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt from <c>StructureChanged</c>, not polled.</b> The document raises it when
///         entities appear, disappear or change parent, and deliberately does not raise it for a
///         transform edit or a rename — a tree rebuilt on every frame of a gizmo drag would lose its
///         expansion state forty times a second. A rename moves one row's text instead.
///     </para>
///     <para>
///         ⚠ <b>Selection travels out of this and not into it.</b> Clicking a row writes
///         <c>SceneDocument.Selection</c>; something else selecting an entity does not move the
///         highlight, because nothing here subscribes to the signal. That is the same gap the
///         application's own hierarchy has, and the fix is the same <c>Effect</c> that needs a
///         reactive scheduler the editor's loop does not flush.
///     </para>
/// </remarks>
public sealed class SceneHierarchyView {
    readonly SceneDocument document;

    /// <summary>The rows.</summary>
    public TreeView Tree { get; }

    /// <summary>The scene being shown.</summary>
    public SceneDocument Scene => document;

    /// <summary>Binds a tree to a scene, inside a container.</summary>
    /// <param name="scene">The document.</param>
    /// <param name="panel">Where the tree goes.</param>
    public SceneHierarchyView(SceneDocument scene, UiElement panel) {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(panel);

        document = scene;

        Tree = panel.Add<TreeView>();
        Tree.MultiSelect = true;

        Tree.SelectionChanged += tree => {
            List<Entity> picked = [];

            foreach (var node in tree.Selection) {
                if (node.Tag is Entity entity) {
                    picked.Add(entity);
                }
            }

            scene.Selection.Set(picked);
        };

        Tree.Renamed += (_, node, name) => {
            if (node.Tag is Entity entity) {
                scene.Rename(entity, name);
            }
        };

        scene.StructureChanged += Restructured;
        scene.Renamed += Renamed;

        Rebuild();
    }

    /// <summary>How many rows are at the top level.</summary>
    public int RootCount => Tree.Root.Children.Count;

    /// <summary>Stops listening to the document.</summary>
    /// <remarks>
    ///     A panel's factory runs again when it is reopened, so a view that stayed subscribed after
    ///     its tree was thrown away would rebuild a tree nobody can see — once per reopen, for ever.
    /// </remarks>
    public void Detach() {
        document.StructureChanged -= Restructured;
        document.Renamed -= Renamed;
    }

    /// <summary>Rebuilds the rows from the document as it stands.</summary>
    public void Rebuild() {
        while (Tree.Root.Children.Count > 0) {
            Tree.Root.Remove(Tree.Root.Children[^1]);
        }

        foreach (var entity in document.Roots) {
            Branch(Tree.Root, document, entity);
        }

        Tree.Refresh();

        // The roots, so a scene opens showing something. Deeper than that is the user's business —
        // the same rule the project browser follows.
        foreach (var node in Tree.Root.Children) {
            Tree.Expand(node);
        }
    }

    static void Branch(TreeNode parent, SceneDocument scene, Entity entity) {
        var node = parent.Add(scene.NameOf(entity), entity);

        foreach (var child in Hierarchy.ChildrenOf(scene.World, entity)) {
            Branch(node, scene, child);
        }
    }

    void Restructured(SceneDocument changed) => Rebuild();

    void Renamed(SceneDocument changed, Entity entity) {
        foreach (var node in Descendants(Tree.Root)) {
            if (node.Tag is Entity found && found == entity) {
                node.Text = changed.NameOf(entity);
                Tree.Refresh();

                return;
            }
        }
    }

    static IEnumerable<TreeNode> Descendants(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var nested in Descendants(child)) {
                yield return nested;
            }
        }
    }
}
