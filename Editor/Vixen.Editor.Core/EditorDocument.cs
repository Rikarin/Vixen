// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ui;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Core;

/// <summary>One thing open for editing, with its own undo history.</summary>
/// <remarks>
///     <para>
///         A scene, a material, a prefab, a settings asset. What it holds and how it writes itself
///         back are the deriving type's business; what is here is the part every open document has —
///         a title, an undo stack, and an honest answer to whether it differs from disk.
///     </para>
///     <para>
///         <b>Unsaved has two sources, and since #656 they are two signals.</b> <see cref="IsDirty" />
///         is the stack's own position — the edits made in this document, which undoing back to the
///         save point takes away. <see cref="ExternallyModified" /> is a change that arrived from
///         outside it — a global rename that rewrote a reference this document holds — which no
///         amount of undoing inside the document can take back. <see cref="HasUnsavedChanges" /> is
///         the two together, and it is what Save All, the project's close question and the tab
///         markers read. <see cref="Save" /> and <see cref="Reload" /> clear both.
///     </para>
///     <para>
///         <b><see cref="IsStale" /> is the opposite question and is deliberately not part of that
///         answer.</b> Dirty is memory ahead of disk; stale is disk ahead of memory. Folding the two
///         together would make <see cref="EditorProject.SaveAll" /> write this document over the
///         edit somebody else made to its file. <see cref="ExternalEdits" /> is what sets it.
///     </para>
///     <para>
///         <b>It is <c>Vixen.Ui</c>'s <see cref="IEditableDocument" /> (#656), explicitly</b>, so a
///         panel hosting one answers <c>document.save</c>, its tab can be asked about on close and a
///         window title can follow it — the framework surfaces, with no editor copy of each.
///         <c>Name</c> is <see cref="Title" />; <c>Revert</c> is <see cref="Reload" />, including the
///         promise that <see langword="false" /> leaves the document dirty and covers both "cannot"
///         and "tried and declined".
///     </para>
///     <para>
///         ⚠ <b>A failed save is reported, not swallowed</b> — the decision on #656.
///         <see cref="Save" /> keeps its void-and-throw contract, because "a subscriber that cannot
///         write its file is a failed save, and the caller's own error handling is what turns that
///         into a notification". The interface's <see langword="bool" /> is reached by catching a
///         write failure, handing it to <see cref="EditorProject.SaveFailed" /> and answering
///         <see langword="false" />; with nobody subscribed it is thrown on, because a failure with
///         nobody to hear it is exactly the swallow that remark forbids.
///     </para>
///     <para>
///         ⚠ <b>The location is the asset database's path, so a document with no asset has
///         none.</b> <see cref="Asset" /> is immutable, and <c>EditorApplication</c> opens its scenes
///         as <c>new SceneDocument(project, world, AssetId.Empty, …)</c> with the real path held by a
///         <c>Writer</c> — so those report <c>Location</c> null, which in
///         <see cref="IEditableDocument" />'s vocabulary means "never saved". Nothing reads
///         <c>Location</c> yet (no recent-documents list, no proxy icon), which is why that is
///         recorded rather than invented.
///     </para>
///     <para>
///         ⚠ <b>And <see cref="IsStale" /> is not <see cref="ExternallyModified" /></b>: stale is
///         disk ahead of memory, which saving would overwrite; externally modified is memory ahead of
///         disk, which saving writes.
///     </para>
/// </remarks>
public abstract class EditorDocument : IEditableDocument {
    readonly Signal<string?> location = new(null);
    readonly Signal<bool> modifiedExternally = new(false);
    readonly Signal<bool> stale = new(false);
    readonly Signal<string> title;

    /// <summary>The project it belongs to.</summary>
    public EditorProject Project { get; }

    /// <summary>What commands on this document's stack are handed.</summary>
    public EditorContext Context { get; }

    /// <summary>This document's undo history.</summary>
    public CommandStack Stack { get; }

    /// <summary>The asset it edits, or <see cref="AssetId.Empty" /> for one not saved anywhere yet.</summary>
    public AssetId Asset { get; }

    /// <summary>What the tab says.</summary>
    public IReadOnlySignal<string> Title => title;

    /// <summary>Whether it has edits of its own that are not on disk: the stack's position.</summary>
    /// <remarks>
    ///     ⚠ <b>Only this document's own edits since #656.</b> A change that reached it from
    ///     outside is <see cref="ExternallyModified" />, and "differs from disk at all" is
    ///     <see cref="HasUnsavedChanges" />.
    /// </remarks>
    public IReadOnlySignal<bool> IsDirty => Stack.IsDirty;

    /// <summary>Whether a command on another stack changed it — a global rename that rewrote a reference in it.</summary>
    /// <remarks>
    ///     No undo inside this document takes it back, so walking the history to the save point
    ///     leaves it set; <see cref="Save" /> and <see cref="Reload" /> clear it.
    /// </remarks>
    public IReadOnlySignal<bool> ExternallyModified => modifiedExternally;

    /// <summary>Whether it differs from what is on disk for either reason: <see cref="IsDirty" /> or <see cref="ExternallyModified" />.</summary>
    /// <remarks>What saving would write, so what Save All writes and what the close prompt asks about.</remarks>
    public IReadOnlySignal<bool> HasUnsavedChanges { get; }

    /// <inheritdoc />
    IReadOnlySignal<string> IEditableDocument.Name => title;

    /// <inheritdoc />
    IReadOnlySignal<string?> IEditableDocument.Location => location;

    /// <summary>Whether its file changed underneath it and it has not caught up.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The other direction from <see cref="IsDirty" />, and deliberately not folded into
    ///         it.</b> Dirty means memory is ahead of disk; this means disk is ahead of memory. A
    ///         clean document that goes stale is reloaded and this never surfaces; a dirty one is
    ///         left alone, and then both are true at once and mean different things.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why it is a signal of its own rather than a second source of dirty.</b>
    ///         <see cref="EditorProject.SaveAll" /> writes every dirty document — so a stale document
    ///         that counted as dirty would have the editor's copy written over the external edit by a
    ///         Ctrl+Shift+S, which is the destructive outcome the whole policy exists to avoid.
    ///     </para>
    ///     <para>
    ///         Cleared by <see cref="Reload" />, which takes the file's version, and by
    ///         <see cref="Save" />, which takes the document's. Those are the only two answers, and a
    ///         person choosing between them is the affordance <see cref="ExternalEdits" /> reports for.
    ///     </para>
    /// </remarks>
    public IReadOnlySignal<bool> IsStale => stale;

    /// <summary>Whether it is still open in the project.</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>Opens a document in a project.</summary>
    /// <param name="project">The project.</param>
    /// <param name="asset">The asset it edits, or <see cref="AssetId.Empty" />.</param>
    /// <param name="title">What the tab says.</param>
    protected EditorDocument(EditorProject project, AssetId asset, string title) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(title);

        Project = project;
        Asset = asset;
        this.title = new(title);
        Context = new(project, this);
        Stack = new(Context);

        // ⚠ Both read every time, never short-circuited, so the computed depends on both however
        // the first one answers.
        HasUnsavedChanges = new Computed<bool>(() => Stack.IsDirty.Value | modifiedExternally.Value);
        location.Value = Locate();

        // Registering from the base constructor means every document is in the project by
        // construction rather than by whoever created it remembering to add it. Nothing virtual is
        // called and the project only stores the reference, so the half-built instance is not used.
        project.Register(this);
    }

    /// <summary>Changes what the tab says.</summary>
    /// <param name="value">The new title.</param>
    /// <remarks>Not itself an edit: renaming the asset is, and that is a command on the global stack.</remarks>
    public void SetTitle(string value) {
        ArgumentException.ThrowIfNullOrEmpty(value);
        title.Value = value;
    }

    /// <summary>Raised after the document has written itself back, before it is marked clean.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a feature with files <i>beside</i> the document subscribes to.</b> A scene
    ///         names a heightfield and a foliage file next to itself; saving the scene without them
    ///         leaves a project in which the ground it was saved with only exists in a process that
    ///         has since exited. Before this, the application called into the terrain code by hand,
    ///         which is a line the application had to know to write — and a second feature with
    ///         sidecars would have been a second line.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It throws through.</b> A subscriber that cannot write its file is a failed save,
    ///         and the caller's own error handling is what turns that into a notification. Swallowing
    ///         it here would report a save that half happened.
    ///     </para>
    /// </remarks>
    public event Action<EditorDocument>? Saved;

    /// <summary>Writes it back and records that this is now what is on disk.</summary>
    public void Save() {
        // ⚠ Announced *before* the write, not after, and that is the whole point of the project
        // raising it: a file watcher is told to ignore a path it is about to see change, so the
        // announcement has to beat the write to the disk. `EditorProject.DocumentSaving` says the
        // rest.
        Project.OnDocumentSaving(this);

        SaveCore();
        Saved?.Invoke(this);
        Stack.MarkClean();
        modifiedExternally.Value = false;

        // The file is now this document's contents, whatever it held a moment ago. A save is the
        // other half of the answer to a stale document — "keep mine" — and it is the same two lines
        // as any other save, which is what makes it an answer rather than a mode.
        stale.Value = false;

        // A rename since it was opened moved the file; the save is the moment the path is asked again.
        location.Value = Locate();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Reports a failed write and answers <see langword="false" /></b>; see the class remarks
    ///     for why this is the one place the throw becomes a <see langword="bool" />. Only the
    ///     failures a disk produces are caught — a bug in a <see cref="SaveCore" /> is not a full
    ///     disk and still throws.
    /// </remarks>
    bool IEditableDocument.Save() {
        try {
            Save();
            return true;
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            if (!Project.ReportSaveFailed(this, failure)) {
                throw;
            }

            return false;
        }
    }

    /// <inheritdoc />
    bool IEditableDocument.Revert() => Reload();

    string? Locate() =>
        !Asset.IsEmpty && Project.Assets.TryGetByGuid(Asset, out var entry) && !entry.IsFolder
            ? Project.Paths.Absolute(entry.Path)
            : null;

    /// <summary>Whether this document knows how to read its file again.</summary>
    /// <remarks>
    ///     False here, and it is a real answer rather than a placeholder. Most documents were written
    ///     to be read once, in a constructor, and re-reading them means rebuilding whatever the
    ///     constructor built — so a base class that claimed every document could would be one whose
    ///     <see cref="Reload" /> silently did nothing for most of them.
    /// </remarks>
    public virtual bool CanReload => false;

    /// <summary>Reads the file again, discarding what was in memory, and forgets the history over it.</summary>
    /// <returns>
    ///     Whether it did. False for a document that <see cref="CanReload" /> says no to, and also
    ///     for one that tried and declined — a file that will not parse, which an override is
    ///     encouraged to refuse rather than to blank itself over.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <b>What an edit made outside the editor arrives through.</b> The asset database, the
    ///         project browser and the build panel have always followed the watcher; an open document
    ///         did not, so a <c>.vxcompositor</c> edited in a text editor beside the running editor
    ///         changed everything except the thing that was open on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The undo history goes, and it has to.</b> The entries describe edits to the
    ///         previous contents of the file — undoing into a document that no longer has the members
    ///         they name is how a reload turns into a corruption.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And then it is clean, which <see cref="CommandStack.Clear" /> alone does not
    ///         make it.</b> Clearing a dirty stack leaves it dirty for good — correctly, because the
    ///         undos that would have got back to disk have just been deleted. That is the wrong
    ///         answer here and only here: what this document holds <i>is</i> the file, so
    ///         <see cref="CommandStack.MarkClean" /> is what stops a discard-and-reload from leaving
    ///         a document that claims to differ from a file it is identical to. Deciding
    ///         <i>whether</i> to discard is not this method's job — see <see cref="ExternalEdits" />,
    ///         which is the one thing in this seam that is a policy rather than a mechanism.
    ///     </para>
    /// </remarks>
    public bool Reload() {
        if (!ReloadCore()) {
            return false;
        }

        Stack.Clear();
        Stack.MarkClean();
        modifiedExternally.Value = false;
        stale.Value = false;

        return true;
    }

    /// <summary>Reads the file again. Overridden by a document that knows how.</summary>
    /// <returns>Whether it did.</returns>
    /// <remarks>
    ///     An override must also say <see cref="CanReload" />, because a caller deciding whether to
    ///     reload has to be able to ask before it does — a prompt that offers to discard somebody's
    ///     edits for a document that would then decline is worse than not offering.
    /// </remarks>
    protected virtual bool ReloadCore() => false;

    /// <summary>Closes it, which is <see cref="EditorProject.Close" /> on this document.</summary>
    /// <returns>Whether it was open.</returns>
    /// <remarks>
    ///     Unsaved changes are not its problem. Whether to prompt, discard or save is a question for
    ///     the shell, which is the only layer that can ask a person.
    /// </remarks>
    public bool Close() => Project.Close(this);

    /// <summary>Writes the document back to wherever it came from.</summary>
    protected abstract void SaveCore();

    /// <summary>Called once when the document leaves the project.</summary>
    protected internal virtual void OnClosed() {
    }

    /// <summary>Something in the project changed on disk, whoever it belongs to.</summary>
    /// <param name="path">
    ///     What changed, project-relative — or <see langword="null" /> when the watcher lost events
    ///     and any file may have.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>For a document that depends on a file other than its own.</b>
    ///         <see cref="ExternalEdits" /> routes a change to the document that <em>owns</em> that
    ///         path, which is the whole of what a reload needs; this is the other question, and until
    ///         <a href="https://github.com/Rikarin/Vixen/issues/922">#922</a> nothing asked it. A
    ///         texture graph inlines the compounds in <c>Assets/Compounds</c>, so a compound edited
    ///         by a <c>git checkout</c> or a text editor left every containing graph inlining the
    ///         version that was on disk when it opened — old ports, old defaults, and a bake made
    ///         from them with nothing saying so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A notification and not a reload.</b> What a document does with it is its own —
    ///         mark something stale, drop a cache, rebuild a menu — and the default is nothing at
    ///         all. Re-reading the document's own file is <see cref="Reload" />'s job and is decided
    ///         by <see cref="ExternalEdits" />'s policy, which weighs unsaved edits; this carries no
    ///         such decision because it is not about this document's contents.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Deletions included, unlike the reload path.</b> A file that has gone must not be
    ///         re-read — it would read back as empty — but a compound that has gone must leave the
    ///         menu, and an override that only heard about writes would keep offering it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Called on the frame, once per drained change per open document.</b> An override
    ///         that walks a directory or reads a file makes an external program's Ctrl+S cost the
    ///         editor a frame; the shape that works is a flag set here and read where the work
    ///         already happens.
    ///     </para>
    /// </remarks>
    protected internal virtual void OnProjectFileChanged(string? path) {
    }

    internal void MarkClosed() => IsOpen = false;

    /// <summary>
    ///     Something outside this document's stack changed it. Its redo entries were recorded against
    ///     a state that no longer exists, so they go; the dirty flag stays until a save.
    /// </summary>
    internal void MarkModifiedExternally() {
        modifiedExternally.Value = true;
        Stack.ClearRedo();
    }

    /// <summary>
    ///     Its file changed underneath it and it did not take the change. Nothing in memory moves —
    ///     the stack, the redo entries and the contents are all still about a document somebody may
    ///     be mid-edit in — and the flag stands until a <see cref="Reload" /> or a <see cref="Save" />
    ///     settles which copy wins.
    /// </summary>
    internal void MarkStale() => stale.Value = true;
}
