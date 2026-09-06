// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>The undo manager a control finds in the editor: whichever document is active right now.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An indirection rather than a subscription, and that is the whole of what four
///         sessions called "a feature, not a line".</b> The problem they named is real —
///         <c>UiDocument.UndoManager</c> is one slot and the active document changes as the user
///         switches tabs — but the answer is not to push a new stack into the slot on every change.
///         It is to put something in the slot that answers the question when it is asked.
///         <c>UndoCommands</c> already argues exactly this for its own lookup: "the manager is looked
///         up on every ask rather than captured", because a captured one is the stack that happened
///         to be there when the window opened.
///     </para>
///     <para>
///         ⚠ <b><c>Peek</c> and not <c>Value</c>.</b> <c>TextField.Record</c> runs from inside an
///         edit, which can be inside a reactive flush; reading <c>ActiveDocument</c> as a signal
///         there would make every text box in the editor a dependency of which document is open, and
///         the churn would land as a rebuild nobody asked for. Which document is active is a fact to
///         read, not a graph edge — the editor polls its own history for the same reason
///         (<c>FollowHistory</c>).
///     </para>
///     <para>
///         <b>The fallback is the project's global stack rather than nothing.</b> A field edited
///         while no document is active — a project setting, an asset inspector — belongs on the same
///         history as the commands those panels run, which is what <c>EditorProject.GlobalStack</c>
///         already holds. Answering <see langword="null" /> there would leave the field with no ⌘Z at
///         all, which is the state this class exists to end.
///     </para>
///     <para>
///         ⚠ <b>Every member forwards through <see cref="IUndoManager" /> and not through
///         <see cref="CommandStack" />'s own names.</b> <c>CommandStack</c> implements the interface
///         explicitly — <c>bool IUndoManager.CanUndo => CanUndo.Value</c> — so its signal-typed
///         properties and its interface members are different members with the same names, and
///         forwarding to the wrong one does not compile rather than quietly returning a signal.
///     </para>
/// </remarks>
sealed class ActiveDocumentUndo(EditorProject project) : IUndoManager {
    IUndoManager Current => project.ActiveDocument.Peek()?.Stack ?? project.GlobalStack;

    /// <inheritdoc />
    public bool CanUndo => Current.CanUndo;

    /// <inheritdoc />
    public bool CanRedo => Current.CanRedo;

    /// <inheritdoc />
    public bool IsPerforming => Current.IsPerforming;

    /// <inheritdoc />
    public void Register(string name, Action undo, Action redo) => Current.Register(name, undo, redo);

    /// <inheritdoc />
    public bool Undo() => Current.Undo();

    /// <inheritdoc />
    public bool Redo() => Current.Redo();
}
