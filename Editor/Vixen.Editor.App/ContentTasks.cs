// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;

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
    }

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

    /// <summary>Shows what any finished task had to say. Called once a frame, on the frame thread.</summary>
    public void Pump() {
        while (finished.TryDequeue(out var result)) {
            // ⚠ Rescanned here rather than inside the task. An import repairs sidecars in a database
            // of its own — see `ProjectWorkspace` for why it is not the editor's — so the panels are
            // showing what was true before it ran until the editor's own index is rebuilt, which has
            // to happen on the thread that owns them.
            Rescan?.Invoke();

            shell.Notifications.Show(result.Title, result.Severity, result.Detail);
        }
    }

    /// <summary>What to do when a task has finished and the panels are stale.</summary>
    public Action? Rescan { get; set; }

    void Run(string title, Func<BackgroundTask, List<ContentDiagnostic>, Task<Finished>> work) {
        // ⚠ Compare-and-set rather than a bool. `Start` runs the work on the pool, so two commands
        // dispatched from one frame — a keybinding and a menu item, or an impatient double-click —
        // would both see `false` and both begin.
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) {
            shell.Notifications.Show("Already running", NotificationSeverity.Warning, "Wait for it to finish.");
            return;
        }

        shell.Tasks.Start(
            title,
            async task => {
                // Collected rather than reported as they arrive: a project with a thousand warnings
                // would be a thousand toasts, and the notification centre is not a build log. The
                // count goes in the summary and the first few go in its detail.
                List<ContentDiagnostic> diagnostics = [];

                try {
                    var result = await work(task, diagnostics).ConfigureAwait(false);
                    finished.Enqueue(result with { Detail = Detail(result, diagnostics) });
                } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
                    finished.Enqueue(new(NotificationSeverity.Error, title + " failed", failure.Message));
                } finally {
                    Volatile.Write(ref running, 0);
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
