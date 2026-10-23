// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The one question in front of a verb that throws edits away, and Revert to Saved, which is such a verb.</summary>
/// <remarks>
///     <b>Out of <c>EditorParity.cs</c> (#1296).</b> <c>Confirm</c> and the two <c>Revert</c>s were the
///     application's private methods, and all they used of it was the shell's dialogs and notifications.
///     New and Open Scene ask through <see cref="Confirm" /> from <c>SceneFileQuestions</c>; File ▸ Revert
///     and <c>document.revert</c> are <c>WorkspaceModule</c>'s lines over <see cref="Revert" />; and a tab's
///     <c>EditorWorkspace.Reverting</c> hook, which the composition root sets, calls the same method.
/// </remarks>
static class Discarding {
    /// <summary>Runs something, asking first if there is unsaved work to lose.</summary>
    /// <param name="shell">The shell, whose dialog asks.</param>
    /// <param name="ask">Whether there is anything to lose; when not, it runs at once.</param>
    /// <param name="title">The question.</param>
    /// <param name="message">What is lost.</param>
    /// <param name="then">What to run on a yes.</param>
    /// <param name="confirm">The yes button's label.</param>
    public static void Confirm(EditorShell shell, bool ask, string title, string message, Action then, string confirm) {
        if (!ask) {
            then();
            return;
        }

        _ = Run();

        async Task Run() {
            if (await shell.Dialogs.ConfirmAsync(title, message, confirm, danger: true).ConfigureAwait(true)) {
                then();
            }
        }
    }

    /// <summary>Whether a document can be reverted: it can be read again, and something would change.</summary>
    /// <param name="document">The document, or <see langword="null" />.</param>
    /// <returns>Whether File ▸ Revert means anything for it.</returns>
    public static bool CanRevert(EditorDocument? document) =>
        document is { CanReload: true } && (document.HasUnsavedChanges.Value || document.IsStale.Value);

    /// <summary>Reads one document's file again, asking first when that throws edits away.</summary>
    /// <param name="shell">The shell, whose dialog asks and whose notifications report.</param>
    /// <param name="document">The document.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The other half of the stale-document answer, as a gesture.</b>
    ///         <c>ExternalEdits</c> declines to reload a document with unsaved edits and says so;
    ///         Ctrl+S is how a person keeps theirs, and this is how they take the file's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It asks, and only when there is something to lose.</b> A revert of a document whose
    ///         file merely moved on discards nothing and is not worth a modal; a revert of one with
    ///         unsaved edits discards the only copy of them. That is also why it is offered for a
    ///         merely-dirty document with no external edit at all: "put it back the way it was saved".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What an asset tab answers <c>document.revert</c> with (#1494)</b> — the workspace's
    ///         <c>Reverting</c> — in place of the document's own revert, which does not ask.
    ///     </para>
    /// </remarks>
    public static void Revert(EditorShell shell, EditorDocument document) {
        if (!document.CanReload) {
            return;
        }

        var title = document.Title.Peek();

        Confirm(
            shell,
            document.HasUnsavedChanges.Value,
            $"Revert '{title}'?",
            "Its unsaved changes and its undo history are discarded, and the file on disk is read "
            + "again. This cannot be undone.",
            () => {
                if (document.Reload()) {
                    return;
                }

                shell.Notifications.Show(
                    $"'{title}' could not be read again",
                    NotificationSeverity.Warning,
                    "What is on screen is what was there before."
                );
            },
            "Revert"
        );
    }
}
