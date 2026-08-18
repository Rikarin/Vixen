// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
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
///         <b>Dirty has two sources.</b> The stack's own position covers the edits made in this
///         document. The other is a change that arrived from outside it — a global rename that
///         rewrote a reference this document holds — which no amount of undoing inside the document
///         can take back, so it is tracked separately and only <see cref="Save" /> clears it.
///     </para>
/// </remarks>
public abstract class EditorDocument {
    readonly Signal<bool> modifiedExternally = new(false);
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

    /// <summary>Whether it differs from what is on disk.</summary>
    public IReadOnlySignal<bool> IsDirty { get; }

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
        IsDirty = new Computed<bool>(() => Stack.IsDirty.Value || modifiedExternally.Value);

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
    }

    /// <summary>Whether this document knows how to read its file again.</summary>
    /// <remarks>
    ///     False here, and it is a real answer rather than a placeholder. Most documents were written
    ///     to be read once, in a constructor, and re-reading them means rebuilding whatever the
    ///     constructor built — so a base class that claimed every document could would be one whose
    ///     <see cref="Reload" /> silently did nothing for most of them.
    /// </remarks>
    public virtual bool CanReload => false;

    /// <summary>Reads the file again, discarding what was in memory, and forgets the history over it.</summary>
    /// <returns>Whether it did, which is <see cref="CanReload" />.</returns>
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
    ///         they name is how a reload turns into a corruption. <see cref="CommandStack.Clear" />
    ///         keeps the dirty flag, which is why the caller's job is to reload only what is clean:
    ///         see the policy on <c>ExternalEdits</c>, which is the one thing here that is a decision
    ///         rather than a mechanism.
    ///     </para>
    /// </remarks>
    public bool Reload() {
        if (!ReloadCore()) {
            return false;
        }

        Stack.Clear();
        modifiedExternally.Value = false;

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

    internal void MarkClosed() => IsOpen = false;

    /// <summary>
    ///     Something outside this document's stack changed it. Its redo entries were recorded against
    ///     a state that no longer exists, so they go; the dirty flag stays until a save.
    /// </summary>
    internal void MarkModifiedExternally() {
        modifiedExternally.Value = true;
        Stack.ClearRedo();
    }
}
