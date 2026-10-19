// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Behaviors;

namespace Vixen.Editor.App;

/// <summary>The editor's <see cref="IProjectSession" />: the rescan, the reopen request, the recent list and the scaffold.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten</b> — doc 50 § P2d, first slice. <see cref="Rescan" /> is
///         <c>ProjectBrowser.Rescan</c>'s three calls, which were also <c>EditorApplication.FollowDisk</c>'s
///         for when the browser was closed — two copies of one sequence, one in a view. And
///         <see cref="RequestProject" /> is <c>EditorProjects.cs</c>'s, with the close prompt handed in
///         rather than reached back for. The second slice (#1293) brought the recent list, New
///         Project's scaffold and the newer-engine warning out of the same file; the third brought
///         the open itself (<see cref="Open" />), the disk watcher (<see cref="Watch" />,
///         <see cref="FollowDisk" />) and the project's code (<see cref="BuildCode" />) out of
///         <c>EditorApplication</c>'s constructor and frame loop.
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
internal sealed class ProjectSession : IProjectSession, IDisposable {
    readonly EditorProject project;
    readonly Action<string, NotificationSeverity, string?> notify;
    readonly Action requestClose;
    readonly Func<bool> closing;
    readonly ProjectHistory history;
    readonly Action<LogLevel, string> log;
    readonly Func<SceneDocument?> activeScene;

    /// <summary>What the watcher has to say, reused so that a quiet frame allocates nothing.</summary>
    readonly List<FileChange> changes = [];

    /// <summary>Authored behaviours a failed build had no type to put back on, by the scene they came off.</summary>
    /// <remarks>
    ///     ⚠ <b>Held rather than dropped (#1490), and the scene refuses to save while it holds any.</b> A
    ///     failed build is not the author deleting a class — the next build that works declares the alias
    ///     again — so the values wait here, and <see cref="RefuseToSaveWithout" /> keeps a save from
    ///     writing the scene without them.
    /// </remarks>
    readonly Dictionary<SceneDocument, List<AuthoredBehavior>> held = [];

    /// <summary>What <see cref="RestoreProjectBehaviors" /> could not put back during this build.</summary>
    int dropped;

    /// <summary>What tells the editor that something outside it changed the assets on disk.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The panel's own remarks used to say why there wasn't one</b> — "a watcher is worth
    ///         having and is not free: it needs debouncing, a rename heuristic and a way to not fight
    ///         the editor's own writes". All three of those are <c>Vixen.Core.IO</c>'s and have been
    ///         since <c>FileChangeCoalescer</c> was written; what was missing was not the mechanism
    ///         but the wire. Saving a texture from another program left the Project panel showing the
    ///         project as it was when the editor started.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null when there is no assets directory</b>, which a scratch project opened before
    ///         anything has been saved genuinely has. A missing folder is not an error worth refusing
    ///         to start over — it is a project with nothing in it yet.
    ///     </para>
    /// </remarks>
    IFileWatcher? watcher;

    /// <summary>What carries a change on disk the last few metres, from the project to the open document.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of the watcher that was never wired</b>, moved here from the application with the
    ///     watcher it reads (#1293). Everything else on this path reads the drained list for its length:
    ///     the browser rescans, the database rescans, the build panel refreshes. <c>ReloadShaders</c> is
    ///     the one exception and it filters to <c>.rvn</c> — so a <c>.vxscene</c> or a
    ///     <c>.vxcompositor</c> saved by another program reached the tree, the index and the build, and
    ///     did not reach the panel that had it open. Null until <see cref="Watch" />.
    /// </remarks>
    ExternalEdits? external;

    /// <summary>The project's own code. See <see cref="Code" />.</summary>
    readonly ProjectAssemblies code;

    /// <summary>Builds the session over the open project.</summary>
    /// <param name="project">The open project.</param>
    /// <param name="notify">Where a refusal is reported.</param>
    /// <param name="requestClose">The application's close request, which puts up the unsaved-work prompt.</param>
    /// <param name="closing">
    ///     Whether the editor has agreed to close or is still asking — false afterwards means the prompt
    ///     was declined.
    /// </param>
    /// <param name="history">The user's recent-projects list, read from their data directory.</param>
    /// <param name="log">Where the compiler's output and a watcher that could not start are written, or nowhere.</param>
    /// <param name="scene">
    ///     The scene whose authored behaviours a code build takes off and puts back, asked for at each
    ///     build because the editor's active scene moves; <see langword="null" /> for none.
    /// </param>
    public ProjectSession(
        EditorProject project,
        Action<string, NotificationSeverity, string?> notify,
        Action requestClose,
        Func<bool> closing,
        ProjectHistory history,
        Action<LogLevel, string>? log = null,
        Func<SceneDocument?>? scene = null
    ) {
        this.project = project;
        this.notify = notify;
        this.requestClose = requestClose;
        this.closing = closing;
        this.history = history;
        this.log = log ?? ((_, _) => { });
        activeScene = scene ?? (() => null);
        code = new ProjectAssemblies(project.Paths);
        project.DocumentSaving += RefuseToSaveWithout;

        // ⚠ #656: a document saved through `Vixen.Ui`'s `document.save` or the close prompt answers
        // a failed write with `false` and reports it here, which is what makes that `false` a message
        // rather than a Save that silently did nothing. With nobody subscribed it would throw. Moved
        // from the application's constructor (#1293): it is the project's documents and the session's
        // notification, and nothing of the window's.
        project.SaveFailed += (failed, failure) => notify(
            $"Could not save {failed.Title.Peek()}",
            NotificationSeverity.Error,
            failure.Message
        );
    }

    /// <summary>Opens the project at a root and builds the session over it.</summary>
    /// <param name="paths">Where the project is. A directory that does not exist yet is an empty project.</param>
    /// <param name="notify">Where a refusal is reported.</param>
    /// <param name="requestClose">The application's close request, which puts up the unsaved-work prompt.</param>
    /// <param name="closing">Whether the editor has agreed to close or is still asking.</param>
    /// <param name="history">The user's recent-projects list.</param>
    /// <param name="log">Where the compiler's output and a watcher that could not start are written.</param>
    /// <param name="scene">The scene a code build takes the authored behaviours off.</param>
    /// <returns>The session, with its project open and its index read.</returns>
    /// <remarks>
    ///     <c>EditorProject.Open</c> tolerates a missing Assets directory — see <c>AssetDatabase.Scan</c> —
    ///     which is what makes a scratch project under a directory that does not exist yet fine.
    /// </remarks>
    internal static ProjectSession Open(
        ProjectPaths paths,
        Action<string, NotificationSeverity, string?> notify,
        Action requestClose,
        Func<bool> closing,
        ProjectHistory history,
        Action<LogLevel, string> log,
        Func<SceneDocument?> scene
    ) {
        var project = new EditorProject(paths);
        project.Open();

        return new(project, notify, requestClose, closing, history, log, scene);
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
    public event Action<IReadOnlyList<FileChange>, bool>? ChangedOnDisk;

    /// <inheritdoc />
    public event Action? CodeBuilt;

    /// <inheritdoc />
    public bool IsWatching => watcher is not null;

    /// <summary>The project's own code, once it has been built and loaded.</summary>
    /// <remarks>
    ///     ⚠ <b>Reloaded by every <see cref="BuildCode" />.</b> This remark used to say the code was loaded
    ///     once and never unloaded because the registries had no way to empty; both halves were stale —
    ///     <c>ProjectAssemblies.Reload</c> unloads first, and <c>Unload</c> evicts all six registries.
    /// </remarks>
    public IProjectCode Code => code;

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

        // ⚠ Before anything drains the watcher again (#1488). A file dropped into `Assets/` from
        // outside gets its `.meta` minted by this scan, and that write is into the watched directory:
        // unsuppressed, the watcher reported it a quarter of a second later as a second outside
        // change, and the whole scan, sweep and thumbnail flush ran twice. `Suppress` also drops an
        // event already pending for the path, so it is right whether or not the platform has spoken.
        Suppress(report.Written);

        // ⚠ And the scan has now seen every file on disk, including one whose creation is still
        // settling in the watcher. Without this, deleting that file inside the debounce window
        // cancelled its pending creation and nothing was ever reported: the index kept an entry for a
        // file that was gone. The `.meta` echo above used to be the second burst that cleaned it up.
        watcher?.Observed();

        project.Assets.Save();

        // ⚠ Rebuilt with it. The reverse index is what answers "what would break if I deleted this",
        // and one built against the previous scan answers it about assets that have moved.
        project.References.Build(project.Assets);

        Rescanned?.Invoke(report);
        return report;
    }

    /// <summary>Tells the watcher that files this session wrote under <c>Assets/</c> were its own doing.</summary>
    /// <param name="written">Absolute paths; any outside the watched directory are ignored.</param>
    void Suppress(IReadOnlyList<string> written) {
        if (watcher is null) {
            return;
        }

        foreach (var absolute in written) {
            var relative = Path.GetRelativePath(project.Paths.Assets, absolute).Replace(Path.DirectorySeparatorChar, VirtualPath.Separator);

            if (!relative.StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative)
                && VirtualPath.TryCreate("/" + relative, out var path)) {
                watcher.Suppress(path);
            }
        }
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

    /// <summary>Builds and loads the project's own code, and says what happened.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>On the first frame rather than in the constructor.</b> It runs a compiler, which
    ///         takes seconds on a cold restore — and the console it reports into is a panel the shell
    ///         has to have built first. An editor that blocked on <c>dotnet build</c> before drawing
    ///         anything would look like one that failed to start.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A failed build is a notification and not a refusal.</b> The project's content is
    ///         still worth opening — a scene, a texture, a material do not care whether the game's
    ///         C# compiles — and an editor that would not open a project because of a syntax error
    ///         would be one you could not use to fix the syntax error.
    ///     </para>
    /// </remarks>
    public bool BuildCode() {
        // ⚠ Taken off before the assembly goes and put back after it returns. A behaviour somebody
        // authored is an instance of a type that is about to stop existing — leaving it attached
        // would hold the old context alive *and* lose the values, which is both failures at once.
        // Bytes and an alias are what crosses: the same alias registers again from the rebuilt
        // assembly, and the state goes into an instance of the new type.
        var scene = activeScene();
        var authored = SaveProjectBehaviors(scene);
        var before = held.ToArray();
        var built = code.Reload();

        held.Clear();

        if (built.Output is { Length: > 0 } said) {
            log(built.Failed ? LogLevel.Error : LogLevel.Debug, said);
        }

        if (built.Failed) {
            notify(
                "The project's code did not build",
                NotificationSeverity.Error,
                built.Assembly is null
                    ? "Its components and behaviours are not available. The console has what the compiler said."
                    : "The last build that worked is still loaded. The console has what the compiler said."
            );
        }

        // ⚠ Put back on every path, not only when an assembly loaded (#1490). A project with no code
        // and a build that failed used to leave the scene without them, and the next save wrote it so.
        // The load ran the module initializers, so whatever the project declares is in the registries
        // — and `ComponentsView.Registered` re-reads them, so the Add Component menu has it untold.
        if (scene is not null) {
            RestoreProjectBehaviors(scene, authored);
        }

        // And what an earlier failure had nowhere to put, now that there may be somewhere.
        foreach (var (document, waiting) in before) {
            RestoreProjectBehaviors(document, waiting);
        }

        Report(built.Failed);

        if (built.Assembly is not null) {
            // The inspectors' sections and the build panel are the application's, and follow this —
            // after a failed build too when the last good one was loaded again, because its types are
            // new ones from a new context.
            CodeBuilt?.Invoke();
        }

        return !built.Failed;
    }

    /// <summary>One authored behaviour, as something that outlives the type that held it.</summary>
    /// <param name="Entity">Which entity carried it.</param>
    /// <param name="Alias">Its name, which is what survives a rebuild.</param>
    /// <param name="State">Its values.</param>
    readonly record struct AuthoredBehavior(Entity Entity, string Alias, byte[] State);

    /// <summary>Takes the loaded project assembly's behaviours off the scene, keeping what was in them.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the project's — and the remark that said "every behaviour" was wrong (#1490).</b> It
    ///     argued that deciding which assembly a behaviour came from means reading its type, and that
    ///     types are about to become unreliable. They are not yet: this runs before the unload, while the
    ///     type is exactly what it says. Taking an engine's or a plugin's behaviour off as well put it at
    ///     the mercy of the reload — which, with no <c>.csproj</c>, never put it back.
    /// </remarks>
    List<AuthoredBehavior> SaveProjectBehaviors(SceneDocument? scene) {
        List<AuthoredBehavior> authored = [];

        if (scene is null || code.Loaded is not { } loaded) {
            return authored;
        }

        foreach (var entity in scene.Entities) {
            foreach (var behavior in scene.Behaviors.AllOn(entity).ToArray()) {
                if (behavior.GetType().Assembly != loaded || !SceneBehaviorRegistry.TryGet(behavior.GetType(), out var binder)) {
                    continue;
                }

                authored.Add(new(entity, binder.Name, binder.Save(behavior)));
                binder.RemoveFrom(scene.Behaviors, entity);
            }
        }

        return authored;
    }

    /// <summary>Puts them back, on the rebuilt types — or holds them, when nothing declares them yet.</summary>
    /// <remarks>
    ///     ⚠ <b>A behaviour whose alias nothing declares is held, and <see cref="Report" /> decides what
    ///     that means.</b> After a build that worked, somebody deleted or renamed the class; there is
    ///     nowhere to put its values, and silently keeping them would mean a save that wrote a behaviour
    ///     the build cannot name — so it is dropped, and said so. After a build that failed, it waits.
    /// </remarks>
    void RestoreProjectBehaviors(SceneDocument scene, List<AuthoredBehavior> authored) {
        foreach (var behavior in authored) {
            if (!scene.World.IsAlive(behavior.Entity)) {
                // The entity went while the behaviour was held; there is nothing to put it on.
                dropped++;
                continue;
            }

            if (!SceneBehaviorRegistry.TryGet(behavior.Alias, out var binder)) {
                if (!held.TryGetValue(scene, out var waiting)) {
                    held[scene] = waiting = [];
                }

                waiting.Add(behavior);
                continue;
            }

            binder.AttachTo(scene.Behaviors, behavior.Entity, binder.Restore(behavior.State));
        }
    }

    /// <summary>Says what a build could not put back: dropped after one that worked, held after one that did not.</summary>
    void Report(bool failed) {
        if (!failed) {
            foreach (var (_, waiting) in held) {
                dropped += waiting.Count;
            }

            held.Clear();
        }

        if (dropped > 0) {
            notify(
                $"{dropped} authored behaviour(s) were dropped",
                NotificationSeverity.Warning,
                "The rebuilt project no longer declares them, so there was nowhere to put their values."
            );
        }

        dropped = 0;

        if (held.Values.Sum(waiting => waiting.Count) is > 0 and var count) {
            notify(
                $"{count} authored behaviour(s) are held until the code builds",
                NotificationSeverity.Warning,
                "Nothing declares them until it does, so they are off the scene for now — and the scene "
                + "will not save, because it would be written without them."
            );
        }
    }

    /// <summary>Refuses to write a scene that has behaviours held off it.</summary>
    /// <remarks>
    ///     ⚠ <b>Raised before the write, through <c>EditorProject.DocumentSaving</c>, as an
    ///     <see cref="IOException" /></b> — the failure every save path already reports as a refused write
    ///     and leaves the document dirty for. A scene saved now would be the file without the behaviours,
    ///     which is the data loss #1490 is about arriving by the other door.
    /// </remarks>
    void RefuseToSaveWithout(EditorDocument document) {
        if (document is SceneDocument scene && held.TryGetValue(scene, out var waiting) && waiting.Count > 0) {
            throw new IOException(
                $"'{scene.Title.Peek()}' has {waiting.Count} authored behaviour(s) held off it until the "
                + "project's code builds. Saving now would write it without them; fix the build first."
            );
        }
    }

    /// <summary>Starts watching the project's assets, if there are any to watch.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called by the application after it has written its first scene, not when this is
    ///         built.</b> A first run saves <c>Main.vxscene</c> into a new project, and a watcher already
    ///         running would hear the editor's own write as somebody else's.
    ///     </para>
    ///     ⚠ <b>A quarter of a second of quiet before a change counts.</b> A text editor's atomic
    ///     save is four raw events and a rename; an art tool exporting a texture atlas is one per
    ///     file. Rescanning on each would walk the project several times for one action —
    ///     <c>FileChangeCoalescer</c>'s debounce is what turns a burst into a rescan.
    ///     <para>
    ///         ⚠ <b>The watcher before <see cref="External" />, and that is one of the constructor's
    ///         order-dependent steps made a line here (#1293).</b> <c>ExternalEdits</c> suppresses the
    ///         editor's own saves on the watcher it is handed, so one built first would suppress nothing.
    ///         And it is built when there is no watcher too, because it is what subscribes to
    ///         <c>EditorProject.DocumentSaving</c>, and a project with no watcher still has documents
    ///         that save. What a null watcher costs is the suppression, not the wiring.
    ///     </para>
    /// </remarks>
    /// <param name="assets">
    ///     Whether to start the platform's watcher at all — off only in a harness. ⚠ 80–95 ms of a
    ///     session, measured, and it is the platform's rather than ours:
    ///     <c>FileSystemWatcher.EnableRaisingEvents = true</c> starts an FSEvents stream, and a test that
    ///     never edits a file behind the editor's back paid it anyway, 344 times over in
    ///     <c>Vixen.Editor.App.Tests</c> — see <c>EditorSessionOptions.WatchAssets</c> and #557.
    /// </param>
    internal void Watch(bool assets) {
        if (assets) {
            watcher?.Dispose();
            watcher = Start(project);
        }

        external?.Dispose();
        external = new(project, watcher);
    }

    /// <summary>What a change on disk did to the open documents, for the suite that asserts about it.</summary>
    /// <exception cref="InvalidOperationException">Asked for before <see cref="Watch" />.</exception>
    internal ExternalEdits External =>
        external ?? throw new InvalidOperationException("The session routes changes to open documents only once it watches.");

    IFileWatcher? Start(EditorProject open) {
        if (!Directory.Exists(open.Paths.Assets)) {
            return null;
        }

        try {
            return new FileWatcher(open.Paths.Assets, VirtualPath.Root) {
                Debounce = TimeSpan.FromMilliseconds(250)
            };
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A project on a share the platform cannot watch, or one whose handle limit is spent.
            // The editor still opens and Refresh still works, which is the right trade for a
            // convenience — and the console says why the panel stopped keeping up by itself.
            log(
                LogLevel.Warning,
                $"Could not watch '{open.Paths.Assets}' for changes, so the Project panel will only "
                + $"update on Refresh. {exception.Message}"
            );

            return null;
        }
    }

    /// <summary>Rescans when something outside the editor has changed the assets, and says what changed.</summary>
    /// <returns>Whether anything had changed — what <see cref="ChangedOnDisk" /> was raised for.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What changed is deliberately not used for the scan.</b> The database's scan is the
    ///         thing that knows how to repair a sidecar, re-GUID a duplicate and quarantine an orphan,
    ///         and a browser that applied one path at a time would be a second, worse implementation of
    ///         it that disagreed after the first rename. The changes are drained to find out
    ///         <i>whether</i> to rescan, and handed on for the readers that do care what — a shader
    ///         reload, an open document.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overflow is a rescan too.</b> Losing events is the one case where nothing that
    ///         arrives afterwards can be trusted to describe the folder — which is exactly the case a
    ///         full rescan covers, and why the watcher reports it separately.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Drained on the frame thread, which is the whole shape of <c>IFileWatcher</c>.</b> A
    ///         rescan clears and repopulates the database's dictionaries, and doing that from a
    ///         <c>FileSystemWatcher</c> callback would race every panel reading it. The application
    ///         calls this once a frame.
    ///     </para>
    /// </remarks>
    internal bool FollowDisk() {
        if (watcher is null) {
            return false;
        }

        changes.Clear();

        var drained = watcher.Drain(changes) > 0;
        var overflowed = watcher.HasOverflowed;

        if (overflowed) {
            watcher.ClearOverflow();
            drained = true;
        }

        if (!drained) {
            return false;
        }

        // ⚠ The database is the project's and outlives any panel — a file added while the Project tab
        // is closed has to be in the asset picker and in a build, not only in the tree.
        Rescan();

        // ⚠ After the rescan, and that is the one ordering constraint in the seam. A path becomes a
        // document through the GUID index, and a rename is exactly the change that moves an entry in
        // it — so a reader routing before the scan would look the new path up in an index that
        // still has the old one and find nothing open.
        ChangedOnDisk?.Invoke(changes, overflowed);

        // ⚠ And then the open documents, last — after the readers above, which is where the
        // application's `FollowedDisk` had it (#1293). An overflow has no paths to route, so it does
        // the other thing: re-read what can be re-read.
        if (overflowed) {
            external?.Rescan();
        } else {
            external?.Apply(changes);
        }

        return true;
    }

    /// <summary>Stops watching the disk.</summary>
    /// <remarks>
    ///     ⚠ <b>Early in the editor's shutdown, so that nothing arrives during the rest of it.</b> A
    ///     watcher left running holds a platform handle and keeps recording changes into a coalescer
    ///     nobody will drain again.
    /// </remarks>
    public void Dispose() {
        watcher?.Dispose();
        watcher = null;

        // And the subscription on the project's saves with it, so that the documents closing after
        // this do not announce writes to a watcher that has gone.
        external?.Dispose();
        external = null;

        project.DocumentSaving -= RefuseToSaveWithout;
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
