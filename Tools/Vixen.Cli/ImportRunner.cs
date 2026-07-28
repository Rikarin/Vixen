// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.AssetCompiler;
using Vixen.Editor.Assets.Content;

namespace Vixen.Cli;

/// <summary>Runs the import pipeline over a project and says what happened.</summary>
/// <remarks>
///     ⚠ <b>The import itself is <see cref="ContentPipeline" />'s, in <c>Vixen.Editor.Assets</c>.</b>
///     What is left here is the console and the worker pool — the first because a tool and an editor
///     want different output, and the second because process isolation is a command-line option and
///     not something an editor's background task should be starting behind somebody's back.
/// </remarks>
public static class ImportRunner {
    /// <summary>Imports everything in a project that needs it.</summary>
    /// <param name="project">The project.</param>
    /// <param name="target">Which build target to import for.</param>
    /// <param name="output">Where to write progress and diagnostics.</param>
    /// <param name="verbose">Whether to name every asset, rather than only the ones with something to say.</param>
    /// <param name="isolated">Whether to run importers in worker processes.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What it did.</returns>
    public static async Task<ImportSummary> RunAsync(
        Project project,
        string target,
        DiagnosticWriter output,
        bool verbose,
        bool isolated = false,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);

        // Worker processes, when asked for. Doc 08's reason is crash isolation and not speed: an
        // importer that *throws* is already caught, and an importer that takes its process down —
        // a malformed FBX inside a C++ library — is not catchable from inside that process. Off by
        // default because it costs a process start and a copy of every artefact over a pipe, and the
        // failure it protects against is rare enough to be worth asking for.
        using var pool = isolated ? new CompilerPool(project.Paths.Root) : null;

        if (pool is not null) {
            output.Line($"  Importing in {pool.WorkerCount} worker process(es).");
        }

        return await ContentPipeline.ImportAsync(
            project.Workspace,
            target,
            diagnostic => ContentBuildRunner.Write(output, diagnostic),
            verbose ? step => Verbose(output, step) : null,
            pool,
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>Names one asset under <c>--verbose</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the ones that had nothing to say.</b> An asset with a diagnostic has already had
    ///     it printed, with its path attached, and naming it again would put every warned-about file
    ///     in the log twice — once as a warning and once as a line saying it was fine.
    /// </remarks>
    static void Verbose(DiagnosticWriter output, ImportProgress step) {
        if (step.Outcome.Importer is not { } importer || step.Outcome.Diagnostics.Count > 0) {
            return;
        }

        output.Line($"  {(step.Outcome.WasCached ? "cached " : "import ")} {step.Path} ({importer})");
    }

    /// <summary>Prints the one line a person reads when nothing went wrong.</summary>
    /// <param name="summary">What the import did.</param>
    /// <param name="output">Where to write it.</param>
    public static void Report(ImportSummary summary, DiagnosticWriter output) {
        ArgumentNullException.ThrowIfNull(output);

        output.Line(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Imported {summary.Imported}, {summary.Cached} unchanged, {summary.Failed} failed in "
                + $"{summary.Elapsed.TotalMilliseconds:F0} ms."
            )
        );
    }
}
