// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>The editor's <see cref="IProjectSession" />: the rescan and the reopen request.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten</b> — doc 50 § P2d, first slice. <see cref="Rescan" /> is
///         <c>ProjectBrowser.Rescan</c>'s three calls, which were also <c>EditorApplication.FollowDisk</c>'s
///         for when the browser was closed — two copies of one sequence, one in a view. And
///         <see cref="RequestProject" /> is <c>EditorProjects.cs</c>'s, with the close prompt handed in
///         rather than reached back for.
///     </para>
///     <para>
///         ⚠ <b>The index is saved on every rescan, which is not what <c>EditorProject.Open</c> does</b>
///         — that saves only when the scan disagreed with the index it loaded. The browser's copy never
///         had the guard and this keeps its behaviour: an in-editor rename or move changes the database
///         in memory and then rescans, and a guard measuring the scan alone would see "nothing changed"
///         and leave a stale index on disk. That is a cache and not a correctness problem, but it is a
///         decision and not part of a move.
///     </para>
/// </remarks>
internal sealed class ProjectSession : IProjectSession {
    readonly EditorProject project;
    readonly Action<string, NotificationSeverity, string?> notify;
    readonly Action requestClose;
    readonly Func<bool> closing;

    /// <summary>Builds the session over the open project.</summary>
    /// <param name="project">The open project.</param>
    /// <param name="notify">Where a refusal is reported.</param>
    /// <param name="requestClose">The application's close request, which puts up the unsaved-work prompt.</param>
    /// <param name="closing">
    ///     Whether the editor has agreed to close or is still asking — false afterwards means the prompt
    ///     was declined.
    /// </param>
    public ProjectSession(
        EditorProject project,
        Action<string, NotificationSeverity, string?> notify,
        Action requestClose,
        Func<bool> closing
    ) {
        this.project = project;
        this.notify = notify;
        this.requestClose = requestClose;
        this.closing = closing;
    }

    /// <inheritdoc />
    public EditorProject Project => project;

    /// <inheritdoc />
    /// <remarks>
    ///     Read by the host after the close has done its work. Null is the ordinary case and means the
    ///     user is quitting.
    /// </remarks>
    public string? PendingProject { get; private set; }

    /// <inheritdoc />
    public event Action<ScanReport>? Rescanned;

    /// <inheritdoc />
    /// <remarks>
    ///     Asks about unsaved work first, through the same prompt the window's close button uses —
    ///     which is why it is a request rather than a call.
    /// </remarks>
    public void RequestProject(string root) {
        ArgumentException.ThrowIfNullOrEmpty(root);

        var full = Path.GetFullPath(root);

        if (string.Equals(full, project.Paths.Root, StringComparison.Ordinal)) {
            notify(project.Name, NotificationSeverity.Info, "That project is already open.");
            return;
        }

        PendingProject = full;
        requestClose();

        // ⚠ Cleared when the prompt was declined, which the close request reports by leaving the
        // editor neither closing nor asking. Without this, backing out of "save your changes?" would
        // leave a pending root that the *next* close — the one where they meant to quit — would
        // silently act on.
        if (!closing()) {
            PendingProject = null;
        }
    }

    /// <summary>Forgets the pending root, because the prompt it was waiting on was cancelled.</summary>
    /// <remarks>
    ///     ⚠ <b>A cancelled prompt is a decision, not a deferred one.</b> <see cref="RequestProject" />
    ///     sets a pending root before asking, and leaving it set would make the <i>next</i> close — the
    ///     one where they meant to quit — silently reopen the editor over a project they backed out of
    ///     choosing.
    /// </remarks>
    internal void Decline() => PendingProject = null;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The index is saved after the scan.</b> It lives in <c>Library/</c> and is what makes the
    ///     <i>next</i> launch skip the walk; a rescan that left the old one on disk would make the
    ///     editor slower the more often it was refreshed, which is the wrong way round.
    /// </remarks>
    public ScanReport Rescan() {
        var report = project.Assets.Scan();

        project.Assets.Save();

        // ⚠ Rebuilt with it. The reverse index is what answers "what would break if I deleted this",
        // and one built against the previous scan answers it about assets that have moved.
        project.References.Build(project.Assets);

        Rescanned?.Invoke(report);
        return report;
    }
}
