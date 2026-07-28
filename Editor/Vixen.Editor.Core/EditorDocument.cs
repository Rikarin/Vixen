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

    /// <summary>Writes it back and records that this is now what is on disk.</summary>
    public void Save() {
        SaveCore();
        Stack.MarkClean();
        modifiedExternally.Value = false;
    }

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
