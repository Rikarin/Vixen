// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Assets;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;

namespace Vixen.Cli;

/// <summary>How much attention something the doctor found needs.</summary>
public enum Health {
    /// <summary>Worth knowing, and fine.</summary>
    Fine,

    /// <summary>Worth fixing, and the project still builds.</summary>
    Concerning,

    /// <summary>The project does not work, or will not build.</summary>
    Broken
}

/// <summary>One thing the doctor looked at.</summary>
/// <param name="Health">How it is.</param>
/// <param name="Subject">What was looked at.</param>
/// <param name="Detail">What was found, in a sentence that says what to do about it.</param>
public sealed record Finding(Health Health, string Subject, string Detail);

/// <summary>Looks at a project and says what is wrong with it, changing nothing.</summary>
/// <remarks>
///     <para>
///         <b>It repairs nothing, on purpose.</b> <c>vixen import</c> scans in the repairing mode —
///         a file with no sidecar gets one, an orphaned sidecar is quarantined — because that is what
///         opening a project does. A person asking what is wrong wants the answer, not a working tree
///         with edits in it, and a build server asking the same question wants it even more.
///         <c>ScanOptions.ReadOnly</c> exists for exactly this and this is its first caller.
///     </para>
///     <para>
///         Everything it checks is something that fails later and further away: an asset that was
///         never imported fails at the content build, a catalog naming a bundle that is not there
///         fails on a device, and a duplicate GUID fails when the wrong texture appears on a model.
///     </para>
/// </remarks>
public static class DoctorRunner {
    /// <summary>Examines a project.</summary>
    /// <param name="project">The project.</param>
    /// <param name="target">Which target's build to look at.</param>
    /// <param name="outputDirectory">Where that build is.</param>
    /// <returns>What it found, worst first.</returns>
    public static List<Finding> Examine(Project project, string target, string outputDirectory) {
        ArgumentNullException.ThrowIfNull(project);

        var findings = new List<Finding>();

        Directories(project, findings);
        Assets(project, findings);
        Groups(project, findings);
        Imports(project, findings);
        Content(project, target, outputDirectory, findings);

        // Worst first, and stable within a rank, so two runs over one project print the same thing
        // in the same order and a diff of two runs is only what changed.
        return [.. findings.OrderByDescending(finding => finding.Health)];
    }

    /// <summary>Prints findings, and says whether anything is broken.</summary>
    /// <param name="findings">What the doctor found.</param>
    /// <param name="output">Where to write them.</param>
    /// <returns>Whether the project is usable.</returns>
    public static bool Report(IEnumerable<Finding> findings, TextWriter output) {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(output);

        var broken = false;

        foreach (var finding in findings) {
            output.WriteLine($"  {Mark(finding.Health)} {finding.Subject}: {finding.Detail}");
            broken |= finding.Health == Health.Broken;
        }

        return !broken;
    }

    static void Directories(Project project, List<Finding> findings) {
        findings.Add(
            Directory.Exists(project.Paths.Assets)
                ? new(Health.Fine, "Assets/", project.Paths.Assets)
                : new(Health.Broken, "Assets/", $"there is no directory at '{project.Paths.Assets}'.")
        );

        try {
            Directory.CreateDirectory(project.Paths.Library);
            var probe = Path.Combine(project.Paths.Library, ".vixen-doctor");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            findings.Add(new(Health.Fine, "Library/", "writable."));
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            findings.Add(
                new(
                    Health.Broken,
                    "Library/",
                    $"cannot be written to ({failure.Message}), so nothing can be imported."
                )
            );
        }
    }

    static void Assets(Project project, List<Finding> findings) {
        // Read-only: the point of this command is to say what is wrong, and a scan that repaired
        // would make "is this project clean?" unanswerable by anything that had already run it.
        var scan = project.Database.Scan(ScanOptions.ReadOnly);

        findings.Add(
            new(
                Health.Fine,
                "assets",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{scan.Assets} indexed in {scan.Elapsed.TotalMilliseconds:F0} ms."
                )
            )
        );

        foreach (var issue in scan.Issues) {
            findings.Add(new(HealthOf(issue.Kind), issue.Path, issue.Message));
        }
    }

    static void Groups(Project project, List<Finding> findings) {
        var groups = project.Groups(out var unreadable);

        foreach (var failure in unreadable) {
            findings.Add(new(Health.Broken, ".vxgroup", failure));
        }

        findings.Add(
            new(
                Health.Fine,
                "groups",
                groups.Count == 0
                    ? "none defined, so anything addressable goes in the invented Default group."
                    : string.Join(", ", groups.Select(group => group.Name))
            )
        );
    }

    static void Imports(Project project, List<Finding> findings) {
        if (!File.Exists(project.CacheFile)) {
            findings.Add(new(Health.Concerning, "imports", "nothing has been imported yet. Run `vixen import`."));
            return;
        }

        var missing = new List<string>();
        var addressed = 0;

        foreach (var entry in project.Database.Entries) {
            if (entry.IsFolder) {
                continue;
            }

            if (!IsAddressable(project, entry)) {
                continue;
            }

            addressed++;

            if (!project.Cache.TryGet(entry.Guid, out var record) || record is null) {
                missing.Add(entry.Path);
            }
        }

        findings.Add(
            new(
                Health.Fine,
                "imports",
                string.Create(CultureInfo.InvariantCulture, $"{project.Cache.Count} cached, {addressed} addressable.")
            )
        );

        foreach (var path in missing.Order(StringComparer.Ordinal)) {
            findings.Add(
                new(
                    Health.Broken,
                    path,
                    "is addressable and has never been imported, so a content build would have no chunk for it."
                )
            );
        }
    }

    static void Content(Project project, string target, string outputDirectory, List<Finding> findings) {
        var catalogPath = Path.Combine(outputDirectory, ContentBuildRunner.CatalogFileName);

        if (!File.Exists(catalogPath)) {
            findings.Add(
                new(
                    Health.Concerning,
                    "content",
                    $"no build for {target} at '{outputDirectory}'. Run `vixen content build`."
                )
            );

            return;
        }

        ContentCatalog catalog;

        try {
            catalog = CatalogFormat.Read(File.ReadAllBytes(catalogPath));
        } catch (Exception failure) when (failure is IOException or InvalidDataException) {
            findings.Add(new(Health.Broken, "content", $"'{catalogPath}' could not be read: {failure.Message}"));
            return;
        }

        findings.Add(
            new(
                Health.Fine,
                "content",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{catalog.Count} addresses in {catalog.Bundles.Count} bundles, built for {catalog.Target}."
                )
            )
        );

        if (catalog.Target != target) {
            findings.Add(
                new(
                    Health.Concerning,
                    "content",
                    $"the build in '{outputDirectory}' is for {catalog.Target} and this is a {target} check."
                )
            );
        }

        // A local bundle the catalog names and the directory does not have is the failure that
        // reaches a device: everything resolves, and the load throws on the first address in it.
        foreach (var bundle in catalog.Bundles.OrderBy(bundle => bundle.Name, StringComparer.Ordinal)) {
            if (bundle.Url.Length > 0) {
                continue;
            }

            if (!Directory.EnumerateFiles(outputDirectory, "*.bundle").Any(file => Matches(file, bundle.Hash))) {
                findings.Add(
                    new(
                        Health.Broken,
                        "content",
                        $"the catalog names bundle '{bundle.Name}' and no file in '{outputDirectory}' has its hash."
                    )
                );
            }
        }
    }

    /// <summary>Whether a bundle file is the one a catalog entry names, by the hash in its name.</summary>
    static bool Matches(string file, Core.ObjectId hash) {
        var name = Path.GetFileNameWithoutExtension(file);
        var text = hash.ToString();

        return name.EndsWith(text[..16], StringComparison.Ordinal)
            || ContentHash.Compute(File.ReadAllBytes(file)) == hash;
    }

    static bool IsAddressable(Project project, AssetEntry entry) {
        try {
            var meta = AssetMetaFile.ReadFile(AssetMetaFile.PathFor(project.Paths.Absolute(entry.Path)));
            return meta.Addressable?.Address is { Length: > 0 };
        } catch (Exception failure) when (failure is IOException or Core.Yaml.YamlParseException
                                              or Core.Yaml.YamlBindingException) {
            // The scan has already reported an unreadable sidecar with its own message.
            return false;
        }
    }

    static Health HealthOf(AssetIssueKind kind) =>
        kind switch {
            AssetIssueKind.MetaUnreadable or AssetIssueKind.DuplicateGuid => Health.Broken,
            _ => Health.Concerning
        };

    static string Mark(Health health) =>
        health switch {
            Health.Broken => "broken ",
            Health.Concerning => "check  ",
            _ => "ok     "
        };
}
