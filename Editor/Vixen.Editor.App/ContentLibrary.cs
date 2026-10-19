// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Vixen.Core;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>The editor's <see cref="IContentLibrary" />: import, package export, collections and saved filters.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten</b> — doc 50 § D2. Every body here was a private method of
///         <c>EditorApplication</c> (<c>BringIn</c>, <c>Bring</c>, <c>CopyTree</c>, <c>Inside</c>,
///         <c>Closure</c>, <c>WritePackage</c>, the collection and filter verbs), and what changed is
///         who can call them: the Assets menu, the browser's OS drop, a test with no window and a
///         plugin holding <c>Require&lt;IContentLibrary&gt;()</c> now reach one object.
///     </para>
///     <para>
///         ⚠ <b>The state moved with the verbs.</b> What the application kept for them — the
///         collections' settings store and the save after each change — is read here; the two things
///         that stay the application's are handed in as delegates rather than reached back for: the
///         preferences object (which the settings window's Revert <i>replaces</i>, so it is asked for
///         on every call rather than held) and the content tasks' import.
///     </para>
/// </remarks>
internal sealed class ContentLibrary : IContentLibrary {
    /// <summary>What a package this editor writes is called.</summary>
    internal const string PackageExtension = ".vxpackage";

    readonly EditorProject project;
    readonly Action<string, NotificationSeverity, string?> notify;
    readonly Func<List<SavedAssetFilter>> filters;
    readonly Action writeFilters;
    readonly Action import;

    /// <summary>Builds the library over a project.</summary>
    /// <param name="project">The open project.</param>
    /// <param name="notify">
    ///     Where a refusal or a success is reported — the shell's notification centre in the editor. ⚠
    ///     A delegate rather than the centre, because the centre draws toasts and so needs a document:
    ///     the service must be callable with no window, which is doc 50 § D2's first property.
    /// </param>
    /// <param name="filters">
    ///     The user's saved filters, asked for on every call. ⚠ A lambda over the preferences rather than
    ///     the list, because the settings window's Revert re-reads the file into a <i>new</i> object.
    /// </param>
    /// <param name="writeFilters">Writes the preferences file the filters live in.</param>
    /// <param name="import">Imports what a scan found, on the content tasks' worker.</param>
    /// <param name="shown">
    ///     What the browser is showing, or <see langword="null" /> for a model whose view lives only as
    ///     long as it does — the editor hands one kept in the user's preferences.
    /// </param>
    public ContentLibrary(
        EditorProject project,
        Action<string, NotificationSeverity, string?> notify,
        Func<List<SavedAssetFilter>> filters,
        Action writeFilters,
        Action import,
        ShownContent? shown = null
    ) {
        this.project = project;
        this.notify = notify;
        this.filters = filters;
        this.writeFilters = writeFilters;
        this.import = import;
        Shown = shown ?? new ShownContent();
    }

    /// <inheritdoc />
    public EditorProject Project => project;

    /// <inheritdoc />
    public ShownContent Shown { get; }

    /// <inheritdoc />
    public event Action? CollectionsChanged;

    /// <inheritdoc />
    public event Action? Arrived;

    /// <summary>Copies files and folders into the project, then scans and imports what arrived.</summary>
    /// <param name="paths">Absolute paths on the user's disk.</param>
    /// <param name="folder">The project-relative folder to land them in.</param>
    /// <returns>How many were copied.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Copied rather than referenced.</b> An asset outside the project tree is one the
    ///         content build cannot find, the reference index cannot name and a colleague does not
    ///         have — every engine that allowed it spent years telling people why their build was
    ///         broken.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A directory is copied whole, and until it was, the commonest drag there is did
    ///         nothing.</b> "Drag a folder of textures in from Finder" is doc 20's own wording for
    ///         this row, and <c>File.Copy</c> over a directory throws — so the dialog half reported
    ///         "could not copy the files" for the one input every user tries first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A path already inside the project is refused rather than duplicated.</b> The
    ///         browser is a file view of <c>Assets/</c>, and a person dragging a row of it onto
    ///         itself means a move — which <c>EditorApplication.MoveAssets</c> does with identity intact.
    ///         Copying would mint a second GUID for the same bytes, which is the state the reference
    ///         index cannot repair.
    ///     </para>
    /// </remarks>
    public int Import(IReadOnlyList<string> paths, string folder) {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0) {
            return 0;
        }

        var destination = project.Paths.Absolute(string.IsNullOrEmpty(folder) ? AssetTree.RootName : folder);
        var copied = 0;

        try {
            Directory.CreateDirectory(destination);

            foreach (var path in paths) {
                copied += Bring(path, destination) ? 1 : 0;
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            notify("Could not copy the files", NotificationSeverity.Error, exception.Message);
            return copied;
        }

        if (copied == 0) {
            return 0;
        }

        project.Assets.Scan();
        Arrived?.Invoke();
        import();

        notify(
            copied == 1 ? $"1 asset imported into {folder}" : $"{copied} assets imported into {folder}",
            NotificationSeverity.Success,
            null
        );

        return copied;
    }

    /// <summary>Copies one file or one directory in, and says why not if it will not.</summary>
    /// <param name="path">What was chosen or dropped.</param>
    /// <param name="destination">The absolute folder it goes into.</param>
    /// <returns>Whether anything was copied.</returns>
    bool Bring(string path, string destination) {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(name)) {
            return false;
        }

        var target = Path.Combine(destination, name);

        if (Inside(path, project.Paths.Root)) {
            notify(
                name + " is already in this project",
                NotificationSeverity.Warning,
                "Drag it onto a folder in the browser to move it, which keeps its references."
            );

            return false;
        }

        // ⚠ Never over something already there. Two textures called `wood.png` from two folders is
        // the ordinary case, and silently replacing the one already in the project is a change
        // nothing records and undo cannot reach.
        if (File.Exists(target) || Directory.Exists(target)) {
            notify(
                name + " is already in the project",
                NotificationSeverity.Warning,
                "Rename it, or import it into a folder of its own."
            );

            return false;
        }

        if (Directory.Exists(path)) {
            CopyTree(path, target);
            return true;
        }

        if (!File.Exists(path)) {
            return false;
        }

        File.Copy(path, target);
        return true;
    }

    /// <summary>Copies a directory and everything under it.</summary>
    /// <param name="from">The source directory.</param>
    /// <param name="to">Where it goes, which does not exist yet.</param>
    static void CopyTree(string from, string to) {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from)) {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(from)) {
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }

    /// <summary>Whether a path is the given directory or lives under it.</summary>
    /// <param name="path">The path in question.</param>
    /// <param name="directory">The directory it might be under.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    ///     ⚠ <b>Compared with a separator on the end, so <c>ProjectTwo</c> is not "inside"
    ///     <c>Project</c>.</b> A prefix test without one is the check that refuses a legitimate
    ///     import from the folder next door and gives a reason that is not true.
    /// </remarks>
    static bool Inside(string path, string directory) {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return full.StartsWith(root, StringComparison.Ordinal)
            || string.Equals(full + Path.DirectorySeparatorChar, root, StringComparison.Ordinal);
    }

    /// <summary>Makes an empty asset of a kind in the selected folder and scans it in.</summary>
    /// <param name="kind">What to make.</param>
    /// <returns>The new asset, or <see langword="null" /> when the file could not be written.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Moved, not rewritten</b> — this was <c>EditorWorlds.CreateAsset</c>, the body behind
    ///         every Create ▸ line. What stayed behind is the half that is not this context's: opening
    ///         the new file, which the command does with the identity returned here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Scanned before it is returned, because a document is found by its GUID.</b> The file
    ///         has to be in the asset database for <c>IDocumentHost.TryOpen</c> to have an
    ///         identity to open it under — and without the sidecar the scan mints, a second scan would
    ///         give it a new one and every reference to it would dangle.
    ///     </para>
    /// </remarks>
    public AssetId? Create(NewAssetKind kind) {
        ArgumentNullException.ThrowIfNull(kind);

        var folder = "Assets";

        if (project.Selection.Count > 0 && project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            folder = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "Assets";
        }

        var directory = project.Paths.Absolute(folder);
        var path = Path.Combine(directory, kind.DefaultName + kind.Extension);
        var attempt = 2;

        while (File.Exists(path)) {
            path = Path.Combine(directory, $"{kind.DefaultName} {attempt++}{kind.Extension}");
        }

        try {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, kind.NewContents());
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            notify("Could not create the asset", NotificationSeverity.Error, exception.Message);

            return null;
        }

        project.Assets.Scan();
        Arrived?.Invoke();

        notify(Path.GetFileName(path) + " created", NotificationSeverity.Success, null);

        return project.Assets.TryGetByPath(project.Paths.Relative(path), out var created) ? created.Guid : null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One step and not a closure</b> — the opposite of <see cref="Closure" />, and on
    ///     purpose. Find References answers "who points at this" because a person is about to look at
    ///     the answer: open one, delete the lot, or see what they have in common.
    /// </remarks>
    public IReadOnlyList<AssetId> Referrers(IEnumerable<AssetId> assets) => Walk(assets, project.References.ReferrersOf);

    /// <inheritdoc />
    public IReadOnlyList<AssetId> Dependencies(IEnumerable<AssetId> assets) =>
        Walk(assets, asset => project.References.ReferencesFrom(asset).Select(reference => reference.Asset));

    /// <summary>Walks one edge of the reference graph from some assets.</summary>
    /// <param name="assets">Where to start.</param>
    /// <param name="edge">Which way to walk: what points at an asset, or what it points at.</param>
    /// <returns>What was reached, in the order found and each once.</returns>
    /// <remarks>
    ///     ⚠ <b>A set for membership and a list for order.</b> The two verbs differ only in which
    ///     direction they walk, and both can reach the same asset from several starting ones — a
    ///     linear <c>Contains</c> per hit is quadratic in the answer, which a select-all over a
    ///     heavily cross-referenced project is exactly the shape of.
    /// </remarks>
    static List<AssetId> Walk(IEnumerable<AssetId> assets, Func<AssetId, IEnumerable<AssetId>> edge) {
        ArgumentNullException.ThrowIfNull(assets);

        List<AssetId> found = [];
        HashSet<AssetId> seen = [];

        foreach (var asset in assets) {
            foreach (var reached in edge(asset)) {
                if (seen.Add(reached)) {
                    found.Add(reached);
                }
            }
        }

        return found;
    }

    /// <summary>Everything the selection reaches through the reference graph, itself included.</summary>
    /// <param name="roots">Where to start.</param>
    /// <returns>The closure, roots first and each asset once.</returns>
    /// <remarks>
    ///     ⚠ <b>A closure and not one step, and the difference is what a package is for.</b>
    ///     Select Dependencies walks one edge because a person is about to look at the
    ///     answer; an archive has to carry the material, the textures it names, and whatever those
    ///     name in turn, or it unpacks into a project with holes in it.
    ///     <para>
    ///         ⚠ <b>A reference cycle is ordinary here rather than exceptional.</b> Two prefabs that
    ///         name each other are a project people actually have, so the walk is over a visited set
    ///         and not a recursion with a depth limit.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<AssetId> Closure(IEnumerable<AssetId> roots) {
        ArgumentNullException.ThrowIfNull(roots);

        List<AssetId> found = [];
        HashSet<AssetId> seen = [];
        Queue<AssetId> pending = [];

        foreach (var root in roots) {
            if (seen.Add(root)) {
                found.Add(root);
                pending.Enqueue(root);
            }
        }

        while (pending.Count > 0) {
            foreach (var reference in project.References.ReferencesFrom(pending.Dequeue())) {
                if (seen.Add(reference.Asset)) {
                    found.Add(reference.Asset);
                    pending.Enqueue(reference.Asset);
                }
            }
        }

        return found;
    }

    /// <summary>Writes the assets into an archive, each under its project-relative path.</summary>
    /// <param name="path">The archive.</param>
    /// <param name="assets">What goes in.</param>
    /// <returns>How many were written, or <c>-1</c> when the archive could not be.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sidecar ships with the asset, and leaving it out would be the defect.</b> An
    ///         identity is in the <c>.meta</c> — a package unpacked without one is scanned into a
    ///         project that mints fresh GUIDs, so every reference inside the package points at
    ///         nothing and the material arrives white. That is exactly the failure the closure was
    ///         computed to avoid, arriving by the other door.
    ///     </para>
    ///     <para>
    ///         Folders carry no bytes, so they are skipped rather than written as empty entries: the
    ///         paths inside the archive already describe the shape of the tree.
    ///     </para>
    /// </remarks>
    public int ExportPackage(string path, IReadOnlyList<AssetId> assets) {
        ArgumentNullException.ThrowIfNull(assets);

        var written = 0;

        try {
            // ⚠ Truncated rather than opened. `ZipArchiveMode.Create` over an existing file appends
            // to whatever is in it, which for a second export to the same name is an archive with
            // two copies of every asset — and a reader that takes the first one.
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create)) {
                foreach (var asset in assets) {
                    if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
                        continue;
                    }

                    var absolute = project.Paths.Absolute(entry.Path);

                    if (!File.Exists(absolute)) {
                        continue;
                    }

                    archive.CreateEntryFromFile(absolute, entry.Path);

                    var meta = AssetMetaFile.PathFor(absolute);

                    if (File.Exists(meta)) {
                        archive.CreateEntryFromFile(meta, AssetMetaFile.PathFor(entry.Path));
                    }

                    written++;
                }
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            notify("Could not export", NotificationSeverity.Error, exception.Message);
            return -1;
        }

        notify(
            written == 1 ? "1 asset exported" : $"{written} assets exported",
            NotificationSeverity.Success,
            null
        );

        return written;
    }

    /// <summary>The project's collections, read through its own settings store.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked for rather than held.</b> <c>ProjectSettingsStore.Get</c> caches, so every
    ///     caller gets the same instance — but <c>Reload</c> and <c>Reset</c> replace it, and a field
    ///     here would be the detached object those two exist to warn about.
    /// </remarks>
    AssetCollections Store => project.Settings.Get<AssetCollections>();

    /// <inheritdoc />
    public IReadOnlyList<SavedAssetSet> Collections => Store.Collections;

    /// <inheritdoc />
    public void AddToCollection(string name, IReadOnlyList<AssetId> assets) {
        if (Store.Add(name, assets)) {
            WriteCollections();
        }
    }

    /// <inheritdoc />
    public void RemoveFromCollection(string name, IReadOnlyList<AssetId> assets) {
        if (Store.Remove(name, assets)) {
            WriteCollections();
        }
    }

    /// <inheritdoc />
    public void ForgetCollection(string name) {
        if (Store.Forget(name)) {
            WriteCollections();
        }
    }

    /// <summary>Writes the collections and tells whoever shows them.</summary>
    /// <remarks>
    ///     ⚠ <b>Written on every change rather than on the way out</b>, for the reason the
    ///     preferences are: closing the panel and closing the editor are both ways the change was
    ///     being lost, and nothing runs on the first of them.
    /// </remarks>
    void WriteCollections() {
        project.Settings.Save<AssetCollections>();
        CollectionsChanged?.Invoke();
    }

    /// <inheritdoc />
    public IReadOnlyList<SavedAssetFilter> Filters => filters();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A name that is already taken replaces rather than doubling the menu</b>, which is
    ///     <c>CurvePresetLibrary.Save</c>'s decision restated: somebody who saves "Textures"
    ///     twice has said what they mean by it, and two identical lines on a menu is the answer
    ///     nobody wants.
    /// </remarks>
    public void SaveFilter(string name, string query, string kind) {
        var saved = filters();

        saved.RemoveAll(filter => string.Equals(filter.Name, name, StringComparison.Ordinal));
        saved.Add(new SavedAssetFilter { Name = name, Search = query, Kind = kind });
        writeFilters();
    }

    /// <inheritdoc />
    public void ForgetFilter(string name) {
        if (filters().RemoveAll(saved => string.Equals(saved.Name, name, StringComparison.Ordinal)) > 0) {
            writeFilters();
        }
    }
}
