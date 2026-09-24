// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vixen.Build;

/// <summary>
///     Which projects own a changed file, as a function of repository-relative paths and project
///     text: the part of <c>--since</c> that decides what a narrowed run may skip.
/// </summary>
/// <remarks>
///     <para>
///         Dependency-free, and linked into <c>Vixen.ApiCheck.Tests</c> for the reason every rule
///         beside it is: <c>build/_build.csproj</c> is outside the solution, so a rule that answers
///         only inside a Nuke run is a rule nobody has watched answer. <c>AffectedOwnershipTests</c>
///         is the other caller.
///     </para>
///     <para>
///         ⚠ <b>Directory containment is not the only way a project reads a file.</b> The walk up to
///         the nearest <c>.csproj</c> is right for a file inside a project, and says nothing at all
///         about the 115 shaders under <c>Raven/Library/</c>, which no project contains and nine
///         projects read — through a <c>&lt;None Include="..\..\Raven\Library\**\*.rvn" /&gt;</c>, an
///         <c>AdditionalFiles</c> reflection the binding generator compiles, or (for
///         <c>Vixen.Raven.Tests</c>) a path walked off disk at run time. Without this, a comment-only
///         edit to <c>ForwardPlus.rvn</c> made <c>AffectedProjects --since</c> refuse outright
///         (<a href="https://github.com/Rikarin/Vixen/issues/1420">#1420</a>).
///     </para>
///     <para>
///         ⚠ <b>The fix is deliberately not a line in <see cref="OwnedByNoProject" />.</b> That list
///         is for files nothing compiles or tests; exempting the shader library there would narrow a
///         shader edit to <em>no</em> tests, silently, which is the hole the orphan assertion exists
///         to close. A reader is an owner.
///     </para>
/// </remarks>
public static class AffectedOwnership {
    /// <summary>
    ///     A project that reads files under a directory without naming them in any item, so that
    ///     nothing in its project file could be matched against a change.
    /// </summary>
    /// <param name="Prefix">The repository-relative directory, <c>/</c>-separated, with a trailing slash.</param>
    /// <param name="Project">The reading project, repository-relative.</param>
    /// <param name="Evidence">
    ///     A repository-relative source file of that project which spells the directory, so that a
    ///     reader which stopped reading is noticed rather than trusted.
    /// </param>
    public sealed record DeclaredReader(string Prefix, string Project, string Evidence);

    /// <summary>
    ///     The readers no project file can reveal, because they find their input on disk.
    /// </summary>
    /// <remarks>
    ///     ⚠ Short on purpose, and the only hand-kept part of the rule: everything a project names
    ///     in an item is derived by <see cref="ItemPatterns" /> instead. <c>Vixen.Raven.Tests</c> is
    ///     here because <c>LibraryReflectionTests</c>, <c>LibraryCompileWorkTests</c> and
    ///     <c>LexerDifferentialTests</c> bind the whole library by walking four directories up from
    ///     the test binary, which no <c>.csproj</c> records. It is a floor and not a closure: an
    ///     <c>.rvn</c> import closure is still invisible to <c>--since</c>, as its targets say.
    /// </remarks>
    public static IReadOnlyList<DeclaredReader> DeclaredReaders { get; } = [
        new("Raven/Library/", "Raven/Vixen.Raven.Tests/Vixen.Raven.Tests.csproj", "Raven/Vixen.Raven.Tests/LibraryReflectionTests.cs")
    ];

    /// <summary>
    ///     Item kinds whose <c>Include</c> is not a file this project reads.
    /// </summary>
    /// <remarks>
    ///     ⚠ A <c>ProjectReference</c> is by far the commonest out-of-directory include in the tree
    ///     (about 1 600 of them) and it already has its own edge in the reverse reference graph; read
    ///     as a file include it would make every project an owner of every <c>.csproj</c> it
    ///     references, which is the closure computed a second time and wrongly.
    /// </remarks>
    static readonly HashSet<string> NotFileItems = new(StringComparer.Ordinal) {
        "ProjectReference", "PackageReference", "PackageVersion", "GlobalPackageReference", "Using",
        "InternalsVisibleTo", "AssemblyAttribute", "Folder", "FrameworkReference", "TrimmerRootAssembly"
    };

    /// <summary>
    ///     Whether a changed file is one no project can own, so that owning none of them is an
    ///     answer rather than a hole.
    /// </summary>
    /// <param name="relative">The repository-relative path, <c>/</c>-separated.</param>
    /// <returns><see langword="true" /> for a file no .NET target can check.</returns>
    /// <remarks>
    ///     The list is deliberately short and by directory. Everything else that reaches the walk
    ///     and finds no project is reported as an error: a source file outside every project is
    ///     either a project that was never added or a rule here that has gone stale, and both are
    ///     worth a message.
    /// </remarks>
    public static bool OwnedByNoProject(string relative) =>
        !relative.Contains('/', StringComparison.Ordinal)
        || relative.StartsWith("docs/", StringComparison.Ordinal)
        || relative.StartsWith(".github/", StringComparison.Ordinal)
        || relative.StartsWith(".nuke/", StringComparison.Ordinal)
        || relative.StartsWith(".config/", StringComparison.Ordinal)
        || relative.StartsWith("references/", StringComparison.Ordinal)
        || relative.StartsWith("artifacts/", StringComparison.Ordinal)
        // The site is TypeScript and its own build; no .csproj owns a line of it, so without
        // this any change under www/ makes `--since` refuse rather than narrow.
        || relative.StartsWith("www/", StringComparison.Ordinal)
        // ⚠ The VS Code extension is the same case one level down: TextMate grammars, language
        // configuration and snippets in JSON, tested by `node --test` against its own
        // node_modules. `Tools/` is otherwise all projects, so the walk found none here and a
        // batch that taught the grammar a new VXML keyword (`@rows`, #758) made `--since`
        // refuse outright. No .NET target can check a line of it, so owning none is the answer.
        || relative.StartsWith("Tools/Vixen.VSCode/", StringComparison.Ordinal)
        // ⚠ An area README documents a top-level directory rather than anything in it, and no
        // .csproj sits beside it to be walked up to — `Raven/README.md`, and `Core/README.md`
        // and `Platform/README.md` the same way. The repository-root `README.md` is already
        // covered by the no-slash clause at the top, so only the sibling case was missing, and
        // it made `--since` refuse outright: a batch that touched an area README could not use
        // the narrowing targets at all. CLAUDE.md calls these READMEs "the best entry point
        // into an unfamiliar area", so they are edited often and compile into nothing.
        //
        // Kept as narrow as the others: exactly one slash, and exactly that name. A README
        // deeper in the tree still belongs to the project above it, which is where the
        // per-module READMEs the convention actually cares about live.
        || (relative.EndsWith("/README.md", StringComparison.Ordinal)
            && relative.Count(character => character == '/') == 1);


    /// <summary>
    ///     The repository-relative patterns a project's items name <em>outside</em> its own
    ///     directory — the files it reads that directory containment cannot attribute to it.
    /// </summary>
    /// <param name="project">The project file, repository-relative and <c>/</c>-separated.</param>
    /// <param name="text">The project file's XML.</param>
    /// <param name="read">
    ///     Reads a repository-relative file, or returns <see langword="null" /> for one that does not
    ///     exist, so that an <c>&lt;Import&gt;</c>ed file's own items are followed; <see langword="null" />
    ///     to follow none.
    /// </param>
    /// <returns>Glob patterns, repository-relative, <c>/</c>-separated.</returns>
    /// <remarks>
    ///     <para>
    ///         Read from the XML rather than evaluated, for the reason
    ///         <c>ReverseReferenceGraph</c> gives: one file per project against minutes of MSBuild.
    ///         What that costs is stated rather than hidden — an include spelled through any property
    ///         but <c>$(MSBuildThisFileDirectory)</c> and <c>$(MSBuildProjectDirectory)</c> is not
    ///         resolved and contributes nothing, and <c>Exclude</c> is ignored, which can only make a
    ///         project own <em>more</em> than it reads. Over-inclusion runs a test too many;
    ///         under-inclusion is the silent skip this whole selector exists to refuse.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An import is followed</b>, because that is how <c>Testing/</c> is shared: a test
    ///         project imports <c>Testing/Vixen.Testing.GoldenFile.props</c>, and the props file
    ///         compiles <c>$(MSBuildThisFileDirectory)GoldenFile.cs</c> into it. Reading only the
    ///         project file owned the props and not the source it links, so an edit to any of the
    ///         five <c>Testing/*.cs</c> helpers made <c>--since</c> refuse.
    ///     </para>
    ///     <para>
    ///         Patterns inside the project's own directory are dropped, because the directory walk
    ///         already owns every file there, and so is a pattern that climbs above the repository.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> ItemPatterns(string project, string text, Func<string, string?>? read = null) {
        var directory = Directory(project);
        var patterns = new List<string>();
        var followed = new HashSet<string>(StringComparer.Ordinal) { project };

        Collect(project, text);

        return patterns;

        // `file` is the project or a file it imports: items resolve against the project's directory
        // wherever they are written, and `$(MSBuildThisFileDirectory)` against the file's own.
        void Collect(string file, string xml) {
            foreach (var element in XDocument.Parse(xml).Descendants()) {
                if (NotFileItems.Contains(element.Name.LocalName)) {
                    continue;
                }

                // An item's Include, and an <Import Project="…">, are the two ways a project file
                // names another file it reads.
                var isImport = element.Name.LocalName == "Import";
                var value = isImport
                    ? element.Attribute("Project")?.Value
                    : element.Parent?.Name.LocalName == "ItemGroup" ? element.Attribute("Include")?.Value : null;

                if (string.IsNullOrWhiteSpace(value)) {
                    continue;
                }

                foreach (var include in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                    var resolved = ResolveInclude(directory, Directory(file), include);

                    if (resolved is null) {
                        continue;
                    }

                    if (directory.Length == 0 || !resolved.StartsWith(directory + "/", StringComparison.Ordinal)) {
                        patterns.Add(resolved);
                    }

                    if (isImport && read is not null && !resolved.Contains('*', StringComparison.Ordinal)
                        && followed.Add(resolved) && read(resolved) is { } imported) {
                        Collect(resolved, imported);
                    }
                }
            }
        }
    }

    /// <summary>Whether a repository-relative path matches an MSBuild-style glob.</summary>
    /// <param name="pattern">The glob: <c>**</c> spans directories, <c>*</c> and <c>?</c> do not.</param>
    /// <param name="relative">The path, <c>/</c>-separated.</param>
    /// <returns><see langword="true" /> on a match.</returns>
    /// <remarks>
    ///     Case-insensitive, because MSBuild on the platforms this repository is edited on is, and
    ///     because the direction of a wrong answer here is running one test project too many.
    /// </remarks>
    public static bool Matches(string pattern, string relative) {
        // A literal pattern is by far the commonest (a linked file, one reflect.json) and needs no
        // regex; the static Regex cache holds fifteen, against a few hundred patterns here.
        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal)) {
            return string.Equals(pattern, relative, StringComparison.OrdinalIgnoreCase);
        }

        var regex = Globs.GetOrAdd(
            pattern,
            glob => new Regex(
                "^" + Regex.Escape(glob)
                    .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
                    .Replace(@"\*\*", ".*", StringComparison.Ordinal)
                    .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
                    .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        );

        return regex.IsMatch(relative);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Globs = new(StringComparer.Ordinal);

    /// <summary>
    ///     Every project that reads <paramref name="relative" /> without containing it: through an
    ///     item or an import that names it, through MSBuild's implicit <c>Directory.Build.*</c>
    ///     import, or as a <see cref="DeclaredReaders" /> entry.
    /// </summary>
    /// <param name="relative">The changed file, repository-relative and <c>/</c>-separated.</param>
    /// <param name="patternsByProject">Each project's <see cref="ItemPatterns" />, keyed by project.</param>
    /// <returns>The reading projects, ordered.</returns>
    /// <remarks>
    ///     ⚠ A <c>Directory.Build.props</c> or <c>.targets</c> is imported by every project beneath
    ///     it without a line in any of them. <c>Raven/Directory.Build.props</c> has no project beside
    ///     it to be walked up to, so an edit to it refused; it is read by every Raven project. The
    ///     repository-root pair stays with the projectless root files, as it always has, because
    ///     "every project" is the unnarrowed run.
    /// </remarks>
    public static IReadOnlyList<string> ReadersOf(string relative, IReadOnlyDictionary<string, IReadOnlyList<string>> patternsByProject) {
        var readers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var reader in DeclaredReaders.Where(reader => relative.StartsWith(reader.Prefix, StringComparison.Ordinal))) {
            readers.Add(reader.Project);
        }

        var name = relative[(relative.LastIndexOf('/') + 1)..];
        var beneath = Directory(relative) is { Length: > 0 } above
            && (name == "Directory.Build.props" || name == "Directory.Build.targets")
                ? above + "/"
                : null;

        foreach (var (project, patterns) in patternsByProject) {
            if ((beneath is not null && project.StartsWith(beneath, StringComparison.Ordinal))
                || patterns.Any(pattern => Matches(pattern, relative))) {
                readers.Add(project);
            }
        }

        return [.. readers];
    }


    /// <summary>The owners of a set of changed files, and the files nothing owns.</summary>
    /// <param name="Projects">Every owning project, ordered.</param>
    /// <param name="Orphans">Changed files with no owner that are not known to be projectless.</param>
    public sealed record Ownership(IReadOnlyList<string> Projects, IReadOnlyList<string> Orphans);

    /// <summary>
    ///     The whole rule: a changed file is owned by the project whose directory holds it, and by
    ///     every project that reads it from outside; one owned by neither is projectless or an orphan.
    /// </summary>
    /// <param name="changed">Changed files, repository-relative and <c>/</c>-separated.</param>
    /// <param name="containing">The nearest project at or above a file, or <see langword="null" />.</param>
    /// <param name="patternsByProject">Each project's <see cref="ItemPatterns" />.</param>
    /// <returns>The owners and the orphans.</returns>
    /// <remarks>
    ///     ⚠ Readers are added to a file that <em>has</em> a containing project too, and that is
    ///     not generosity: <c>build/PathCaseRule.cs</c> is contained by <c>build/_build.csproj</c>,
    ///     which is outside the solution, and compiled into <c>Vixen.ApiCheck.Tests</c> through a
    ///     linked <c>Compile</c>. Owned by its container alone, an edit to any of the build rules
    ///     that suite exists to fixture narrowed to no test at all.
    /// </remarks>
    public static Ownership Classify(
        IEnumerable<string> changed,
        Func<string, string?> containing,
        IReadOnlyDictionary<string, IReadOnlyList<string>> patternsByProject
    ) {
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        var orphans = new List<string>();

        foreach (var file in changed) {
            var container = containing(file);
            var readers = ReadersOf(file, patternsByProject);

            if (container is not null) {
                projects.Add(container);
            }

            projects.UnionWith(readers);

            if (container is null && readers.Count == 0 && !OwnedByNoProject(file)) {
                orphans.Add(file);
            }
        }

        return new([.. projects], orphans);
    }

    /// <summary>The directory part of a repository-relative path, or empty at the root.</summary>
    static string Directory(string relative) {
        var slash = relative.LastIndexOf('/');

        return slash < 0 ? string.Empty : relative[..slash];
    }

    /// <summary>
    ///     An <c>Include</c> or <c>Project</c> value written in <paramref name="fileDirectory" />
    ///     for a project in <paramref name="projectDirectory" />, repository-relative, or
    ///     <see langword="null" /> when it names a property this reader does not evaluate.
    /// </summary>
    static string? ResolveInclude(string projectDirectory, string fileDirectory, string include) {
        const string thisFile = "$(MSBuildThisFileDirectory)";
        const string thisProject = "$(MSBuildProjectDirectory)";

        var from = projectDirectory;

        if (include.StartsWith(thisFile, StringComparison.Ordinal)) {
            from = fileDirectory;
            include = include[thisFile.Length..];
        } else if (include.StartsWith(thisProject, StringComparison.Ordinal)) {
            include = include[thisProject.Length..];
        }

        return include.Contains("$(", StringComparison.Ordinal)
            || include.Contains("@(", StringComparison.Ordinal)
            || include.Contains("%(", StringComparison.Ordinal)
                ? null
                : Resolve(from, include.Replace('\\', '/'));
    }

    /// <summary>
    ///     <paramref name="include" /> resolved against <paramref name="directory" />, with
    ///     <c>.</c> and <c>..</c> folded, or <see langword="null" /> when it leaves the repository.
    /// </summary>
    static string? Resolve(string directory, string include) {
        var segments = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));

        foreach (var segment in include.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (segment == ".") {
                continue;
            }

            if (segment == "..") {
                if (segments.Count == 0) {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }
}
