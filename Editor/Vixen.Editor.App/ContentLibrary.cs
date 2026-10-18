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
    public ContentLibrary(
        EditorProject project,
        Action<string, NotificationSeverity, string?> notify,
        Func<List<SavedAssetFilter>> filters,
        Action writeFilters,
        Action import
    ) {
        this.project = project;
        this.notify = notify;
        this.filters = filters;
        this.writeFilters = writeFilters;
        this.import = import;
    }

    /// <inheritdoc />
    public EditorProject Project => project;

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
