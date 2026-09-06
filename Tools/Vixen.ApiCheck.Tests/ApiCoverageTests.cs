// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Enumeration;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>⚠ Verifying the instrument: what <c>CheckApi</c> does not look at, written down.</summary>
/// <remarks>
///     <para>
///         Coverage is a glob in <c>build/Build.Api.cs</c>, and a glob says nothing about what it
///         does not match. An assembly outside it still packs, so every addition, every signature
///         change and every silent removal in it passes with nothing to approve it — while the
///         target prints <c>Checking the public surface of N assemblies</c> and succeeds. Asking
///         what a gate prints on the day it does not run is the whole of this file: the answer for
///         an assembly <c>CheckApi</c> has never heard of is <em>success</em>, and no amount of
///         reading the target's output reveals it.
///     </para>
///     <para>
///         So the skipped set is committed, in <c>build/ApiUncovered.txt</c>, and these two tests
///         hold it to the tree in both directions. A project that starts packing and is checked by
///         nobody fails here rather than shipping quietly; a line for a project that has since been
///         covered, stopped packing or been deleted fails too, because a list that is allowed to
///         rot is one more instrument reporting success.
///     </para>
///     <para>
///         ⚠ <b>"Covered" has two halves and they are not the same question.</b> <c>CheckApi</c>'s
///         subject is the glob in <c>ApiCheckedProjects()</c>; the <c>PublicAPI.Shipped.txt</c>
///         beside a project is only what it compares against once the glob has put the assembly on
///         the list. Reading coverage from the file alone — which this did — reports an assembly
///         the target has never heard of as covered, and that is not a hypothetical ordering:
///         <c>Vixen.ApiCheck --update</c> writes the baseline and nothing writes the glob, so
///         whoever acts on #641 or #749 gets the file first.
///     </para>
///     <para>
///         ⚠ The subject is <c>Vixen.slnx</c> rather than a glob over the directory tree, and that
///         is not a detail. A walk from the repository root descends into
///         <c>.claude/worktrees</c> — a whole checkout per agent — and would compare one agent's
///         copy of this repository with another's. Reading the solution also gets the
///         <c>net10.0-ios</c>, <c>-android</c> and <c>-browser</c> projects right for free: they
///         are outside it, nothing has built them, and <c>CheckApi</c> would have nothing to read.
///     </para>
/// </remarks>
public sealed class ApiCoverageTests {
    /// <summary>The tokens <c>build/ApiUncovered.txt</c> accepts, so that "no reason" is not one.</summary>
    static readonly string[] Reasons = ["editor-undecided", "library-undecided", "tool-command-line", "no-assembly"];

    [Fact]
    public void APackableProjectIsEitherCheckedOrWrittenDown() {
        var missing = PackableProjects()
            .Where(project => !IsChecked(project))
            .Where(project => !Ledger().ContainsKey(project))
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These projects pack, so their public surface is a promise to somebody — and CheckApi "
            + "does not read it, which means an addition, a signature change or a removal in them "
            + "passes with nothing to approve it and the gate still prints success. Either cover "
            + "them in build/Build.Api.cs, or set IsPackable=false, or write the reason in "
            + "build/ApiUncovered.txt.\n  "
            + string.Join("\n  ", missing)
        );
    }

    /// <summary>
    ///     The direction that keeps the list from becoming decoration: a line whose project is now
    ///     covered, no longer packs, or no longer exists.
    /// </summary>
    [Fact]
    public void AWrittenDownProjectStillPacksAndIsStillUnchecked() {
        var packable = PackableProjects().ToHashSet(StringComparer.Ordinal);
        var stale = new List<string>();

        foreach (var (project, reason) in Ledger().OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            if (!File.Exists(Path.Combine(RepositoryRoot(), project))) {
                stale.Add($"{project}: no such project — it was renamed or removed.");
            } else if (!packable.Contains(project)) {
                stale.Add($"{project}: does not pack, or is not in Vixen.slnx — nothing was skipped, so delete the line.");
            } else if (IsChecked(project)) {
                stale.Add(
                    $"{project}: has a PublicAPI.Shipped.txt *and* is matched by Build.Api.cs's glob, "
                    + "so CheckApi does read it now — delete the line."
                );
            } else if (!Reasons.Contains(reason, StringComparer.Ordinal)) {
                stale.Add($"{project}: `{reason}` is not one of the reasons the file's header defines.");
            }
        }

        Assert.True(
            stale.Count == 0,
            "build/ApiUncovered.txt disagrees with the tree. A list of what a gate skips is only "
            + "worth reading while every line on it is still true.\n  "
            + string.Join("\n  ", stale)
        );
    }

    /// <summary>
    ///     Every non-test, non-generator project in <c>Vixen.slnx</c> that produces a package.
    ///     Absence of <c>IsPackable</c> means <em>yes</em>, exactly as it does in
    ///     <c>Build.Api.cs</c>: everything in the RUNTIME profile packs by profile rather than by
    ///     declaration, and the <c>TOOLING</c> profile never sets the property at all.
    /// </summary>
    static IEnumerable<string> PackableProjects() =>
        SolutionProjects()
            .Where(project => !EndsWithAny(project, ".Tests", ".Generator", ".Generators", ".Analyzers"))
            .Where(
                project => !string.Equals(
                    PropertyOf(Path.Combine(RepositoryRoot(), project), "IsPackable"),
                    "false",
                    StringComparison.OrdinalIgnoreCase
                )
            );

    /// <summary>
    ///     ⚠ A baseline the glob does not reach is read by nobody, so the two definitions of
    ///     "covered" must both hold — and the disagreement is reported in its own test.
    /// </summary>
    [Fact]
    public void EveryBaselineSitsWhereCheckApiLooks() {
        var patterns = CheckApiGlobs();

        var unreachable = PackableProjects()
            .Where(HasBaseline)
            .Where(project => !patterns.Any(pattern => MatchesGlob(pattern, project)))
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unreachable.Count == 0,
            "These projects have a PublicAPI.Shipped.txt and CheckApi's glob in build/Build.Api.cs "
            + "does not match them, so nothing compares the baseline with anything and the gate "
            + "still prints success. `Vixen.ApiCheck --update` writes the baseline; extending "
            + "ApiCheckedProjects() is the other half of the same commit.\n  "
            + string.Join("\n  ", unreachable)
        );
    }

    /// <summary>
    ///     ⚠ The two projects that cannot be un-packed, which is the half of #641 and #749 nobody
    ///     had measured: a covered package's own published dependencies.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both issues offer "cover it, or stop packing it" as a free choice between two
    ///         options. For two of the undecided projects it is not free, because a covered package
    ///         already depends on them and a <c>ProjectReference</c> without
    ///         <c>PrivateAssets=all</c> becomes a <c>&lt;dependency&gt;</c> in the
    ///         <c>.nuspec</c>. <c>Vixen.Editor.Plugin</c> — the one Editor assembly
    ///         <c>ApiCheckedProjects()</c> names, and the one doc 11 asks a *stricter* compatibility
    ///         policy of than anywhere else in the editor — references
    ///         <c>Vixen.Editor.Ui</c>, and <c>Vixen.Live.Realm</c> references <c>Vixen.App</c>. So
    ///         the strictest promise in the tree is only as strict as an assembly whose surface is
    ///         approved by nothing: a removal in <c>Vixen.Editor.Ui</c> breaks a plugin author's
    ///         build, passes <c>CheckApi</c>, and is not even visible in the diff of the package
    ///         that promised compatibility.
    ///     </para>
    ///     <para>
    ///         ⚠ Which is why this is an assertion and not a paragraph: the exceptions are named
    ///         here, so a covered contract taking on a *third* unreviewed dependency fails rather
    ///         than joining a silence nobody re-reads. The other direction matters as much — when
    ///         either is covered or stops packing, this goes red with the line to delete.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APublishedDependencyOfACoveredPackageIsCoveredToo() {
        string[] known = [
            // #641. Un-packing this one is not available: Vixen.Editor.Plugin's package would
            // declare a dependency that does not exist. Covering it is the only answer that leaves
            // the plugin contract restorable.
            "Editor/Vixen.Editor.Ui/Vixen.Editor.Ui.csproj",

            // #749, and the same shape one folder over: Vixen.Live.Realm is covered, and
            // VixenApp.Run<TGame> reaches its consumers through that package as well as through the
            // six Samples that reference it by path.
            "Tools/Vixen.App/Vixen.App.csproj",
        ];

        var unreviewed = PublishedDependencyClosure()
            .Where(project => !IsChecked(project))
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unreviewed.SequenceEqual(known.OrderBy(project => project, StringComparer.Ordinal), StringComparer.Ordinal),
            "The packable projects a covered package depends on, and which CheckApi does not read, "
            + "are supposed to be exactly the two #641 and #749 are open about. A NEW name here is a "
            + "reviewed package that just took on an unreviewed dependency — cover it or give the "
            + "reference PrivateAssets=all. A name that has GONE has been covered or stopped "
            + "packing; delete it from `known` in the same commit, because an exception list nobody "
            + "prunes is the instrument reporting success.\n  expected: "
            + string.Join(", ", known.OrderBy(project => project, StringComparer.Ordinal))
            + "\n  found:    "
            + string.Join(", ", unreviewed)
        );
    }

    /// <summary>
    ///     ⚠ The other list of packages this repository promises, and the one that is a promise to
    ///     an outsider rather than to itself: what <c>dotnet new</c> puts in a scaffolded csproj.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #641 and #749 both offer "stop packing it" as the cheap way out, and it has exactly
    ///         one way to go wrong: un-pack something a template references and every project
    ///         scaffolded from that template fails to restore, against a package id that no longer
    ///         exists. The templates are the only place this repository tells somebody outside it
    ///         which packages to install, so their <c>PackageReference</c>s are the floor under any
    ///         answer to those issues — and across all six templates that floor is ten names, of
    ///         which exactly one, <c>Vixen.Editor.Plugin</c>, is under <c>Editor/</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing cheap asked this before.</b> <c>CheckTemplates</c> does ask it, properly
    ///         and end-to-end — but only against the feed a full <c>Pack</c> has just written, so
    ///         nobody runs it per branch and <c>docs/overview.md</c> records that the target has
    ///         never been executed at all. A csproj is XML and the solution is a list; the
    ///         name-level half of the question needs neither a build nor a feed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryPackageATemplateReferencesStillPacks() {
        var packable = PackableProjects().ToHashSet(StringComparer.Ordinal);
        var referenced = TemplatePackageReferences();

        Assert.True(
            referenced.Count > 5,
            $"The templates yielded {referenced.Count} Vixen package references, which is too few "
            + "to be the six templates. The reader has stopped matching and the assertion below "
            + "would pass over nothing."
        );

        var missing = referenced
            .Where(name => !packable.Any(project =>
                string.Equals(Path.GetFileNameWithoutExtension(project), name, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "A template scaffolds a csproj referencing these package ids, and no project in "
            + "Vixen.slnx both bears the name and packs — so `dotnet new` produces a project that "
            + "cannot restore. If this went red while answering #641 or #749, the IsPackable=false "
            + "went one project too far: this is the set that has to keep packing whatever else "
            + "does not.\n  "
            + string.Join("\n  ", missing)
        );
    }

    /// <summary>The <c>Vixen.*</c> package ids the shipped templates reference, deduplicated.</summary>
    static List<string> TemplatePackageReferences() =>
        Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot(), "Tools", "Vixen.Templates", "templates"),
                "*.csproj",
                SearchOption.AllDirectories)
            .SelectMany(template => XDocument.Load(template).Descendants("PackageReference"))
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Where(name => name.StartsWith("Vixen.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Every packable project reachable from a covered one through references that survive into
    ///     the package — <c>ReferenceOutputAssembly=false</c> (an analyzer) and
    ///     <c>PrivateAssets=all</c> do not, and are the two ways to depend on something without
    ///     promising it.
    /// </summary>
    static IEnumerable<string> PublishedDependencyClosure() {
        var packable = PackableProjects().ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<string>(packable.Where(IsChecked));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reached = new HashSet<string>(StringComparer.Ordinal);

        while (pending.Count > 0) {
            var project = pending.Pop();

            if (!seen.Add(project)) {
                continue;
            }

            foreach (var reference in PublishedReferences(project).Where(packable.Contains)) {
                reached.Add(reference);
                pending.Push(reference);
            }
        }

        return reached;
    }

    static IEnumerable<string> PublishedReferences(string project) {
        var document = XDocument.Load(Path.Combine(RepositoryRoot(), project));

        foreach (var element in document.Descendants("ProjectReference")) {
            var include = (string?)element.Attribute("Include");

            if (string.IsNullOrEmpty(include)
                || Says(element, "ReferenceOutputAssembly", "false")
                || Says(element, "PrivateAssets", "all")) {
                continue;
            }

            yield return Relative(project, include);
        }
    }

    static bool Says(XElement element, string attribute, string value) =>
        string.Equals((string?)element.Attribute(attribute), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     A reference's <c>Include</c> is relative to the referencing project and spelled with
    ///     backslashes; the rest of this file speaks repository-relative forward slashes.
    /// </summary>
    static string Relative(string project, string include) {
        var directory = Path.GetDirectoryName(Path.Combine(RepositoryRoot(), project))!;
        var resolved = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/')));

        return Path.GetRelativePath(RepositoryRoot(), resolved).Replace('\\', '/');
    }

    /// <summary>
    ///     ⚠ Both halves, because they are different questions and the tree can answer them
    ///     differently. <c>CheckApi</c>'s subject is the glob in <c>ApiCheckedProjects()</c>; the
    ///     file beside the project is only what it compares against once it has decided to look. A
    ///     baseline written without extending the glob — which is the order the tooling imposes,
    ///     since <c>--update</c> writes the file and nothing writes the glob — used to read as
    ///     covered here and made <see cref="AWrittenDownProjectStillPacksAndIsStillUnchecked" />
    ///     demand the deletion of the only line still recording the truth.
    /// </summary>
    /// <remarks>
    ///     The other direction needs no test: a project the glob matches with no baseline reads as
    ///     an empty baseline (<c>ApiBaseline.Read</c>), so every public type in it is an unapproved
    ///     addition and <c>CheckApi</c> itself fails.
    /// </remarks>
    static bool IsChecked(string project) =>
        HasBaseline(project) && CheckApiGlobs().Any(pattern => MatchesGlob(pattern, project));

    static bool HasBaseline(string project) =>
        File.Exists(Path.Combine(RepositoryRoot(), Path.GetDirectoryName(project)!, "PublicAPI.Shipped.txt"));

    /// <summary>
    ///     The glob patterns <c>CheckApi</c> actually passes, read out of <c>build/Build.Api.cs</c>
    ///     rather than copied into this file.
    /// </summary>
    /// <remarks>
    ///     A copy is the thing most likely to drift, and the alternative the issue that prompted
    ///     this weighed — having the build emit its subject list as a committed artefact — costs a
    ///     gate run to regenerate. Reading the source is the cheap middle: it cannot silently
    ///     disagree with the target, and it fails loudly rather than matching nothing if the call
    ///     is ever rewritten into a shape this does not recognise.
    /// </remarks>
    static IReadOnlyList<string> CheckApiGlobs() {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "build", "Build.Api.cs"));
        var call = Regex.Match(source, @"\.GlobFiles\(\s*(?<arguments>[^)]*)\)", RegexOptions.Singleline);

        Assert.True(
            call.Success,
            "build/Build.Api.cs has no .GlobFiles( … ) call, so ApiCheckedProjects() no longer says "
            + "what CheckApi's subject is in a shape this test can read. Teach it the new shape "
            + "rather than deleting it — a coverage test that matches nothing passes."
        );

        var patterns = Regex.Matches(call.Groups["arguments"].Value, "\"(?<pattern>[^\"]+)\"")
            .Select(match => match.Groups["pattern"].Value)
            .ToList();

        Assert.NotEmpty(patterns);

        return patterns;
    }

    /// <summary>
    ///     Segment-wise glob matching for the shapes <c>ApiCheckedProjects()</c> uses:
    ///     <c>**</c> for any run of directories, <c>*</c> and <c>?</c> inside one segment.
    /// </summary>
    /// <remarks>
    ///     <c>**</c> matches zero segments here. Whether Nuke's own matcher agrees is not something
    ///     this tree can tell, because no project sits directly in <c>Core/</c> or any other globbed
    ///     root — so the two can only differ about a path that does not exist.
    /// </remarks>
    static bool MatchesGlob(string pattern, string path) =>
        MatchesFrom(pattern.Split('/'), 0, path.Split('/'), 0);

    static bool MatchesFrom(string[] pattern, int patternIndex, string[] path, int pathIndex) {
        while (true) {
            if (patternIndex == pattern.Length) {
                return pathIndex == path.Length;
            }

            if (pattern[patternIndex] == "**") {
                for (var skipped = pathIndex; skipped <= path.Length; skipped++) {
                    if (MatchesFrom(pattern, patternIndex + 1, path, skipped)) {
                        return true;
                    }
                }

                return false;
            }

            if (pathIndex == path.Length
                || !FileSystemName.MatchesSimpleExpression(pattern[patternIndex], path[pathIndex], ignoreCase: false)) {
                return false;
            }

            patternIndex++;
            pathIndex++;
        }
    }

    static Dictionary<string, string> Ledger() {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(Path.Combine(RepositoryRoot(), "build", "ApiUncovered.txt"))) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var columns = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            Assert.True(columns.Length == 2, $"build/ApiUncovered.txt: `{text}` is not a path and a reason.");

            entries[columns[0]] = columns[1];
        }

        return entries;
    }

    /// <summary>
    ///     The solution's project paths, as it spells them, normalised to forward slashes.
    /// </summary>
    static IEnumerable<string> SolutionProjects() =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "Vixen.slnx"))
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!.Replace('\\', '/'))
            .ToList();

    static bool EndsWithAny(string project, params string[] suffixes) {
        var name = Path.GetFileNameWithoutExtension(project);

        return suffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
    }

    static string? PropertyOf(string project, string name) =>
        XDocument.Load(project).Descendants(name).FirstOrDefault()?.Value.Trim();

    /// <summary>Walks up from the test assembly until the repository root is recognisable.</summary>
    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
