// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;

namespace Vixen.Cli;

/// <summary>What an import of a whole project did.</summary>
/// <param name="Imported">How many assets ran an importer.</param>
/// <param name="Cached">How many were skipped because nothing about them had changed.</param>
/// <param name="Failed">How many came out with an error.</param>
/// <param name="Elapsed">How long it took.</param>
public readonly record struct ImportSummary(int Imported, int Cached, int Failed, TimeSpan Elapsed);

/// <summary>Runs the import pipeline over a project and says what happened.</summary>
/// <remarks>
///     Separate from the command that parses the arguments, because <c>content build</c> imports
///     before it packs and neither of them should own the other's output format.
/// </remarks>
public static class ImportRunner {
    /// <summary>Imports everything in a project that needs it.</summary>
    /// <param name="project">The project.</param>
    /// <param name="target">Which build target to import for.</param>
    /// <param name="output">Where to write progress and diagnostics.</param>
    /// <param name="verbose">Whether to name every asset, rather than only the ones with something to say.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What it did.</returns>
    /// <remarks>
    ///     <b>The scan repairs.</b> A file with no sidecar gets one, an orphaned sidecar is
    ///     quarantined, and two assets claiming a GUID are separated — which is what opening a
    ///     project does, and what a person running <c>vixen import</c> is asking for. <c>doctor</c>
    ///     is the one that looks without touching.
    /// </remarks>
    public static async Task<ImportSummary> RunAsync(
        Project project,
        string target,
        DiagnosticWriter output,
        bool verbose,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);

        var clock = Stopwatch.StartNew();
        var scan = project.Database.Scan();

        foreach (var issue in scan.Issues) {
            output.Asset(DiagnosticWriter.SeverityOf(issue.Kind), issue.Path, DiagnosticCode.Scan, issue.Message);
        }

        var pipeline = new ImportPipeline(
            project.Database,
            Project.Importers(),
            project.Artifacts,
            project.Files,
            project.Cache
        ) {
            Target = target
        };

        var pathOf = project.Database.Entries.ToDictionary(entry => entry.Guid, entry => entry.Path);
        var outcomes = await pipeline.ImportAllAsync(cancellationToken).ConfigureAwait(false);
        var imported = 0;
        var cached = 0;
        var failed = 0;

        foreach (var outcome in outcomes) {
            var path = pathOf.GetValueOrDefault(outcome.Asset, outcome.Asset.ToString());

            if (!outcome.Succeeded) {
                failed++;
            } else if (outcome.WasCached) {
                cached++;
            } else if (outcome.Importer is not null) {
                imported++;
            }

            foreach (var diagnostic in outcome.Diagnostics) {
                output.Asset(diagnostic.Severity, path, DiagnosticCode.Import, diagnostic.Message);
            }

            if (verbose && outcome.Importer is not null && outcome.Diagnostics.Count == 0) {
                output.Line($"  {(outcome.WasCached ? "cached " : "import ")} {path} ({outcome.Importer})");
            }
        }

        project.Save();

        return new(imported, cached, failed, clock.Elapsed);
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
