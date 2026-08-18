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
    /// <returns>What the scan did, or an empty report if the cached index was good enough to use.</returns>
    /// <remarks>
    ///     The cached index is used when it loads and is not stale, because rescanning a
    ///     hundred-thousand-asset project on every launch is the cost that makes an editor feel slow
    ///     to start. The reference index is rebuilt either way: it is not persisted, since it is a
    ///     grep over content that the working tree may have changed while the editor was closed.
    /// </remarks>
    public ScanReport Open(ScanOptions? options = null) {
        var report = new ScanReport(0, TimeSpan.Zero, []);

        if (!Assets.TryLoad() || Assets.IsStale()) {
            report = Assets.Scan(options);
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
