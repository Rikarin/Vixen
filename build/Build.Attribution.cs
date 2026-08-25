// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

/// <summary>
///     The attribution manifest, checked against the files that pin what it attributes.
/// </summary>
/// <remarks>
///     <para>
///         Spec: ADR-015 (docs/plan/01 § Licence), which requires a third-party manifest covering the
///         managed packages and the native binaries. The manifest itself is
///         <c>docs/manual/third-party.md</c>; this is the half that stops it lying.
///     </para>
///     <para>
///         <b>Why a gate and not a documented refresh step.</b> A hand-written attribution list is
///         wrong one package bump after it is written, and nothing about a bump makes anyone open it.
///         The repository has learned this often enough to have made a rule of it — a documented
///         instrument that does not run is worse than no instrument, because it is believed. So the
///         inventory is enforced rather than requested: <see cref="CheckFormat" /> fails on a pinned
///         package with no row, a row naming a package that is no longer pinned, and a row whose
///         version has drifted from the pin.
///     </para>
///     <para>
///         ⚠ <b>What this gate does NOT check, and the distinction is the whole of its honesty.</b>
///         It checks the <em>inventory</em> — that the set of things attributed is the set of things
///         depended on, at the versions depended on. It does <b>not</b> check that the licence named
///         beside each row is the licence that package actually carries. It cannot: that is a claim
///         about a third party's metadata, and verifying it needs either a network fetch (which would
///         make the gate flaky and offline-hostile) or a restored package cache (which a clean
///         checkout does not have). The licence column is therefore verified by a person, once, when
///         the row is added, and the manifest records the <em>source</em> of each determination so
///         the next reader can re-check it rather than trust it. The rot this gate catches is the
///         rot that actually happens — a dependency added or bumped and the notice not followed.
///     </para>
///     <para>
///         The two sections are delimited by HTML comments rather than by heading text, so that
///         rewording a heading cannot silently take rows out of scope. A marker that is missing, or a
///         section that parses to implausibly few rows, fails the gate for that reason rather than by
///         finding nothing to disagree with — which is the failure mode
///         <see cref="CheckLicenceHeaders" />'s row-count floor exists to prevent, for the same
///         reason.
///     </para>
/// </remarks>
partial class Build {
    AbsolutePath AttributionPage => RootDirectory / "docs" / "manual" / "third-party.md";

    AbsolutePath PackagesProps => RootDirectory / "Directory.Packages.props";

    /// <summary>The managed section's fence. Invisible when rendered, unambiguous when parsed.</summary>
    const string ManagedBegin = "<!-- attribution:managed:begin -->";

    const string ManagedEnd = "<!-- attribution:managed:end -->";
    const string NativeBegin = "<!-- attribution:native:begin -->";
    const string NativeEnd = "<!-- attribution:native:end -->";

    /// <summary>
    ///     A pin in <c>Directory.Packages.props</c>. Central package management makes this file the
    ///     authority — a csproj carrying an inline version is an NU1008 error — so the set of
    ///     <c>PackageVersion</c> elements is the set of managed dependencies, with no second place to
    ///     look.
    /// </summary>
    [GeneratedRegex("""<PackageVersion\s+Include="(?<id>[^"]+)"\s+Version="(?<version>[^"]+)"\s*/>""")]
    private static partial Regex PackagePin();

    /// <summary>
    ///     A manifest row: <c>| `id` | version | …</c>. The backticks on the first cell are what
    ///     distinguishes a row from the header and separator lines of the same table, so a table can
    ///     be split under sub-headings for readability without the gate losing track of it.
    /// </summary>
    [GeneratedRegex(@"^\|\s*`(?<id>[^`]+)`\s*\|\s*(?<version>[^|]+?)\s*\|")]
    private static partial Regex ManifestRow();

    /// <summary>
    ///     The gate on its own, so that it can be run — and seen to run — without the two minute-long
    ///     <c>dotnet format</c> passes <see cref="CheckFormat" /> also carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not a second implementation, and that is the point of it being three lines.</b> An
    ///     instrument nobody can run alone is an instrument nobody checks, and one that has never
    ///     been watched failing is indistinguishable from one that cannot fail. Break a row in the
    ///     manifest and run this: it should name the row.
    /// </remarks>
    Target CheckAttribution => definition => definition
        .Description("Fails if docs/manual/third-party.md and the dependency pins disagree")
        .Executes(CheckAttributionManifest);

    /// <summary>
    ///     Fails <see cref="CheckFormat" /> when the attribution manifest and the files that pin the
    ///     dependencies disagree about what is depended on.
    /// </summary>
    /// <remarks>
    ///     Every disagreement is reported, not the first — a gate that stops at one turns a five-row
    ///     omission into five runs of the target, which is the same argument
    ///     <see cref="CheckLicenceHeaders" /> makes about naming every unheaded file.
    /// </remarks>
    void CheckAttributionManifest() {
        Assert.FileExists(AttributionPage);
        Assert.FileExists(PackagesProps);

        var page = AttributionPage.ReadAllText();
        var problems = new List<string>();

        var pinned = PackagePin()
            .Matches(PackagesProps.ReadAllText())
            .ToDictionary(match => match.Groups["id"].Value, match => match.Groups["version"].Value, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            pinned.Count > 30,
            $"found only {pinned.Count} pinned packages in {PackagesProps.Name}, which is too few to "
            + "be the whole file — PackagePin() no longer matches how versions are written."
        );

        Compare(
            "managed package",
            pinned,
            Rows(page, ManagedBegin, ManagedEnd, "managed", problems),
            $"`{PackagesProps.Name}`",
            problems
        );

        var natives = ReadNativeManifest()
            .Dependencies
            .ToDictionary(dependency => dependency.Id, dependency => dependency.Version, StringComparer.OrdinalIgnoreCase);

        Compare(
            "native dependency",
            natives,
            Rows(page, NativeBegin, NativeEnd, "native", problems),
            $"`{RootDirectory.GetRelativePathTo(NativeManifestFile)}`",
            problems
        );

        foreach (var problem in problems) {
            Log.Error("{Problem}", problem);
        }

        Assert.True(
            problems.Count == 0,
            $"{problems.Count} disagreement(s) between docs/manual/third-party.md and the files that "
            + "pin the dependencies. A dependency added, removed or bumped is an edit to the manifest "
            + "in the same commit — and the licence column is a claim somebody has to verify, so add "
            + "the row by hand rather than by copying the version across."
        );

        Log.Information(
            "Attribution manifest agrees with {Managed} pinned packages and {Native} native dependencies.",
            pinned.Count,
            natives.Count
        );
    }

    /// <summary>The rows of one delimited section, keyed by id.</summary>
    /// <remarks>
    ///     A missing marker is reported as itself. Silently returning nothing would make the gate pass
    ///     on a page whose sections had been renamed away, which is the one outcome worse than
    ///     failing.
    /// </remarks>
    static Dictionary<string, string> Rows(
        string page,
        string begin,
        string end,
        string section,
        List<string> problems
    ) {
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var from = page.IndexOf(begin, StringComparison.Ordinal);
        var to = page.IndexOf(end, StringComparison.Ordinal);

        if (from < 0 || to < from) {
            problems.Add($"docs/manual/third-party.md: the {section} section is not delimited by "
                + $"`{begin}` … `{end}`, so nothing in it can be checked.");

            return rows;
        }

        foreach (var line in page[(from + begin.Length)..to].Split('\n')) {
            var match = ManifestRow().Match(line.Trim());

            if (!match.Success) {
                continue;
            }

            var id = match.Groups["id"].Value.Trim();

            if (!rows.TryAdd(id, match.Groups["version"].Value.Trim())) {
                problems.Add($"docs/manual/third-party.md: `{id}` has two rows in the {section} section.");
            }
        }

        return rows;
    }

    /// <summary>The set difference, in both directions, plus the versions that drifted.</summary>
    static void Compare(
        string noun,
        Dictionary<string, string> pinned,
        Dictionary<string, string> documented,
        string authority,
        List<string> problems
    ) {
        foreach (var (id, version) in pinned.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
            if (!documented.TryGetValue(id, out var documentedVersion)) {
                problems.Add($"docs/manual/third-party.md: {noun} `{id}` {version} is pinned in "
                    + $"{authority} and has no row. Add one — and verify its licence rather than "
                    + "assuming it.");
            } else if (!string.Equals(documentedVersion, version, StringComparison.OrdinalIgnoreCase)) {
                problems.Add($"docs/manual/third-party.md: {noun} `{id}` is documented at "
                    + $"{documentedVersion} and pinned at {version} in {authority}. A version bump can "
                    + "change the licence; re-check it before changing the row.");
            }
        }

        foreach (var id in documented.Keys
            .Where(id => !pinned.ContainsKey(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)) {
            problems.Add($"docs/manual/third-party.md: {noun} `{id}` has a row and is not pinned in "
                + $"{authority}. If it was removed, remove the row.");
        }
    }
}
