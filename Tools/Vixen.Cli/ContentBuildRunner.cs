// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Assets;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;

namespace Vixen.Cli;

/// <summary>Turns an imported project into a directory a game or a CDN can be pointed at.</summary>
/// <remarks>
///     The three steps doc 08 separates, run in order: plan what to pack, pack it, write it down.
///     Each is a component that exists already; what lives here is the part that has to touch a
///     filesystem, which is why it is the part that had nowhere to be until there was a CLI.
/// </remarks>
public static class ContentBuildRunner {
    /// <summary>What the build wrote, beside the catalog, so a static host needs nothing else.</summary>
    /// <remarks>
    ///     <c>ContentUpdate</c> reads the hash before the catalog, because a hash is tiny and a real
    ///     catalog is not — and it reads it from the catalog's URL with <c>.hash</c> appended.
    ///     <c>Vixen.ContentServer</c> synthesises it for a directory that has none; a CDN will not, so
    ///     the build writes it.
    /// </remarks>
    public const string HashFileSuffix = ".hash";

    /// <summary>The catalog's file name, which is what a runtime and a server both expect.</summary>
    public const string CatalogFileName = "catalog.bin";

    /// <summary>Plans, packs and writes a content build.</summary>
    /// <param name="project">The project, already imported.</param>
    /// <param name="target">Which build target — <c>Windows</c>, <c>Android/Vulkan</c>.</param>
    /// <param name="outputDirectory">Where to write it.</param>
    /// <param name="output">Where to write progress and diagnostics.</param>
    /// <returns>Whether it produced a build.</returns>
    public static bool Run(Project project, string target, string outputDirectory, DiagnosticWriter output) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(output);

        var groups = project.Groups(out var unreadable);

        foreach (var failure in unreadable) {
            output.Project(ImportSeverity.Error, DiagnosticCode.Plan, failure);
        }

        if (unreadable.Count > 0) {
            return false;
        }

        var plan = BuildPlanner.Plan(
            project.Database.Entries,
            project.Cache,
            entry => ReadMeta(project, entry),
            groups
        );

        foreach (var diagnostic in plan.Diagnostics) {
            output.Project(diagnostic.Severity, DiagnosticCode.Plan, diagnostic.Message);
        }

        if (!plan.Succeeded) {
            return false;
        }

        if (plan.Assets.Length == 0) {
            // Not an error. A project can legitimately have nothing addressable yet, and a build with
            // an empty catalog is what a game with no content loads — but silence here would look
            // like success at packing everything.
            output.Project(
                ImportSeverity.Information,
                DiagnosticCode.Plan,
                "Nothing in this project has an address, so the catalog is empty."
            );
        }

        var built = new ContentBuilder(target).Build(plan.Groups, plan.Assets, project.Chunks);

        foreach (var diagnostic in built.Diagnostics) {
            output.Project(diagnostic.Severity, DiagnosticCode.Pack, diagnostic.Message);
        }

        if (built.Diagnostics.Any(diagnostic => diagnostic.Severity == ImportSeverity.Error)) {
            return false;
        }

        Write(built, outputDirectory);

        var bytes = built.Bundles.Sum(bundle => (long)bundle.Bytes.Length);

        output.Line(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Built {plan.Assets.Length} {Plural(plan.Assets.Length, "address", "addresses")} into "
                + $"{built.Bundles.Length} {Plural(built.Bundles.Length, "bundle", "bundles")} ({bytes:N0} bytes) "
                + $"for {target}, at {outputDirectory}."
            )
        );

        return true;
    }

    /// <summary>Writes the bundles, the catalog and the catalog's hash.</summary>
    /// <remarks>
    ///     <b>The files this tool owns are removed first.</b> Bundle file names carry a content hash,
    ///     so a rebuild of changed content writes new names and leaves the old ones behind — and a
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

    static AssetMeta? ReadMeta(Project project, AssetEntry entry) {
        try {
            return AssetMetaFile.ReadFile(AssetMetaFile.PathFor(project.Paths.Absolute(entry.Path)));
        } catch (Exception failure) when (failure is IOException or YamlParseException or YamlBindingException) {
            // Reported by the planner as "no readable sidecar", with the path attached, which is the
            // message somebody can act on. The exception's is about a byte offset.
            return null;
        }
    }

    static string Plural(int count, string one, string many) => count == 1 ? one : many;
}
