// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Platform;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>What is left of the project on the application: the reopen answer and the start-up door.</summary>
/// <remarks>
///     The chooser and doc 20's A2 reasoning are <see cref="ProjectChooser" />'s (#1296); the session
///     is <see cref="IProjectSession" />. What stays is the host's read of where to reopen, and the
///     picker helper the settings window's two path rows share.
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>Where the editor should reopen, or <see langword="null" /> to stay closed.</summary>
    /// <remarks>
    ///     Read by the host after <see cref="RequestClose" /> has done its work — the reopen loop is the
    ///     composition root's. The answer is <see cref="IProjectSession.PendingProject" />'s.
    /// </remarks>
    public string? PendingProject => session.PendingProject;

    /// <summary>The project service — doc 50 § P2d. Published to plugins under <see cref="IProjectSession" />.</summary>
    internal IProjectSession Session => session;

    /// <summary>The startup Project Browser: recent projects, Browse, and New.</summary>
    /// <remarks>
    ///     The dialog is <see cref="ProjectChooser" />'s (#1296) — File ▸ New and Open Project are
    ///     <c>WorkspaceModule</c>'s over another one — and this is the start-up latch's door onto it,
    ///     public because a host opens it.
    /// </remarks>
    public void ShowProjectBrowser() => new ProjectChooser(Shell, session, services.Dialogs, deferred, dataDirectory).Show();

    /// <summary>Asks the platform something that answers with a path, and acts on the answer.</summary>
    /// <param name="ask">Which picker to open.</param>
    /// <param name="then">What to do with the path, which is only called when there is one.</param>
    /// <param name="failed">What to say if the picker itself threw.</param>
    /// <remarks>
    ///     ⚠ <b>Four things every one of these has to get right, written once.</b> There may be no
    ///     picker at all — a runtime question with a runtime answer; the answer arrives on whatever
    ///     thread the platform completed on, so it goes through <see cref="Deferred" /> to reach the
    ///     frame thread; a cancelled picker answers null and must do nothing; and a picker that threw
    ///     is a notification rather than an exception in the loop. Six call sites had four copies of
    ///     that between them.
    /// </remarks>
    void Picked(Func<INativeDialogs, ValueTask<string?>> ask, Action<string> then, string failed) {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        deferred.When(
            ask(dialogs),
            path => {
                if (path is { Length: > 0 }) {
                    then(path);
                }
            },
            failure => Shell.Notifications.Show(failed, NotificationSeverity.Error, failure.Message)
        );
    }
}
