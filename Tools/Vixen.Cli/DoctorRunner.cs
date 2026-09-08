// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Assets;
using Vixen.Core.Serialization.Storage;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;
using Vixen.Graphics;

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
///     <para>
///         ⚠ <b>One of its findings is not about the project at all</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1094">#1094</a>. <c>Machine</c> asks
///         whether this box has a Vulkan device, because <c>doctor</c> is the verb whose whole job is
///         "what is wrong here" and that was the one question it could not answer: the only other way
///         to ask was to run a bake. It is deliberately never <see cref="Health.Broken" /> — see that
///         method for the exit-code argument.
///     </para>
/// </remarks>
public static class DoctorRunner {
    /// <summary>Examines a project, and the machine it would run on.</summary>
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
        findings.Add(Machine());

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

    /// <summary>Whether this machine can run compute, which is the one question about the machine.</summary>
    /// <returns>What was found.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1094">#1094</a>: every other check
    ///         here is about the project and this is not.</b> The person who needs it is the one whose
    ///         <c>texture bake --graph</c> has just been refused on a container image somebody else
    ///         wrote — and whose only other way to ask "does this machine have a device" is to run a
    ///         bake, which needs a compiled graph, a project and a name, and fails for a dozen reasons
    ///         that are not the machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="Health.Concerning" /> and never <see cref="Health.Broken" />, and that
    ///         is the exit-code decision rather than a shade of wording.</b> <see cref="Report" />
    ///         fails the command on <c>Broken</c> alone, and this command's contract is <c>0</c> did
    ///         what was asked, <c>1</c> the project is wrong, <c>2</c> the invocation was wrong. A
    ///         machine with no adapter is not a wrong project, so reporting it as broken would make
    ///         every <c>dotnet</c> container image look like a broken checkout and make <c>doctor</c>
    ///         useless in CI — which is where it is most useful. It is information at exit 0, like
    ///         "this project has no addressable assets".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two states that read alike are kept apart</b>, which is what
    ///         <c>HeadlessGraphics.Refusal</c>'s own remarks are about: a machine with no adapter and
    ///         a machine with no <c>libvulkan</c> on the search path are different problems with
    ///         different fixes. So the driver's own sentence is printed and this file never writes
    ///         "no GPU" over the top of it. The unwrapped overload is taken because the wrapped one
    ///         ends with advice about a verb the reader may not have run.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it cannot go quiet.</b> A probe that swallowed an exception and printed
    ///         nothing would be indistinguishable from a healthy machine — this repository's most
    ///         frequently named failure — so the <c>catch</c> reports rather than returning, and its
    ///         sentence says the probe itself failed, which is a third thing neither of the other two
    ///         prints.
    ///     </para>
    /// </remarks>
    static Finding Machine() {
        IGraphicsDevice? device = null;

        try {
            return HeadlessGraphics.TryOpen(out device, out _, out var driver)
                ? Machine(HeadlessGraphics.Adapter(device!), null)
                : Machine(null, driver);
        } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            // ⚠ Reported and not swallowed. `VulkanLoader` reaches a native library through a P/Invoke
            // and a machine can fail it in ways `TryCreate` does not turn into a refusal — and a
            // `doctor` that printed nothing for those would be a `doctor` that says a machine is fine
            // by not looking.
            return new(
                Health.Concerning,
                "GPU",
                $"the probe itself failed ({failure.GetType().Name}: {failure.Message}), so whether this "
                + "machine has a device is unknown rather than answered."
            );
        } finally {
            device?.Dispose();
        }
    }

    /// <summary>The sentence for one outcome of the probe.</summary>
    /// <param name="adapter">The adapter's own name, or <see langword="null" /> if none opened.</param>
    /// <param name="driver">
    ///     What the driver said when none opened, unwrapped, or <see langword="null" /> when one did.
    /// </param>
    /// <returns>What to print.</returns>
    /// <remarks>
    ///     ⚠ <b>Split out so the two outcomes can be compared without a machine that has neither.</b>
    ///     Only one of them is reachable on any given box, so a test that ran the probe could assert
    ///     one branch and would be blind to whether the other says anything different — which is the
    ///     failure <a href="https://github.com/Rikarin/Vixen/issues/1094">#1094</a> names in as many
    ///     words. <c>DoctorGraphicsTests</c> asks both.
    /// </remarks>
    internal static Finding Machine(string? adapter, string? driver) =>
        adapter is { Length: > 0 }
            ? new(Health.Fine, "GPU", $"{adapter}, through Vulkan.")
            : new(
                Health.Concerning,
                "GPU",
                (driver is { Length: > 0 } ? driver.TrimEnd() : "the Vulkan backend refused and said nothing about why.")
                + " Nothing in a project can fix that, so this is information rather than a fault: only "
                + "the verbs that dispatch — texture bake --graph — need one."
            );

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

        return name.EndsWith(text[..BundleFile.HashPrefixLength], StringComparison.Ordinal)
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
