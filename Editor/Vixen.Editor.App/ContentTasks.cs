// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>Importing and building content, from the editor, without freezing the window.</summary>
/// <remarks>
///     <para>
///         <b>The last two things Phase 6's exit criterion asks for.</b> Both were built and neither
///         had a way in from the editor: <c>ContentPipeline</c> does the work and <c>vixen</c> has
///         been calling it from a terminal. This is the same call on a background task, which is
///         what doc 11 means by "never a modal progress dialog".
///     </para>
///     <para>
///         ⚠ <b>One at a time, and the guard is not politeness.</b> Two imports write the same
///         sidecars, the same artefact store and the same cache file at once. The second one does not
///         produce a worse build, it produces a corrupt <c>Library/</c> — and the report it prints
///         describes neither of the two things that happened.
///     </para>
///     <para>
///         ⚠ <b>The work runs on a pool thread and the result is shown on the frame thread.</b>
///         Notifications, panels and the document belong to whoever is drawing them, so what crosses
///         back is a queued value that <see cref="Pump" /> drains once a frame — the same shape
///         <c>BackgroundTaskManager</c> uses for progress, and for the same reason.
///     </para>
/// </remarks>
sealed class ContentTasks {
    readonly EditorProject project;
    readonly EditorShell shell;
    readonly ProjectWorkspace workspace;
    readonly ConcurrentQueue<Finished> finished = [];
    readonly ConcurrentQueue<Action> afterwards = [];

    int running;

    /// <summary>Prepares the editor's content pipeline over a project.</summary>
    /// <param name="project">The project.</param>
    /// <param name="shell">The chrome the tasks and notifications appear in.</param>
    public ContentTasks(EditorProject project, EditorShell shell) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(shell);

        this.project = project;
        this.shell = shell;
        workspace = new(project.Paths);
        Meshes = new(workspace);
        Surfaces = new(workspace);
    }

    /// <summary>Where the viewport reads the geometry a scene's mesh references name.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Here because this is what owns a live <c>ProjectWorkspace</c>.</b> A source reads the
    ///         chunks the last import wrote, and the import cache that says which chunk is which is
    ///         loaded with the workspace — so a second workspace for the viewport would be a second copy
    ///         of a file this one already has open, and the two would disagree the moment an import
    ///         finished.
    ///     </para>
    ///     <para>
    ///         ⚠ Invalidated when an import finishes. A chunk is content-addressed, so a re-imported
    ///         mesh is a different id under the same reference and nothing about the cached geometry
    ///         would ever say it is stale — the viewport would draw the old mesh until the project was
    ///         reopened.
    ///     </para>
    /// </remarks>
    public ProjectMeshSource Meshes { get; }

    /// <summary>Where the viewport reads the look of the materials a scene's entities name.</summary>
    /// <inheritdoc cref="Meshes" path="/remarks" />
    public ProjectSurfaceSource Surfaces { get; }

    /// <summary>Whether an import or a build is running.</summary>
    /// <remarks>What the two commands' enablement reads, so the menu greys itself out.</remarks>
    public bool IsBusy => Volatile.Read(ref running) != 0;

    /// <summary>Which target the editor imports and builds for.</summary>
    /// <remarks>
    ///     The machine the editor is running on, until there is a target picker. A content build is
    ///     target-specific — the same texture is BC7 on a desktop and ASTC on a phone — so there is
    ///     no neutral answer, and the one that surprises nobody is "for this computer".
    /// </remarks>
    public string Target { get; set; } = ProjectWorkspace.HostTarget;

    /// <summary>Where a build for a target goes when nobody said.</summary>
    /// <param name="target">The target.</param>
    /// <returns>The directory.</returns>
    /// <remarks>
    ///     Forwarded rather than recomputed, so that the editor's default output and
    ///     <c>vixen content build</c>'s are one rule. The rule includes turning a narrowed target's
    ///     slash into a hyphen, which is exactly the sort of thing a second copy gets wrong.
    /// </remarks>
    public string DefaultOutput(string target) => workspace.DefaultOutput(target);

    /// <summary>Runs an import over the whole project.</summary>
    public void Import() {
        Run(
            "Importing assets",
            async (task, diagnostics) => {
                var summary = await ContentPipeline.ImportAsync(
                    workspace,
                    Target,
                    diagnostics.Add,
                    step => task.Report(step.Fraction, step.Path),
                    cancellationToken: task.Cancellation
                ).ConfigureAwait(false);

                return new(
                    summary.Succeeded ? NotificationSeverity.Success : NotificationSeverity.Error,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Imported {summary.Imported}, {summary.Cached} unchanged, {summary.Failed} failed"
                    )
                );
            }
        );
    }

    /// <summary>Imports and then packs a content build.</summary>
    /// <remarks>
    ///     ⚠ <b>Imports first, always.</b> The plan reads the import cache, so a build over a project
    ///     whose sources have changed packs the previous import's artefacts — a build that succeeds
    ///     and ships yesterday's content, which is the worst shape a wrong answer can take.
    /// </remarks>
    public void Build() {
        Run(
            "Building content",
            async (task, diagnostics) => {
                task.Report(0f, "Importing");

                var imported = await ContentPipeline.ImportAsync(
                    workspace,
                    Target,
                    diagnostics.Add,
                    step => task.Report(step.Fraction * 0.8f, step.Path),
                    cancellationToken: task.Cancellation
                ).ConfigureAwait(false);

                if (!imported.Succeeded) {
                    return new(
                        NotificationSeverity.Error,
                        $"{imported.Failed} asset(s) failed to import, so nothing was packed"
                    );
                }

                task.Cancellation.ThrowIfCancellationRequested();
                task.Report(0.8f, "Packing");

                var output = workspace.DefaultOutput(Target);
                var built = ContentPipeline.Build(workspace, Target, output, diagnostics.Add);

                return built.Succeeded
                    ? new(
                        NotificationSeverity.Success,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{built.Addresses} address(es) in {built.Bundles} bundle(s), {built.Bytes:N0} bytes"
                        ),
                        built.OutputDirectory
                    )
                    : new Finished(NotificationSeverity.Error, "The content build did not produce a build");
            }
        );
    }

    /// <summary>Builds the content, publishes the application, and optionally launches it.</summary>
    /// <param name="request">What to build, how, and where to.</param>
    /// <param name="log">Where <c>dotnet publish</c>'s own output goes.</param>
    /// <param name="completed">Told whether it worked, on the frame thread.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 20's B7 asks for a Build Settings window "over <c>Tools/Vixen.Cli</c>'s existing
    ///         calls", and this is the sequence that command runs</b> — import, pack, publish — with
    ///         <see cref="PlayerBuild" /> as the shared half. It is here rather than beside the panel
    ///         for the reason this class exists at all: it is the one place that knows two of these
    ///         must not run at once, and a player build writes the same <c>Library/</c> an import does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It imports for the <i>player's</i> target rather than for <see cref="Target" />.</b>
    ///         The editor's content target is what its own panels read and the player's is what ships;
    ///         they agree in the ordinary case and a team building for a phone from a workstation is
    ///         exactly the case where they must not be one field. See <c>PlayerBuildSettings</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A launch is awaited, so the task stays in the task centre for as long as the game
    ///         is up.</b> That is the useful shape rather than an accident of the call: the progress
    ///         entry is what says a player is running, its Cancel is what stops one that has hung with
    ///         no window, and the notification at the end carries the game's own exit code — which is
    ///         the thing somebody wants after a crash and would otherwise have to find in a terminal.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No shader bundle, and the difference from <c>vixen build</c> is stated rather than
    ///         left to be discovered.</b> The ahead-of-time compile is <c>ShaderBuildRunner</c>'s,
    ///         which links Raven's compiler — a build-time library the editor deliberately does not
    ///         carry. A project with a <c>Shaders.effects.json</c> is told so in the build log; one
    ///         without has no bundle either way and nothing to be told.
    ///     </para>
    /// </remarks>
    public void BuildPlayer(PlayerBuildRequest request, TextWriter log, Action<bool>? completed = null) {
        ArgumentNullException.ThrowIfNull(log);

        Run(
            $"Building {request.Target}",
            async (task, diagnostics) => {
                task.Report(0f, "Importing");

                var imported = await ContentPipeline.ImportAsync(
                    workspace,
                    request.Target,
                    diagnostics.Add,
                    step => task.Report(step.Fraction * ImportShare, step.Path),
                    cancellationToken: task.Cancellation
                ).ConfigureAwait(false);

                if (!imported.Succeeded) {
                    return new(
                        NotificationSeverity.Error,
                        $"{imported.Failed} asset(s) failed to import, so nothing was built"
                    );
                }

                task.Cancellation.ThrowIfCancellationRequested();
                task.Report(ImportShare, "Packing content");

                // Into the project's own content directory rather than the artefact directory: the
                // SDK's targets are what copy it beside the binary, and doing it here as well would
                // put two copies in the publish and disagree about which is current. `vixen build`
                // makes the same call for the same reason.
                var packed = ContentPipeline.Build(
                    workspace,
                    request.Target,
                    workspace.DefaultOutput(request.Target),
                    diagnostics.Add
                );

                if (!packed.Succeeded) {
                    return new(NotificationSeverity.Error, "The content build did not produce a build");
                }

                task.Cancellation.ThrowIfCancellationRequested();
                task.Report(PublishShare, "Publishing");

                log.WriteLine(
                    $"Publishing {Path.GetFileName(request.ProjectFile)} for {request.Target} as {request.Variant}"
                );

                if (File.Exists(Path.Combine(project.Paths.ProjectSettings, ShaderManifestFile))) {
                    log.WriteLine(
                        $"  This project has a {ShaderManifestFile}. The editor does not compile the shader "
                        + "bundle — run `vixen build` for a player that carries one."
                    );
                }

                var published = await PlayerBuild.PublishAsync(
                    request.ProjectFile,
                    request.Shape,
                    request.Variant,
                    request.Output,
                    log,
                    capture: true,
                    task.Cancellation
                ).ConfigureAwait(false);

                if (!published) {
                    return new(
                        NotificationSeverity.Error,
                        "The publish failed",
                        "dotnet publish said why — the console has its output."
                    );
                }

                if (!request.Launch) {
                    return new(NotificationSeverity.Success, $"{request.Target} player built", request.Output);
                }

                task.Report(LaunchShare, "Running");

                var exit = await PlayerBuild.LaunchAsync(
                    request.Output,
                    Path.GetFileNameWithoutExtension(request.ProjectFile),
                    [],
                    log,
                    capture: true,
                    task.Cancellation
                ).ConfigureAwait(false);

                return exit == 0
                    ? new(NotificationSeverity.Success, "The player exited normally", request.Output)
                    : new Finished(
                        NotificationSeverity.Warning,
                        $"The player exited with code {exit.ToString(CultureInfo.InvariantCulture)}",
                        exit == PlayerBuild.NoExecutable
                            ? "Nothing was launched: the publish produced no executable of that name."
                            : "Its own exit code, not the build's. The console has what it said."
                    );
            },
            completed
        );
    }

    /// <summary>How much of the progress bar each of the three steps gets.</summary>
    /// <remarks>
    ///     The import is the only one that can report a fraction of itself; the pack is a few seconds
    ///     and the publish is a process this cannot see inside. So the bar is honest about the first
    ///     and moves twice for the other two, rather than pretending to a smoothness it does not have.
    /// </remarks>
    const float ImportShare = 0.4f;

    /// <inheritdoc cref="ImportShare" />
    const float PublishShare = 0.5f;

    /// <inheritdoc cref="ImportShare" />
    const float LaunchShare = 0.95f;

    /// <summary>What the shader manifest is called, which is <c>ShaderBuildRunner</c>'s constant.</summary>
    /// <remarks>
    ///     Repeated rather than referenced, because the type that declares it is in <c>Vixen.Cli</c> —
    ///     which is a tool this application does not link and should not. What is shared between the
    ///     two heads is <see cref="PlayerBuild" />; a file name mentioned in one sentence of a log is
    ///     not worth widening that.
    /// </remarks>
    const string ShaderManifestFile = "Shaders.effects.json";

    /// <summary>Shows what any finished task had to say. Called once a frame, on the frame thread.</summary>
    public void Pump() {
        while (finished.TryDequeue(out var result)) {
            // ⚠ Rescanned here rather than inside the task. An import repairs sidecars in a database
            // of its own — see `ProjectWorkspace` for why it is not the editor's — so the panels are
            // showing what was true before it ran until the editor's own index is rebuilt, which has
            // to happen on the thread that owns them.
            Rescan?.Invoke();

            // ⚠ And the geometry and the materials the viewport is holding, for the same reason and on
            // the same thread: a re-imported asset is a new chunk id under the same reference, so
            // nothing about the cached MeshData or surface says it is stale and the viewport would draw
            // the old one for ever. Editing a material is the case that makes this visible — it is a
            // file somebody has open in another tab, and the point of the material reaching the
            // viewport at all is watching the change land there.
            workspace.Cache.TryLoad(workspace.CacheFile);
            Meshes.Invalidate();
            Surfaces.Invalidate();

            shell.Notifications.Show(result.Title, result.Severity, result.Detail);
        }

        // ⚠ After the notifications and on this thread, which is the whole point of it being here.
        // What a player build wants to do when it ends is put a device row back from Deploying — a
        // panel touch, and the task that ended was on the pool.
        while (afterwards.TryDequeue(out var work)) {
            work();
        }

        if (IsBusy != wasBusy) {
            wasBusy = !wasBusy;
            BusyChanged?.Invoke();
        }
    }

    /// <summary>Whether work was running the last time <see cref="Pump" /> looked.</summary>
    bool wasBusy;

    /// <summary>What to do when a task has finished and the panels are stale.</summary>
    public Action? Rescan { get; set; }

    /// <summary>What to do when work starts or stops, for a panel whose buttons say which.</summary>
    /// <remarks>
    ///     ⚠ <b>Raised from <see cref="Pump" /> by comparing, rather than from the two places that
    ///     write the flag.</b> One of those places is on a pool thread and the other is not, and
    ///     there are four ways out of a task — finished, failed, refused, cancelled — of which
    ///     cancellation produces no result at all. A bool compared once a frame catches every one of
    ///     them and cannot be forgotten by a fifth way out being added later.
    /// </remarks>
    public Action? BusyChanged { get; set; }

    /// <summary>Runs one piece of content work, and tells the caller how it went.</summary>
    /// <param name="title">What the task centre calls it.</param>
    /// <param name="work">The work.</param>
    /// <param name="completed">
    ///     Told whether it worked, on the frame thread, exactly once — including when it was
    ///     cancelled, which is the path that produces no result at all.
    /// </param>
    void Run(
        string title,
        Func<BackgroundTask, List<ContentDiagnostic>, Task<Finished>> work,
        Action<bool>? completed = null
    ) {
        // ⚠ Compare-and-set rather than a bool. `Start` runs the work on the pool, so two commands
        // dispatched from one frame — a keybinding and a menu item, or an impatient double-click —
        // would both see `false` and both begin.
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) {
            shell.Notifications.Show("Already running", NotificationSeverity.Warning, "Wait for it to finish.");

            // Directly, because this arm has not left the frame thread. A caller that has already
            // marked something as under way has to be told, or the mark is permanent.
            completed?.Invoke(false);

            return;
        }

        shell.Tasks.Start(
            title,
            async task => {
                // Collected rather than reported as they arrive: a project with a thousand warnings
                // would be a thousand toasts, and the notification centre is not a build log. The
                // count goes in the summary and the first few go in its detail.
                List<ContentDiagnostic> diagnostics = [];
                var succeeded = false;

                try {
                    var result = await work(task, diagnostics).ConfigureAwait(false);

                    succeeded = result.Severity is not NotificationSeverity.Error;
                    finished.Enqueue(result with { Detail = Detail(result, diagnostics) });
                } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
                    finished.Enqueue(new(NotificationSeverity.Error, title + " failed", failure.Message));
                } finally {
                    Volatile.Write(ref running, 0);

                    // ⚠ In the `finally` rather than beside the enqueue, because cancellation leaves
                    // through here without producing a `Finished` at all — and a device row left
                    // saying Deploying after somebody pressed Cancel is a row they wait on for ever.
                    if (completed is not null) {
                        afterwards.Enqueue(() => completed(succeeded));
                    }
                }
            }
        );
    }

    /// <summary>The detail line: what the task said, plus what went wrong along the way.</summary>
    static string Detail(Finished result, List<ContentDiagnostic> diagnostics) {
        var loud = diagnostics
            .Where(diagnostic => diagnostic.Severity != ImportSeverity.Information)
            .ToList();

        if (loud.Count == 0) {
            return result.Detail;
        }

        var lines = loud
            .Take(3)
            .Select(diagnostic => diagnostic.Path.Length == 0 ? diagnostic.Message : $"{diagnostic.Path}: {diagnostic.Message}");

        var more = loud.Count > 3 ? $"{Environment.NewLine}and {loud.Count - 3} more" : string.Empty;

        return string.Join(
            Environment.NewLine,
            [result.Detail, .. lines]
        ) + more;
    }

    /// <summary>What a finished task wants said.</summary>
    readonly record struct Finished(NotificationSeverity Severity, string Title, string Detail = "");
}

/// <summary>What a player build is: which target, published how, from what, and to where.</summary>
/// <param name="Target">The platform, as <see cref="PlayerBuild.Targets" /> spells it.</param>
/// <param name="Shape">What that target publishes as.</param>
/// <param name="Variant">Which of doc 17's variants.</param>
/// <param name="ProjectFile">The <c>.csproj</c> that is the game.</param>
/// <param name="Output">Where the artefact goes.</param>
/// <param name="Launch">Whether to run what was built.</param>
/// <remarks>
///     Resolved on the frame thread and handed over whole, rather than read from the settings inside
///     the task. A build has to be a build of what was on screen when the button was pressed: the
///     panel is still editable while it runs, and a target read from the store two minutes later
///     would produce an artefact nobody asked for.
/// </remarks>
readonly record struct PlayerBuildRequest(
    string Target,
    TargetShape Shape,
    string Variant,
    string ProjectFile,
    string Output,
    bool Launch
);
