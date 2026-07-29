// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>One entry in a project browser's tree: a folder, or an asset in one.</summary>
/// <remarks>
///     ⚠ <b>Not <c>TreeNode</c>, and it must not be.</b> <c>Vixen.Editor.Core</c> does not reference
///     the interface framework — see the assembly's own README for why — so a browser's shape is
///     modelled here and turned into rows by whoever is drawing it. That is the same split
///     <c>DockLayout</c> and <c>NodeGraph</c> make: the arrangement is a value that can be asserted
///     on without a document, a stylesheet or a font.
/// </remarks>
/// <param name="Name">What it is called, with its extension if it has one.</param>
/// <param name="Path">Where it is, project-relative with forward slashes.</param>
/// <param name="IsFolder">Whether it holds other things.</param>
/// <param name="Guid">
///     Its identity, or <see cref="AssetId.Empty" /> for a folder the database has no entry for —
///     see <see cref="AssetTree.Build" />.
/// </param>
/// <param name="Children">What is under it, folders first and then by name.</param>
public sealed record AssetTreeNode(
    string Name,
    string Path,
    bool IsFolder,
    AssetId Guid,
    IReadOnlyList<AssetTreeNode> Children
) {
    /// <summary>Whether the database knows this by a GUID.</summary>
    /// <remarks>
    ///     False only for a synthesised folder. Worth asking before offering anything that needs an
    ///     identity — a reference, a rename that has to update one, an inspector.
    /// </remarks>
    public bool IsIndexed => !Guid.IsEmpty;

    /// <summary>Every node under this one, including this one, parents before children.</summary>
    public IEnumerable<AssetTreeNode> Descend() {
        yield return this;

        foreach (var child in Children) {
            foreach (var node in child.Descend()) {
                yield return node;
            }
        }
    }
}

/// <summary>Turns the asset database's flat index into the shape a browser shows.</summary>
/// <remarks>
///     <para>
///         <b>The database is a dictionary and a browser is a tree, and this is the whole of the
///         difference.</b> <see cref="AssetDatabase.Entries" /> is every asset with its full path and
///         nothing else — which is the right shape for "what is this GUID" and the wrong one for
///         "what is in this folder".
///     </para>
///     <para>
///         ⚠ <b>Pure, and deliberately not a live view.</b> Nothing here watches the file system.
///         A browser rebuilds after a scan, which is the moment the database itself changed; a tree
///         that tried to stay current would be a second source of truth for what is on disk.
///     </para>
/// </remarks>
public static class AssetTree {
    /// <summary>What a browser calls the folder every asset is under.</summary>
    /// <remarks>
    ///     ⚠ Not itself an entry. <see cref="AssetDatabase.Scan" /> walks the inside of
    ///     <c>Assets/</c>, so every folder <i>below</i> it is indexed and the directory itself never
    ///     is — which would leave a tree with no root to hang anything off.
    /// </remarks>
    public const string RootName = "Assets";

    /// <summary>Builds the tree for a project.</summary>
    /// <param name="entries">Every asset, in any order.</param>
    /// <param name="rootName">What to call the root. The default is what the database scans.</param>
    /// <returns>The root, whose children are the top-level folders and files.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A folder with no entry of its own is still a folder.</b> Scanning with
    ///         <see cref="ScanOptions.ReadOnly" /> — which is what a build server checking a project
    ///         does — creates no sidecars, so folders come back unindexed while the files inside them
    ///         are indexed by path. Requiring an entry per level would silently drop every asset
    ///         under such a folder, which is a browser that is empty for a project that is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order is imposed here rather than taken from the database.</b>
    ///         <see cref="AssetDatabase.Entries" /> is a dictionary's values and says so — "in no
    ///         particular order" — and a browser whose rows move between runs is one where nothing is
    ///         where it was left.
    ///     </para>
    /// </remarks>
    public static AssetTreeNode Build(IReadOnlyCollection<AssetEntry> entries, string rootName = RootName) {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(rootName);

        var folders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var root = new Builder(rootName, rootName, AssetId.Empty);

        folders[rootName] = root;

        // ⚠ Folders first and in path order, so a parent is built before anything it holds. The file
        // pass below can then assume every folder on its path exists — and where one does not,
        // because the scan was read-only, `Folder` makes it.
        foreach (var entry in entries.Where(entry => entry.IsFolder).OrderBy(entry => entry.Path, StringComparer.Ordinal)) {
            Folder(entry.Path).Guid = entry.Guid;
        }

        foreach (var entry in entries.Where(entry => !entry.IsFolder)) {
            var separator = entry.Path.LastIndexOf('/');
            var parent = separator < 0 ? root : Folder(entry.Path[..separator]);

            parent.Files.Add(new(entry.Name, entry.Path, false, entry.Guid, []));
        }

        return root.Build();

        Builder Folder(string path) {
            if (folders.TryGetValue(path, out var found)) {
                return found;
            }

            var separator = path.LastIndexOf('/');
            var name = separator < 0 ? path : path[(separator + 1)..];
            var parent = separator < 0 ? root : Folder(path[..separator]);
            var made = new Builder(name, path, AssetId.Empty);

            parent.Folders.Add(made);
            folders[path] = made;

            return made;
        }
    }

    /// <summary>Finds a node by its project-relative path.</summary>
    /// <param name="root">The tree to look in.</param>
    /// <param name="path">The path, project-relative with forward slashes.</param>
    /// <returns>The node, or <see langword="null" />.</returns>
    public static AssetTreeNode? Find(AssetTreeNode root, string path) {
        ArgumentNullException.ThrowIfNull(root);

        return string.IsNullOrEmpty(path)
            ? null
            : root.Descend().FirstOrDefault(node => string.Equals(node.Path, path, StringComparison.Ordinal));
    }

    /// <summary>Narrows a tree to what matches a search, and to the folders holding it.</summary>
    /// <param name="root">The whole tree.</param>
    /// <param name="query">What was typed. Empty returns the tree unchanged.</param>
    /// <returns>The narrowed tree, whose root is always present even when nothing matched.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A folder survives because of what is in it, not because of its name.</b> Matching
    ///         folders on their own names and dropping the rest would hide every file in a folder
    ///         whose name does not contain the search — which is the opposite of what typing a file
    ///         name is for. A folder whose name <i>does</i> match still keeps everything under it,
    ///         because that is a person navigating rather than searching.
    ///     </para>
    ///     <para>
    ///         The comparison ignores case, because a browser that will not find <c>Player.png</c>
    ///         for <c>player</c> is one nobody types into twice.
    ///     </para>
    /// </remarks>
    public static AssetTreeNode Filter(AssetTreeNode root, string? query) {
        ArgumentNullException.ThrowIfNull(root);

        return string.IsNullOrWhiteSpace(query) ? root : Narrow(root, query.Trim()) ?? root with { Children = [] };

        static AssetTreeNode? Narrow(AssetTreeNode node, string query) {
            if (Matches(node, query)) {
                // Kept whole. A matched folder is somewhere to look inside, not a result on its own.
                return node;
            }

            var kept = node.Children.Select(child => Narrow(child, query)).OfType<AssetTreeNode>().ToArray();
            return kept.Length == 0 ? null : node with { Children = kept };
        }

        static bool Matches(AssetTreeNode node, string query) =>
            node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A node being assembled, before its children are known and ordered.</summary>
    sealed class Builder(string name, string path, AssetId guid) {
        public List<Builder> Folders { get; } = [];

        public List<AssetTreeNode> Files { get; } = [];

        public AssetId Guid { get; set; } = guid;

        public AssetTreeNode Build() => new(name, path, true, Guid, [.. Folders.Select(folder => folder.Build()).Concat(Files).Order(Order)]);
    }

    /// <summary>Folders before files, then by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Case-insensitive first and then not</b>, which is two comparisons rather than one for
    ///     a reason. Ordinal alone sorts every capital before every lowercase, so <c>Zebra.png</c>
    ///     lands above <c>apple.png</c>. Ignoring case alone leaves <c>README</c> and <c>readme</c>
    ///     with no order at all — equal, so whichever the enumeration reached first wins, which is
    ///     the non-determinism this comparer exists to remove.
    /// </remarks>
    static readonly IComparer<AssetTreeNode> Order = Comparer<AssetTreeNode>.Create(
        (left, right) => {
            if (left.IsFolder != right.IsFolder) {
                return left.IsFolder ? -1 : 1;
            }

            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(left.Name, right.Name);
        }
    );
}
