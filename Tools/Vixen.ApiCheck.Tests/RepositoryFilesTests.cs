// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using Vixen.Testing;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ That every repository sweep reads the tree git defines, and that none of them keeps a list
///     of its own (<a href="https://github.com/Rikarin/Vixen/issues/1424">#1424</a>).
/// </summary>
/// <remarks>
///     <para>
///         Eighteen walkers used to skip build output by a hand-kept list of directory names, and the
///         copies disagreed: three knew <c>.nuke</c>, one knew <c>references/</c>, none knew
///         <c>Samples/*/Build/</c>. A branch green in a fresh worktree went red on a master that had
///         run <c>./build.sh</c>, because <c>.nuke/temp/</c> held an unpacked copy of the Sdk README
///         citing a line the real one had re-pointed (9200ae53a). <c>Testing/RepositoryFiles.cs</c>
///         replaces every list with <c>git ls-files</c>.
///     </para>
///     <para>
///         ⚠ <b>A scratch repository carrying this checkout's own <c>.gitignore</c>, not the
///         checkout.</b> The trees that caused the failure — clones, unpacked packages, a sample's
///         build output — are exactly what CI and a fresh worktree do not have, so "none of them is
///         swept" asked of the real tree is a predicate that cannot be false there.
///     </para>
/// </remarks>
public sealed class RepositoryFilesTests {
    /// <summary>
    ///     ⚠ The issue's own sabotage, as a test: files planted where a working machine accumulates
    ///     them are not listed, and a new file nobody has added yet is.
    /// </summary>
    [Fact]
    public void A_checkout_lists_what_git_tracks_and_nothing_gitignore_excludes() {
        using var scratch = new ScratchRepository();

        scratch.Write("Core/Vixen.Demo/Demo.cs");
        scratch.Write("Core/Vixen.Demo/Tracked.cs");
        scratch.Git("add", "Core/Vixen.Demo/Tracked.cs");
        scratch.Write("Core/Vixen.Demo/Deleted.cs");
        scratch.Git("add", "Core/Vixen.Demo/Deleted.cs");
        File.Delete(scratch.Path("Core/Vixen.Demo/Deleted.cs"));
        scratch.Write("Core/Vixen.Demo/Shouting.CS");

        // What a working machine accumulates and CI never has.
        scratch.Write("references/README.md");
        scratch.Write("references/fake/Engine.cs");
        scratch.Write(".nuke/temp/vixen-sdk-tools/README.md");
        scratch.Write("Samples/03-Demo/Build/Generated.cs");
        scratch.Write("Samples/03-Demo/Library/Cache.cs");
        scratch.Write("Core/Vixen.Demo/obj/Debug/Generated.cs");
        scratch.Write("Core/Vixen.Demo/bin/Debug/Copied.cs");
        scratch.Write("artifacts/docs/Page.cs");
        scratch.Write("www/node_modules/package/index.cs");

        // ⚠ An agent's worktree is NOT ignored: it is a nested repository, reported by git as one
        // entry ending in a slash and never descended.
        scratch.Write(".claude/worktrees/wf_other/Core/Vixen.Demo/Demo.cs");
        scratch.GitIn(".claude/worktrees/wf_other", "init", "--quiet");

        Assert.Equal(
            [".gitignore", "Core/Vixen.Demo/Demo.cs", "Core/Vixen.Demo/Shouting.CS", "Core/Vixen.Demo/Tracked.cs", "references/README.md"],
            RepositoryFiles.Listed(scratch.Root).Order(StringComparer.Ordinal)
        );

        // A name pattern is matched case-sensitively, so the answer is one answer on every platform.
        Assert.Equal(
            [scratch.Path("Core/Vixen.Demo/Demo.cs"), scratch.Path("Core/Vixen.Demo/Tracked.cs")],
            RepositoryFiles.Files(scratch.Root, "*.cs").Order(StringComparer.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ What the helper prints on the day it cannot answer: an exception, never an empty tree.
    /// </summary>
    /// <remarks>
    ///     Every sweep built on this is a set comparison or a "found nothing wrong", and both report
    ///     success over an empty list. So a directory git does not know and a checkout with no file
    ///     in it both throw rather than return <c>[]</c>.
    /// </remarks>
    [Fact]
    public void A_directory_git_cannot_answer_for_throws_rather_than_reading_as_clean() {
        var outside = Directory.CreateTempSubdirectory("vixen-not-a-checkout-").FullName;

        try {
            var refused = Assert.Throws<InvalidOperationException>(() => RepositoryFiles.Listed(outside));
            Assert.Contains("git ls-files", refused.Message, StringComparison.Ordinal);
        } finally {
            Directory.Delete(outside, recursive: true);
        }

        using var empty = new ScratchRepository(withIgnoreFile: false);

        Assert.Contains(
            "listed no file",
            Assert.Throws<InvalidOperationException>(() => RepositoryFiles.Listed(empty.Root)).Message,
            StringComparison.Ordinal
        );
    }

    /// <summary>The real checkout: large, and relative to itself.</summary>
    [Fact]
    public void The_checkout_this_was_built_in_is_listed_relative_to_itself() {
        var listed = RepositoryFiles.Listed(RepositoryFiles.Root);

        Assert.True(listed.Count > 3000, $"{listed.Count} files is not this repository.");
        Assert.Contains("Testing/RepositoryFiles.cs", listed);
        Assert.DoesNotContain(listed, path => path.StartsWith(".claude/worktrees/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ No source in the tree keeps its own list of directories a sweep skips, outside the four
    ///     named here — exactly, in both directions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The issue asked that a hand-kept list which stays anywhere be held against
    ///         <c>.gitignore</c>; none stays in a repository sweep, so this holds the other half: a
    ///         line naming two or more of the directories those lists were made of is a list, and so
    ///         is a statement that names two and compares against them. A new one reds here before
    ///         it drifts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A statement as well as a line.</b> This read one line at a time and said so, and
    ///         the one walker #1424 left behind was exactly the shape that limit could not see:
    ///         <c>BaselineAgreement.IsSource</c> compared a path segment against <c>"bin"</c>,
    ///         <c>"obj"</c> and <c>"artifacts"</c> on three lines of one <c>||</c> chain, so the guard
    ///         read three lines naming one directory each and passed it. The comparison is what
    ///         separates a list from a neighbourhood: a window of lines alone also caught
    ///         <c>ProjectPaths</c> naming <c>Library</c> and <c>Build</c> on consecutive lines, and a
    ///         <c>dotnet pack</c> given its own <c>obj</c> and <c>bin</c>, which skip nothing.
    ///     </para>
    ///     <para>
    ///         All four survivors filter something that is not this repository: a user's project, the
    ///         synthetic project directories a build rule's fixtures create in a temp folder, and the
    ///         directory a running application watches for stylesheets, which is wherever it was
    ///         launched and need not be a checkout.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_source_keeps_its_own_list_of_directories_to_skip() {
        string[] named = [
            // The editor compiling a USER project's scripts: its Library/, Build/, bin/ and obj/,
            // in a directory that is not this repository and need not be a git checkout at all.
            "Editor/Vixen.Editor.Scripts/ScriptCompiler.cs",

            // One project's directory, and DataContractGeneratorRuleTests hands it synthetic ones
            // written to a temp folder, which are not checkouts either.
            "build/DataContractGeneratorRule.cs",

            // Stylesheet hot reload at run time, in the editor and in a desktop app: keeps the
            // generated obj/<config>/…/<Assembly>.g.vcss from binding. A shipped process watching
            // its own directory, not a sweep of this repository.
            "Editor/Vixen.Editor.App/EditorApplication.cs",
            "Platform/Vixen.Ui.Desktop.HotReload/DesktopHotReload.cs"
        ];

        var found = RepositoryFiles.Files(RepositoryFiles.Root, "*.cs", "*.vxml")
            .Where(path => KeepsASkipList(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepositoryFiles.Root, path).Replace('\\', '/'))
            .Where(path => path != "Tools/Vixen.ApiCheck.Tests/RepositoryFilesTests.cs")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            string.Empty,
            string.Join('\n', found.Except(named).Select(path =>
                $"{path} names two or more build-output directories on one line or in one comparison — a sweep deciding for itself "
                + "what the repository is. Read it through Testing/RepositoryFiles.cs (#1424)."))
        );

        Assert.Equal(
            string.Empty,
            string.Join('\n', named.Except(found).Select(path => $"{path} is named here and no longer keeps a list — delete its line."))
        );
    }

    /// <summary>A quoted directory name one of the old skip lists carried.</summary>
    static readonly Regex SkipList = new(
        "\"(?:bin|obj|\\.git|\\.claude|artifacts|node_modules|\\.nuke|references|packages|\\.vs|\\.idea|TestResults|Library|Build)\"",
        RegexOptions.CultureInvariant
    );

    /// <summary>A test of a name against a path segment, the half of a skip list that is not the names.</summary>
    static readonly Regex Comparison = new(
        @"Equals\(|==|!=|\bis\b|\bor\b|Contains\(|StartsWith\(",
        RegexOptions.CultureInvariant
    );

    /// <summary>A <c>//</c> comment to the end of its line, whose prose would read as a comparison.</summary>
    static readonly Regex LineComment = new("//[^\n]*", RegexOptions.CultureInvariant);

    /// <summary>
    ///     Whether <paramref name="source" /> names two distinct skipped directories on one line, or
    ///     in one statement that also compares against them.
    /// </summary>
    static bool KeepsASkipList(string source) {
        static int Distinct(string text) =>
            SkipList.Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).Count();

        return source.Split('\n').Any(line => Distinct(line) >= 2)
            || LineComment.Replace(source, string.Empty)
                .Split(';')
                .Any(statement => Distinct(statement) >= 2 && Comparison.IsMatch(statement));
    }

    /// <summary>A throwaway git repository in a temp folder, with this checkout's <c>.gitignore</c>.</summary>
    sealed class ScratchRepository : IDisposable {
        public string Root { get; } = Directory.CreateTempSubdirectory("vixen-repository-files-").FullName;

        public ScratchRepository(bool withIgnoreFile = true) {
            if (withIgnoreFile) {
                File.Copy(System.IO.Path.Combine(RepositoryFiles.Root, ".gitignore"), Path(".gitignore"));
            }

            Git("init", "--quiet");
        }

        public string Path(string relative) =>
            System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relative) {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path(relative))!);
            File.WriteAllText(Path(relative), "// planted\n");
        }

        public void Git(params string[] arguments) => GitIn(".", arguments);

        public void GitIn(string directory, params string[] arguments) {
            var start = new ProcessStartInfo("git") {
                WorkingDirectory = Path(directory),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var argument in arguments) {
                start.ArgumentList.Add(argument);
            }

            using var git = Process.Start(start)!;
            var error = git.StandardError.ReadToEndAsync();
            git.StandardOutput.ReadToEnd();
            git.WaitForExit();

            Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} exited {git.ExitCode}: {error.Result}");
        }

        public void Dispose() {
            // git marks its object files read-only, which Directory.Delete refuses on Windows.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
