// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>The project's settings as a document: edits through the editing pipeline, with an undo history of their own.</summary>
/// <remarks>
///     <para>
///         <b>#1310, decision (a).</b> A settings window's rows wrote straight into the settings
///         object — no document, so no undo — and kept a dirty flag of their own beside the store's,
///         which was a second vocabulary for "unsaved" the project's close question could not see.
///         The window's inspector now edits through this document: every change is a command on
///         <see cref="EditorDocument.Stack" />, <see cref="EditorDocument.HasUnsavedChanges" /> is the
///         one answer the window's Apply, Save All and the close prompt read, and undoing back to
///         where it was saved is clean again.
///     </para>
///     <para>
///         ⚠ <b>Its own stack, reached only from inside the settings window.</b> It never becomes
///         <see cref="EditorProject.ActiveDocument" /> — that is what Ctrl+Z reaches when nothing
///         nearer the focus owns a history, so a settings document that took it would be the
///         viewport's Ctrl+Z quietly undoing a project setting, which is the hazard the window's own
///         remark refused undo over. The window hosts this stack as its undo manager instead, so
///         Ctrl+Z there undoes a setting and anywhere else never does.
///     </para>
///     <para>
///         ⚠ <b>A write that did not go through the stack is a change from outside it</b> — a
///         category's Reset, or a plugin that writes a settings object and calls
///         <see cref="ProjectSettingsStore.MarkChanged{T}" />. It is
///         <see cref="EditorDocument.ExternallyModified" />: saved by Save, asked about on close, and
///         not taken back by an undo, because nothing recorded how.
///     </para>
/// </remarks>
public sealed class SettingsDocument : EditorDocument {
    readonly HashSet<Type> touched = [];

    /// <summary>Opens the project's settings as a document, without taking the focus.</summary>
    /// <param name="project">The project.</param>
    /// <param name="title">What the window and the close prompt call it.</param>
    public SettingsDocument(EditorProject project, string title)
        : base(project, AssetId.Empty, title, activate: false) {
        Store = project.Settings;
        Store.Marked += OnMarked;

        // Marks made before the window was first built are already pending in the store.
        if (Store.HasUnsavedChanges) {
            MarkModifiedExternally();
        }
    }

    /// <summary>The store this document writes.</summary>
    public ProjectSettingsStore Store { get; }

    /// <inheritdoc />
    /// <remarks>Reloading is the store's <see cref="ProjectSettingsStore.Reload" />, which every settings type supports.</remarks>
    public override bool CanReload => true;

    /// <summary>Says an edit through this document's stack reached a settings type, so a save writes it.</summary>
    /// <param name="type">The settings type the edited object is.</param>
    /// <remarks>
    ///     Not a dirty mark — the stack already is one, and a second would survive the undo that
    ///     cleaned the first. It is the set of files a save has to write, which the stack does not
    ///     know because a command records members rather than files.
    /// </remarks>
    public void Record(Type type) {
        ArgumentNullException.ThrowIfNull(type);
        touched.Add(type);
    }

    /// <inheritdoc />
    protected override void SaveCore() {
        foreach (var type in touched) {
            Store.Include(type);
        }

        Store.SaveAll();
        touched.Clear();
    }

    /// <inheritdoc />
    protected override bool ReloadCore() {
        Store.Reload();
        touched.Clear();

        return true;
    }

    /// <inheritdoc />
    protected internal override void OnClosed() => Store.Marked -= OnMarked;

    void OnMarked() => MarkModifiedExternally();
}
