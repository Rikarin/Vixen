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
///     <para>
///         ⚠ <b>Every verb goes out as an event rather than being done here.</b> Renaming a file,
///         moving one and dropping one into the scene are all operations on the <i>project</i>, and a
///         browser that performed them would be the second place that knows how — the first being
///         <see cref="AssetOperations" />, which is where the sidecar invariant is written down and
///         tested.
///     </para>
/// </remarks>
sealed class ProjectBrowser {
    /// <summary>What the type filter offers when nothing is chosen.</summary>
    const string AnyType = "All types";

    readonly EditorProject project;
    readonly TreeView tree;
    readonly SearchBox search;
    readonly Select kinds;

    AssetTreeNode root;

    /// <summary>Raised when a row is activated — a double-click, or Enter on the keyboard.</summary>
    /// <remarks>
    ///     What opens an asset. The browser deliberately does not open it itself: which editor claims
    ///     a file is <c>AssetEditorRegistry</c>'s and where the resulting document goes is the
    ///     workspace's, and a browser that knew both would be the third thing that has to be told
    ///     when either changes.
    /// </remarks>
    public event Action<AssetId>? Activated;

    /// <summary>Raised when a row's inline editor is committed.</summary>
    public event Action<AssetId, string>? Renamed;

    /// <summary>Raised when rows are dropped onto a folder row.</summary>
    public event Action<IReadOnlyList<AssetId>, AssetId>? Moved;

    /// <summary>Builds the panel's contents into a container.</summary>
    /// <param name="project">The project being browsed.</param>
    /// <param name="panel">Where to put the rows.</param>
    public ProjectBrowser(EditorProject project, UiElement panel) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(panel);

        this.project = project;
        root = AssetTree.Build(project.Assets.Entries);

        var bar = panel.Add<UiElement>("browser-filters");

        // A `SearchBox` rather than a plain field, so the clear button and the magnifier come from
        // the control that already has them rather than from a panel drawing its own.
        search = bar.Add<SearchBox>();
        search.Placeholder = "Search assets";
        search.ValueChanged += (_, _) => Populate();

        // ⚠ The importer tag rather than the extension, and the list is what the project actually
        // holds rather than everything the engine can import. A dropdown offering nine formats in a
        // project with two of them is a filter that mostly narrows to nothing, and a filter that
        // narrows to nothing is one people stop using after the second time.
        kinds = bar.Add<Select>();
        kinds.SelectionChanged += (_, _) => Populate();

        tree = panel.Add<TreeView>();
        tree.MultiSelect = true;
        tree.AllowDrag = true;

        tree.Activated += (_, node) => {
            // ⚠ Only what the database has an identity for, and never a folder — the same rule the
            // selection follows, for the same reason.
            if (node.Tag is AssetTreeNode { IsIndexed: true, IsFolder: false } asset) {
                Activated?.Invoke(asset.Guid);
            }
        };

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

        tree.Renamed += (_, node, name) => {
            if (node.Tag is AssetTreeNode { IsIndexed: true } asset) {
                Renamed?.Invoke(asset.Guid, name);
            }
        };

        // ⚠ The tree has already moved the row by the time this runs and the disk has not, so the
        // handler reads where it landed, tells the application, and lets the rescan put the rows
        // back where the disk says they are. A move the file system refuses would otherwise leave
        // the browser showing a folder that does not contain what it is drawing.
        tree.Moved += (_, node) => {
            if (node.Parent?.Tag is AssetTreeNode { IsIndexed: true, IsFolder: true } folder) {
                Moved?.Invoke(Dragged(node), folder.Guid);
            } else {
                Populate();
            }
        };

        Refilter();
        Populate();
    }

    /// <summary>How many rows the tree is showing.</summary>
    public int Count => tree.Root.Children.Count;

    /// <summary>The tree, for the harness and for the panel that holds it.</summary>
    public TreeView Tree => tree;

    /// <summary>Deselects everything.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the tree rather than through <c>EditorProject.Selection</c>.</b> The rows'
    ///     highlight is the tree's own state and the document's selection is written <i>from</i> it —
    ///     so clearing the far end alone leaves a row that looks selected, and the next click on it
    ///     is a click on something the tree already thinks is picked. Clearing here raises
    ///     <c>SelectionChanged</c>, which is what empties the project's selection.
    /// </remarks>
    public void Deselect() => tree.Select(null);

    /// <summary>Opens the inline editor on an asset's row.</summary>
    /// <param name="asset">Which asset.</param>
    /// <returns>Whether a row for it is on screen to edit.</returns>
    public bool BeginRename(AssetId asset) {
        foreach (var node in Descendants(tree.Root)) {
            if (node.Tag is AssetTreeNode { IsIndexed: true } found && found.Guid == asset) {
                tree.BeginRename(node);
                return true;
            }
        }

        return false;
    }

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

        Rebuild();
        return report;
    }

    /// <summary>Rebuilds the rows from the database, keeping the filters.</summary>
    /// <remarks>
    ///     What an operation that has already rescanned calls. <see cref="Rescan" /> is the whole
    ///     round trip and doing it twice for one rename is two walks of the project.
    /// </remarks>
    public void Rebuild() {
        root = AssetTree.Build(project.Assets.Entries);

        Refilter();
        Populate();
    }

    /// <summary>Brings the type dropdown into line with what the project actually holds.</summary>
    void Refilter() {
        var chosen = kinds.Value;

        kinds.ClearOptions();
        kinds.AddOption(AnyType);

        foreach (var tag in project.Assets.Entries
            .Where(entry => !entry.IsFolder && !string.IsNullOrEmpty(entry.ImporterTag))
            .Select(entry => entry.ImporterTag!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)) {
            kinds.AddOption(tag);
        }

        // ⚠ Kept if it still exists. Deleting the last texture in a project must not silently widen
        // a filter somebody set — but a filter naming an importer nothing uses any more would hide
        // everything with no way to tell why.
        kinds.Value = chosen is not null && kinds.Options.Any(option => option.Value == chosen) ? chosen : AnyType;
    }

    /// <summary>The assets a drag is carrying: the whole selection when it includes the dragged row.</summary>
    /// <remarks>
    ///     The same rule the outliner follows for entities, and for the same reason: dragging one of
    ///     five selected rows and having four of them stay behind is the behaviour nobody means.
    /// </remarks>
    List<AssetId> Dragged(TreeNode node) {
        if (node.Tag is not AssetTreeNode { IsIndexed: true } asset) {
            return [];
        }

        return tree.Selection.Contains(node) ? [.. project.Selection] : [asset.Guid];
    }

    /// <summary>Rebuilds the rows from the tree and whatever the two filters say.</summary>
    void Populate() {
        while (tree.Root.Children.Count > 0) {
            tree.Root.Remove(tree.Root.Children[^1]);
        }

        var shown = AssetTree.Filter(root, search.Value);
        var kind = kinds.Value is { } value && value != AnyType ? value : null;

        Branch(tree.Root, shown, kind);
        tree.Refresh();

        // The root and its immediate folders, so a project opens showing something. Deeper than that
        // is the user's business — and a search has already narrowed to what matched, so opening
        // everything it kept is what makes a result visible without a click.
        foreach (var node in tree.Root.Children) {
            tree.Expand(node);

            if (!string.IsNullOrWhiteSpace(search.Value) || kind is not null) {
                Reveal(node);
            }
        }

        void Reveal(TreeNode node) {
            tree.Expand(node);

            foreach (var child in node.Children) {
                Reveal(child);
            }
        }
    }

    /// <summary>Adds an asset and its children, dropping the branches the type filter empties.</summary>
    /// <remarks>
    ///     ⚠ <b>Decided bottom-up, the same way the outliner's name filter is.</b> A folder survives
    ///     because something under it did; dropping a folder whose own tag does not match would take
    ///     every matching file inside it with it, which for a filter is the one row somebody was
    ///     looking for.
    /// </remarks>
    bool Branch(TreeNode parent, AssetTreeNode asset, string? kind) {
        var node = parent.Add(asset.Name, asset);
        var kept = kind is null || (!asset.IsFolder && Tagged(asset, kind));

        foreach (var child in asset.Children) {
            kept |= Branch(node, child, kind);
        }

        if (!kept) {
            parent.Remove(node);
        }

        return kept;
    }

    bool Tagged(AssetTreeNode asset, string kind) =>
        asset.IsIndexed
        && project.Assets.TryGetByGuid(asset.Guid, out var entry)
        && string.Equals(entry.ImporterTag, kind, StringComparison.Ordinal);

    static IEnumerable<TreeNode> Descendants(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
