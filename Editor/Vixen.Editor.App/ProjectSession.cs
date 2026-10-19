// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>The editor's <see cref="IProjectSession" />: the rescan, the reopen request, the recent list and the scaffold.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten</b> — doc 50 § P2d, first slice. <see cref="Rescan" /> is
///         <c>ProjectBrowser.Rescan</c>'s three calls, which were also <c>EditorApplication.FollowDisk</c>'s
///         for when the browser was closed — two copies of one sequence, one in a view. And
///         <see cref="RequestProject" /> is <c>EditorProjects.cs</c>'s, with the close prompt handed in
///         rather than reached back for. The second slice (#1293) brought the recent list, New
///         Project's scaffold and the newer-engine warning out of the same file.
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
    readonly ProjectHistory history;

    /// <summary>Builds the session over the open project.</summary>
    /// <param name="project">The open project.</param>
    /// <param name="notify">Where a refusal is reported.</param>
    /// <param name="requestClose">The application's close request, which puts up the unsaved-work prompt.</param>
    /// <param name="closing">
    ///     Whether the editor has agreed to close or is still asking — false afterwards means the prompt
    ///     was declined.
    /// </param>
    /// <param name="history">The user's recent-projects list, read from their data directory.</param>
    public ProjectSession(
        EditorProject project,
        Action<string, NotificationSeverity, string?> notify,
        Action requestClose,
        Func<bool> closing,
        ProjectHistory history
    ) {
        this.project = project;
        this.notify = notify;
        this.requestClose = requestClose;
        this.closing = closing;
        this.history = history;
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
    public IReadOnlyList<RecentProject> Recent => history.Entries;

    /// <summary>The list itself, whose length the preferences set.</summary>
    internal ProjectHistory History => history;

    /// <summary>Puts the open project at the top of the recent list, and says so if a newer engine made it.</summary>
    /// <param name="now">When, in UTC.</param>
    /// <remarks>
    ///     ⚠ <b>Called once the editor has opened the project, not when this is built.</b> A root that
    ///     turned out to be unreadable must not be offered as the first thing to try again next time —
    ///     which is why the application calls this at the end of its constructor rather than here.
    /// </remarks>
    internal void Opened(DateTime now) {
        history.Record(project.Paths.Root, now);
        WarnIfNewerEngine();
    }

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

    /// <summary>Writes a new game project and opens it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to make two directories and call it a project, and the consequence
    ///         showed up two milestones later.</b> A folder with an <c>Assets/</c> in it <i>is</i> a
    ///         project as far as <c>AssetDatabase</c> is concerned — but it has no <c>.csproj</c>, so
    ///         there is nothing for <c>dotnet publish</c> to publish, and E6's Build and Run was
    ///         greyed for every project the editor had ever made with a message naming a terminal
    ///         command. An editor that cannot finish the project it just created fails doc 20's
    ///         second bar on the first screen a new user sees.
    ///     </para>
    ///     <para>
    ///         <b>It writes the <c>game</c> template</b> — the same files <c>dotnet new vixen-game</c>
    ///         and <c>vixen new game</c> write, through the same <see cref="ProjectScaffold" />, so
    ///         there is no third answer to "what is in a new project".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The name is the folder's, cleaned rather than refused.</b> It is not typed here —
    ///         it is whatever directory somebody picked in a file dialog — and "my game (2)" is an
    ///         ordinary folder name and an impossible namespace. See <see cref="ProjectScaffold.NameFrom" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A directory that already holds some of those files is opened rather than
    ///         clobbered.</b> The scaffold finds every collision before it writes anything, and this
    ///         is the case where somebody pointed New Project at a project they already had: the
    ///         useful response is to open it and say what was left alone, not to refuse and not to
    ///         overwrite their <c>Program.cs</c>.
    ///     </para>
    /// </remarks>
    public void CreateProject(string root) {
        try {
            var paths = new ProjectPaths(root);

            Directory.CreateDirectory(paths.Assets);
            Directory.CreateDirectory(paths.ProjectSettings);

            var scaffold = ProjectScaffold.Write("game", ProjectScaffold.NameFrom(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))), root);

            if (scaffold.Collisions.Count > 0) {
                notify(
                    "Opened what was already there",
                    NotificationSeverity.Warning,
                    $"{scaffold.Collisions.Count} file(s) of the game template are already in that "
                    + "folder, so none of them were written."
                );
            } else if (scaffold.Error.Length > 0) {
                // Reported rather than fatal: the directories exist, so what opens is a project that
                // can be edited and cannot yet be published — which is the state this whole method
                // used to leave behind unconditionally.
                notify("The project has no C# project file", NotificationSeverity.Warning, scaffold.Error);
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            notify("Could not create the project", NotificationSeverity.Error, exception.Message);
            return;
        }

        RequestProject(root);
    }

    /// <summary>Says so when the project was made by an engine newer than this one.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>.vxproj</c>'s reader, and the reason the file carries a version at all.</b>
    ///     Doc 08 has named the marker since it was written; what makes it more than a sentinel is
    ///     that opening a project built against a newer engine fails <i>later and stranger</i> — a
    ///     scene with a component this build has never heard of, a settings file with keys nothing
    ///     claims — and every one of those failures is more confusing than being told at the door.
    ///     A field nothing reads would teach people the file does not matter, which is the bar doc
    ///     20's A4 sets for a shipped setting and it applies here too.
    /// </remarks>
    void WarnIfNewerEngine() {
        if (ProjectMarker.TryRead(project.Paths.Root, out var marker) && ProjectMarker.IsFromTheFuture(marker)) {
            notify(
                "This project was made with a newer Vixen",
                NotificationSeverity.Warning,
                $"It says engine {marker.Engine}; this editor is {ProjectScaffold.SdkVersion}. Anything it "
                + "contains that this build does not know about will be reported as it is met."
            );
        }
    }
}
