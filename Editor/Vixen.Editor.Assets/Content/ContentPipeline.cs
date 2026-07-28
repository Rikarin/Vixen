// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Assets;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;

namespace Vixen.Editor.Assets.Content;

/// <summary>Which part of the pipeline said something.</summary>
public enum ContentStage : byte {
    /// <summary>The database scan that runs first.</summary>
    Scan,

    /// <summary>An importer.</summary>
    Import,

    /// <summary>Working out what to pack.</summary>
    Plan,

    /// <summary>Packing it.</summary>
    Pack
}

/// <summary>Something the pipeline said, with enough attached to act on it.</summary>
/// <param name="Severity">How much attention it needs.</param>
/// <param name="Stage">Which part said it.</param>
/// <param name="Path">Which asset it is about, project-relative, or empty for the project as a whole.</param>
/// <param name="Message">What happened, in a sentence a person can act on.</param>
public readonly record struct ContentDiagnostic(
    ImportSeverity Severity,
    ContentStage Stage,
    string Path,
    string Message
);

/// <summary>One asset the import has finished with.</summary>
/// <param name="Fraction">How far through the import this leaves it, from zero to one.</param>
/// <param name="Path">Which asset, project-relative.</param>
/// <param name="Outcome">What happened to it.</param>
/// <remarks>
///     Carries the whole outcome rather than a path and a percentage, because what a caller wants to
///     say about an asset differs: a progress bar wants the fraction, a verbose console log wants
///     whether it was cached and which importer claimed it.
/// </remarks>
public readonly record struct ImportProgress(float Fraction, string Path, ImportOutcome Outcome);

/// <summary>What an import of a whole project did.</summary>
/// <param name="Imported">How many assets ran an importer.</param>
/// <param name="Cached">How many were skipped because nothing about them had changed.</param>
/// <param name="Failed">How many came out with an error.</param>
/// <param name="Elapsed">How long it took.</param>
public readonly record struct ImportSummary(int Imported, int Cached, int Failed, TimeSpan Elapsed) {
    /// <summary>Whether anything failed.</summary>
    public bool Succeeded => Failed == 0;
}

/// <summary>What a content build produced.</summary>
/// <param name="Succeeded">Whether it wrote a build.</param>
/// <param name="Addresses">How many addressable assets were packed.</param>
/// <param name="Bundles">How many bundles they went into.</param>
/// <param name="Bytes">How big those bundles are, in total.</param>
/// <param name="OutputDirectory">Where it was written, or empty if nothing was.</param>
public readonly record struct ContentBuildSummary(
    bool Succeeded,
    int Addresses,
    int Bundles,
    long Bytes,
    string OutputDirectory
);

/// <summary>Importing and building a project, as calls that return data rather than print it.</summary>
/// <remarks>
///     <para>
///         <b>The orchestration the CLI had, with the console taken out of it.</b> Each step is a
///         component that already existed; what lives here is the order they go in and the part that
///         has to touch a filesystem. It reports through a callback rather than to a writer, because
///         an editor puts a diagnostic in a panel and a tool puts it on stdout, and neither should
///         own the other's output format.
///     </para>
///     <para>
///         ⚠ <b>A build packs what the import produced, so the two are ordered and not alternatives.</b>
///         The plan reads the import cache; a build run against a project that has never been imported
///         plans nothing and writes an empty catalog, which looks exactly like a build that worked.
///         Callers that mean "make me a build" call both.
///     </para>
/// </remarks>
public static class ContentPipeline {
    /// <summary>What the build writes beside the catalog, so a static host needs nothing else.</summary>
    /// <remarks>
    ///     <c>ContentUpdate</c> reads the hash before the catalog, because a hash is tiny and a real
    ///     catalog is not — and it reads it from the catalog's URL with <c>.hash</c> appended.
    ///     <c>Vixen.ContentServer</c> synthesises it for a directory that has none; a CDN will not, so
    ///     the build writes it.
    /// </remarks>
    public const string HashFileSuffix = ".hash";

    /// <summary>The catalog's file name, which is what a runtime and a server both expect.</summary>
    public const string CatalogFileName = "catalog.bin";

    /// <summary>Scans, then imports everything that needs it.</summary>
    /// <param name="workspace">The project.</param>
    /// <param name="target">Which build target to import for.</param>
    /// <param name="report">Where diagnostics go.</param>
    /// <param name="progress">Called once per asset the import has finished with, or null.</param>
    /// <param name="executor">An executor to use instead of the in-process one — worker processes.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What it did.</returns>
    /// <remarks>
    ///     <b>The scan repairs.</b> A file with no sidecar gets one, an orphaned sidecar is
    ///     quarantined, and two assets claiming a GUID are separated — which is what opening a
    ///     project does, and what somebody asking for an import is asking for. Looking without
    ///     touching is <see cref="ScanOptions.ReadOnly" />'s job.
    /// </remarks>
    public static async Task<ImportSummary> ImportAsync(
        ProjectWorkspace workspace,
        string target,
        Action<ContentDiagnostic> report,
        Action<ImportProgress>? progress = null,
        IImportExecutor? executor = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(report);

        var clock = Stopwatch.StartNew();

        foreach (var issue in workspace.Database.Scan().Issues) {
            report(new(SeverityOf(issue.Kind), ContentStage.Scan, issue.Path, issue.Message));
        }

        var pipeline = new ImportPipeline(
            workspace.Database,
            ProjectWorkspace.Importers(),
            workspace.Artifacts,
            workspace.Files,
            workspace.Cache
        ) {
            Target = target
        };

        if (executor is not null) {
            pipeline.Executor = executor;
        }

        var pathOf = workspace.Database.Entries.ToDictionary(entry => entry.Guid, entry => entry.Path);
        var outcomes = await pipeline.ImportAllAsync(cancellationToken).ConfigureAwait(false);
        var imported = 0;
        var cached = 0;
        var failed = 0;
        var seen = 0;

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
                report(new(diagnostic.Severity, ContentStage.Import, path, diagnostic.Message));
            }

            // ⚠ Reported after the import rather than during it. `ImportAllAsync` returns when it is
            // finished, so this is a walk over what happened and not a live progress feed — which is
            // honest for a bar that fills at the end, and is what the pipeline's own shape allows
            // until it takes a progress callback of its own.
            progress?.Invoke(new(++seen / (float) Math.Max(outcomes.Count, 1), path, outcome));
        }

        workspace.Save();

        return new(imported, cached, failed, clock.Elapsed);
    }

    /// <summary>Plans, packs and writes a content build.</summary>
    /// <param name="workspace">The project, already imported.</param>
    /// <param name="target">Which build target — <c>Windows</c>, <c>Android/Vulkan</c>.</param>
    /// <param name="outputDirectory">Where to write it.</param>
    /// <param name="report">Where diagnostics go.</param>
    /// <returns>What it produced.</returns>
    public static ContentBuildSummary Build(
        ProjectWorkspace workspace,
        string target,
        string outputDirectory,
        Action<ContentDiagnostic> report
    ) {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(report);

        var groups = workspace.Groups(out var unreadable);

        foreach (var failure in unreadable) {
            report(new(ImportSeverity.Error, ContentStage.Plan, string.Empty, failure));
        }

        if (unreadable.Count > 0) {
            return default;
        }

        var plan = BuildPlanner.Plan(
            workspace.Database.Entries,
            workspace.Cache,
            entry => ReadMeta(workspace, entry),
            groups
        );

        foreach (var diagnostic in plan.Diagnostics) {
            report(new(diagnostic.Severity, ContentStage.Plan, string.Empty, diagnostic.Message));
        }

        if (!plan.Succeeded) {
            return default;
        }

        if (plan.Assets.Length == 0) {
            // Not an error. A project can legitimately have nothing addressable yet, and a build with
            // an empty catalog is what a game with no content loads — but silence here would look
            // like success at packing everything.
            report(
                new(
                    ImportSeverity.Information,
                    ContentStage.Plan,
                    string.Empty,
                    "Nothing in this project has an address, so the catalog is empty."
                )
            );
        }

        var built = new ContentBuilder(target).Build(plan.Groups, plan.Assets, workspace.Chunks);

        foreach (var diagnostic in built.Diagnostics) {
            report(new(diagnostic.Severity, ContentStage.Pack, string.Empty, diagnostic.Message));
        }

        if (built.Diagnostics.Any(diagnostic => diagnostic.Severity == ImportSeverity.Error)) {
            return default;
        }

        Write(built, outputDirectory);

        return new(
            true,
            plan.Assets.Length,
            built.Bundles.Length,
            built.Bundles.Sum(bundle => (long) bundle.Bytes.Length),
            outputDirectory
        );
    }

    /// <summary>How loud an issue the scan found is.</summary>
    /// <param name="kind">What the scan found.</param>
    /// <returns>How much attention it needs.</returns>
    /// <remarks>
    ///     ⚠ <b>None of these is an error, and a created sidecar is not even a warning.</b> Every one
    ///     of them is something the scan <i>repaired</i> — see the database's own "nothing is silently
    ///     tolerant" table — and a first import of a project written by hand creates a sidecar per
    ///     file. Raising that to a warning would make a clean import of a new project produce a
    ///     hundred of them, which is how a build log stops being read.
    /// </remarks>
    public static ImportSeverity SeverityOf(AssetIssueKind kind) =>
        kind switch {
            AssetIssueKind.MetaUnreadable or AssetIssueKind.DuplicateGuid => ImportSeverity.Warning,
            _ => ImportSeverity.Information
        };

    /// <summary>Writes the bundles, the catalog and the catalog's hash.</summary>
    /// <remarks>
    ///     <b>The files this owns are removed first.</b> Bundle file names carry a content hash, so a
    ///     rebuild of changed content writes new names and leaves the old ones behind — and a
    ///     directory that accumulates every bundle ever built is one somebody eventually uploads.
    ///     Only <c>*.bundle</c>, the catalog and its hash are deleted, because a build directory is
    ///     also where a person puts the one-line script that publishes it.
    /// </remarks>
    static void Write(ContentBuildResult built, string outputDirectory) {
        Directory.CreateDirectory(outputDirectory);

        foreach (var stale in Directory.EnumerateFiles(outputDirectory, "*.bundle")) {
            File.Delete(stale);
        }

        foreach (var bundle in built.Bundles) {
            File.WriteAllBytes(Path.Combine(outputDirectory, bundle.FileName), bundle.Bytes.Span);
        }

        var catalog = CatalogFormat.Write(built.Catalog);
        var catalogPath = Path.Combine(outputDirectory, CatalogFileName);

        File.WriteAllBytes(catalogPath, catalog);
        File.WriteAllText(catalogPath + HashFileSuffix, ContentHash.Compute(catalog).ToString());
    }

    static AssetMeta? ReadMeta(ProjectWorkspace workspace, AssetEntry entry) {
        try {
            return AssetMetaFile.ReadFile(AssetMetaFile.PathFor(workspace.Paths.Absolute(entry.Path)));
        } catch (Exception failure) when (failure is IOException or YamlParseException or YamlBindingException) {
            // Reported by the planner as "no readable sidecar", with the path attached, which is the
            // message somebody can act on. The exception's is about a byte offset.
            return null;
        }
    }
}
