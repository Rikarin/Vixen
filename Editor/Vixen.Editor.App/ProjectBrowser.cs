// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>The Project panel: what is in the asset database, as a tree somebody can click.</summary>
/// <remarks>
///     <para>
///         <b>The panel that turns a scanned project into something visible.</b> The database has been
///         built at startup since the shell existed — <c>EditorProject.Open</c> scans it, repairs the
///         sidecars and indexes every GUID — and until now nothing showed the result. This is the view
///         over it, and it is the seam every other "pick an asset" feature hangs off: the inspector's
///         asset picker, drag-and-drop into the scene, and the reverse-reference lookups all want a
///         browser to point at.
///     </para>
///     <para>
///         ⚠ <b>The shape is <see cref="AssetTree" />'s and not this class's</b>, which is what keeps
///         the ordering, the folder synthesis and the search testable without a document. What is left
///         here is genuinely a view: rows, selection, and when to rebuild.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt on demand, not watched.</b> Nothing here has a file-system watcher, so a file
///         added outside the editor appears when the project is rescanned. A watcher is worth having
///         and is not free — it needs debouncing, a rename heuristic and a way to not fight the
///         editor's own writes — and pretending to be live while missing half the events would be
///         worse than a Refresh that says what it does.
///     </para>
/// </remarks>
sealed class ProjectBrowser {
    readonly EditorProject project;
    readonly TreeView tree;
    readonly SearchBox search;

    AssetTreeNode root;

    /// <summary>Builds the panel's contents into a container.</summary>
    /// <param name="project">The project being browsed.</param>
    /// <param name="panel">Where to put the rows.</param>
    public ProjectBrowser(EditorProject project, UiElement panel) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(panel);

        this.project = project;
        root = AssetTree.Build(project.Assets.Entries);

        // A `SearchBox` rather than a plain field, so the clear button and the magnifier come from
        // the control that already has them rather than from a panel drawing its own.
        search = panel.Add<SearchBox>();
        search.Placeholder = "Search assets";
        search.ValueChanged += (_, _) => Populate();

        tree = panel.Add<TreeView>();
        tree.MultiSelect = true;

        tree.SelectionChanged += changed => {
            List<AssetId> picked = [];

            foreach (var node in changed.Selection) {
                // ⚠ Only what the database has an identity for. A folder synthesised because its
                // sidecar was never written has no GUID, and putting `AssetId.Empty` in the selection
                // would make every such folder select the same nothing — and look like one asset.
                if (node.Tag is AssetTreeNode { IsIndexed: true } asset) {
                    picked.Add(asset.Guid);
                }
            }

            project.Selection.Set(picked);
        };

        Populate();
    }

    /// <summary>How many rows the tree is showing.</summary>
    public int Count => tree.Root.Children.Count;

    /// <summary>Rescans the project and rebuilds the tree.</summary>
    /// <returns>What the scan found, for whoever is reporting it.</returns>
    /// <remarks>
    ///     ⚠ <b>The index is saved after the scan.</b> It lives in <c>Library/</c> and is what makes
    ///     the <i>next</i> launch skip the walk; a rescan that left the old one on disk would make
    ///     the editor slower the more often it was refreshed, which is the wrong way round.
    /// </remarks>
    public ScanReport Rescan() {
        var report = project.Assets.Scan();

        project.Assets.Save();
        // ⚠ Rebuilt with it. The reverse index is what answers "what would break if I deleted this",
        // and one built against the previous scan answers it about assets that have moved.
        project.References.Build(project.Assets);

        root = AssetTree.Build(project.Assets.Entries);
        Populate();

        return report;
    }

    /// <summary>Rebuilds the rows from the tree and whatever is in the search field.</summary>
    void Populate() {
        while (tree.Root.Children.Count > 0) {
            tree.Root.Remove(tree.Root.Children[^1]);
        }

        var shown = AssetTree.Filter(root, search.Value);

        Branch(tree.Root, shown);
        tree.Refresh();

        // The root and its immediate folders, so a project opens showing something. Deeper than that
        // is the user's business — and a search has already narrowed to what matched, so opening
        // everything it kept is what makes a result visible without a click.
        foreach (var node in tree.Root.Children) {
            tree.Expand(node);

            if (!string.IsNullOrWhiteSpace(search.Value)) {
                Reveal(node);
            }
        }

        void Branch(TreeNode parent, AssetTreeNode asset) {
            var node = parent.Add(asset.Name, asset);

            foreach (var child in asset.Children) {
                Branch(node, child);
            }
        }

        void Reveal(TreeNode node) {
            tree.Expand(node);

            foreach (var child in node.Children) {
                Reveal(child);
            }
        }
    }
}
