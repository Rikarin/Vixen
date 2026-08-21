// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Core;

/// <summary>An open project: its assets, its documents, its selection, and its global undo history.</summary>
/// <remarks>
///     <para>
///         Constructing one is cheap and touches no disk — it names the directories and builds the
///         empty models. <see cref="Open" /> is what reads the project, so a shell can put a window on
///         screen and then load into it rather than the other way round.
///     </para>
///     <para>
///         <b>The global stack is for what is not inside any one document.</b> Renaming an asset,
///         moving a file, deleting a folder: operations whose effects land in documents that may not
///         even be open. Its interaction with the per-document stacks is documented on
///         <see cref="CommandStack" /> and enforced by <see cref="EditorContext.Touch" />.
///     </para>
/// </remarks>
public sealed class EditorProject {
    readonly CollectionSignal<EditorDocument> documents = new();
    readonly Signal<EditorDocument?> activeDocument = new(null);

    /// <summary>Where the project's directories are.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>What the project is called: the name of its root directory.</summary>
    public string Name { get; }

    /// <summary>Every asset, by GUID and by path.</summary>
    public AssetDatabase Assets { get; }

    /// <summary>Who refers to what.</summary>
    public ReferenceIndex References { get; }

    /// <summary>The settings assets under <c>ProjectSettings/</c>.</summary>
    public ProjectSettingsStore Settings { get; }

    /// <summary>What is selected in the project browser.</summary>
    public Selection<AssetId> Selection { get; } = new();

    /// <summary>What commands on the global stack are handed.</summary>
    public EditorContext Context { get; }

    /// <summary>The undo history for operations that are not inside one document.</summary>
    public CommandStack GlobalStack { get; }

    /// <summary>What is open, in the order it was opened.</summary>
    public IReadOnlyList<EditorDocument> Documents => documents;

    /// <summary>Which document has focus, or <see langword="null" /> if none.</summary>
    public IReadOnlySignal<EditorDocument?> ActiveDocument => activeDocument;

    /// <summary>Whether any open document differs from disk.</summary>
    /// <remarks>What the shell asks before it closes the window.</remarks>
    public IReadOnlySignal<bool> HasUnsavedChanges { get; }

    /// <summary>Raised by an open document just before it writes itself back.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Before the write, which is the entire reason it is here rather than on the
    ///         document.</b> <see cref="EditorDocument.Saved" /> already says "this document has been
    ///         written" and is the hook a sidecar writes itself from. What this says is "a path is
    ///         about to change", and the only subscriber that needs it is one holding a file watcher:
    ///         <c>IFileWatcher.Suppress</c> has to be called before the platform sees the write, or
    ///         the editor's own save arrives back through the watcher as somebody else's edit and the
    ///         document offers to reload itself over the work that was just saved.
    ///     </para>
    ///     <para>
    ///         On the project because the watcher is the project's, not the document's — a document
    ///         raising it would be one that has to be handed a watcher to construct, for a
    ///         relationship it otherwise has no part in. <see cref="ExternalEdits" /> is what
    ///         subscribes, and it is the only thing that should.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It throws through</b>, like <see cref="EditorDocument.Saved" />: a subscriber that
    ///         failed would leave the watcher un-suppressed, and a save that then round-trips is
    ///         better noticed than swallowed.
    ///     </para>
    /// </remarks>
    public event Action<EditorDocument>? DocumentSaving;

    /// <summary>Names a project's directories and builds the empty models over them.</summary>
    /// <param name="paths">Where the project is.</param>
    public EditorProject(ProjectPaths paths) {
        ArgumentNullException.ThrowIfNull(paths);

        Paths = paths;
        Name = Path.GetFileName(paths.Root.TrimEnd(Path.DirectorySeparatorChar, '/'));
        Assets = new(paths);
        References = new();
        Settings = new(paths);
        Context = new(this, null);
        GlobalStack = new(Context);

        // Reads every open document's dirty flag, so the dependency set changes as documents come and
        // go — which is exactly what a computed's run-to-discover dependency tracking is for.
        HasUnsavedChanges = new Computed<bool>(() => {
                for (var index = 0; index < documents.Count; index++) {
                    if (documents[index].IsDirty.Value) {
                        return true;
                    }
                }

                return false;
            }
        );
    }

    /// <summary>Reads the project: the GUID index, repaired if it needs it, and the reference index.</summary>
    /// <param name="options">What to repair, or <see langword="null" /> to repair everything.</param>
    /// <returns>What the scan did, including how much of the cached index it was able to keep.</returns>
    /// <remarks>
    ///     <para>
    ///         The cached index is loaded first and then scanned <em>through</em>, not instead of:
    ///         the scan keeps every asset whose sidecar is still the size and age the index recorded
    ///         and reads the rest, so a launch after one file changed costs one directory walk and
    ///         one file rather than a hundred thousand. Rescanning everything on every launch is the
    ///         cost that makes an editor feel slow to start, and a whole-database freshness check
    ///         only moved that cost to every launch where anything at all had changed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The index is written back only when it differs from what was loaded</b> — a
    ///         launch that changed nothing does no disk write at all, and a launch that changed one
    ///         asset rewrites the file, because the index is small and a partial rewrite of it is a
    ///         far worse thing to have to reason about than a full one.
    ///     </para>
    ///     <para>
    ///         The reference index is rebuilt either way: it is not persisted, since it is a grep
    ///         over content that the working tree may have changed while the editor was closed.
    ///     </para>
    /// </remarks>
    public ScanReport Open(ScanOptions? options = null) {
        var loaded = Assets.TryLoad() ? Assets.Count : -1;
        var report = Assets.Scan(options);

        // Everything reused and the same number of them as were loaded means the file on disk already
        // says exactly this. Anything else — an asset added, removed, or changed — and it does not. A
        // count that matches while the set does not still leaves the newcomer unreused.
        //
        // ⚠ And any issue at all, because a repair changes the index without reading anything: two
        // reused entries can be found to claim one GUID, and the re-GUID that settles it leaves a
        // report saying nothing was rescanned over an index that no longer matches the disk.
        if (loaded != report.Assets || report.Rescanned != 0 || report.Issues.Count != 0) {
            Assets.Save();
        }

        References.Build(Assets);
        return report;
    }

    /// <summary>Finds an open document by the asset it edits.</summary>
    /// <param name="asset">The asset.</param>
    /// <param name="document">The document editing it.</param>
    /// <returns>Whether one is open.</returns>
    /// <remarks>
    ///     What "open this asset" checks first, so double-clicking a scene twice focuses the tab
    ///     rather than opening a second one with its own undo history over the same file.
    /// </remarks>
    public bool TryGetDocument(AssetId asset, [MaybeNullWhen(false)] out EditorDocument document) {
        for (var index = 0; index < documents.Count; index++) {
            if (documents[index].Asset == asset && !asset.IsEmpty) {
                document = documents[index];
                return true;
            }
        }

        document = null;
        return false;
    }

    /// <summary>Gives a document focus.</summary>
    /// <param name="document">The document, or <see langword="null" /> for none.</param>
    public void Activate(EditorDocument? document) {
        if (document is not null && !documents.Contains(document)) {
            throw new InvalidOperationException($"'{document.Title.Peek()}' is not open in this project.");
        }

        activeDocument.Value = document;
    }

    /// <summary>Closes a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether it was open.</returns>
    /// <remarks>
    ///     Unsaved changes are not checked. Prompting is a decision only the shell can make, and a
    ///     model that refused would make "discard and close" impossible to express.
    /// </remarks>
    public bool Close(EditorDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (!documents.Remove(document)) {
            return false;
        }

        document.MarkClosed();

        if (ReferenceEquals(activeDocument.Peek(), document)) {
            activeDocument.Value = documents.Count > 0 ? documents[documents.Count - 1] : null;
        }

        document.OnClosed();
        return true;
    }

    /// <summary>Saves every open document that differs from disk.</summary>
    /// <returns>How many were written.</returns>
    public int SaveAll() {
        var saved = 0;

        // Over a snapshot: a document's Save may open or close another one, and a save that
        // reorganised the list underneath the loop would skip whatever moved into the slot it left.
        foreach (var document in documents.ToArray()) {
            if (document.IsDirty.Value) {
                document.Save();
                saved++;
            }
        }

        return saved;
    }

    internal void OnDocumentSaving(EditorDocument document) => DocumentSaving?.Invoke(document);

    internal void Register(EditorDocument document) {
        documents.Add(document);
        activeDocument.Value = document;
    }
}
