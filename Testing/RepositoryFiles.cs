// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace Vixen.Testing;

/// <summary>
///     The files of this repository, as git defines them: everything it tracks, plus everything new
///     that <c>.gitignore</c> does not exclude.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every sweep that reads "the repository" reads it through here, because the question
///         is git's and a directory walk answers a different one.</b> Eighteen walkers used to skip
///         build output by a hand-kept list of directory names, and the copies disagreed: eleven
///         skipped <c>.git</c>, <c>.claude</c>, <c>bin</c>, <c>obj</c>, <c>artifacts</c> and
///         <c>node_modules</c> and nothing else, three added <c>.nuke</c>, one stayed out of
///         <c>references/</c>. None excluded the other trees a working machine accumulates —
///         <c>references/*</c> (four cloned C# engines, per its README), <c>.nuke/temp/</c> (the
///         unpacked Sdk, README included), <c>Samples/*/Build/</c>. So whether a test failed
///         depended on which build products were on the disk, and a branch green in a fresh worktree
///         went red on a master that had run <c>./build.sh</c>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1424">#1424</a>).
///     </para>
///     <para>
///         <b><c>--others --exclude-standard</c> as well as the index</b>, so a file created and not
///         yet added is swept — a new source file is exactly the one likeliest to carry the defect a
///         census exists for. A tracked file deleted from the working tree is dropped, because there
///         is nothing left to read.
///     </para>
///     <para>
///         ⚠ <b>An agent's worktree under <c>.claude/worktrees/</c> is not ignored</b>, and does not
///         need to be: it holds a <c>.git</c> file, so git reports it as one nested repository — a
///         single entry ending in <c>/</c> — and never lists what is inside. Entries ending in
///         <c>/</c> are dropped here. Inside a worktree, the listing is that worktree's.
///     </para>
///     <para>
///         Paths come back in git's order, which is ordinal on the <c>/</c>-separated relative path,
///         so two platforms see the same list in the same order. A file-name pattern is matched
///         case-sensitively for the same reason; a directory walk on Windows matched <c>*.cs</c>
///         against <c>Foo.CS</c> and on Linux did not.
///     </para>
///     <para>
///         Linked rather than referenced, like everything else in <c>Testing/</c>, and it names only
///         the BCL, so <c>Vixen.DocGen</c> and <c>build/_build.csproj</c> link it too.
///     </para>
/// </remarks>
static class RepositoryFiles {
    static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Listings = new(StringComparer.Ordinal);

    static readonly Lazy<string> RootDirectory = new(FindRoot);

    /// <summary>The repository root: the nearest directory above the running binary holding <c>Vixen.slnx</c>.</summary>
    /// <remarks>
    ///     ⚠ Walked from <see cref="AppContext.BaseDirectory" /> rather than anchored on a
    ///     <c>[CallerFilePath]</c>: CI sets <c>ContinuousIntegrationBuild</c>, which rewrites every
    ///     compiled source path to <c>/_/…</c>.
    /// </remarks>
    public static string Root => RootDirectory.Value;

    /// <summary>
    ///     Every file git would call part of the tree under <paramref name="directory" />, relative to
    ///     it and <c>/</c>-separated.
    /// </summary>
    /// <param name="directory">An absolute directory inside a checkout.</param>
    /// <returns>The relative paths, in git's (ordinal) order, of files that exist.</returns>
    /// <exception cref="InvalidOperationException">
    ///     When git fails, or lists nothing — which a sweep would otherwise report as a clean tree.
    /// </exception>
    public static IReadOnlyList<string> Listed(string directory) =>
        Listings.GetOrAdd(Path.GetFullPath(directory), List);

    /// <summary>
    ///     The files under <paramref name="directory" /> whose names match any of
    ///     <paramref name="patterns" />, as absolute paths.
    /// </summary>
    /// <param name="directory">An absolute directory inside a checkout.</param>
    /// <param name="patterns">
    ///     File-name patterns with <c>*</c> and <c>?</c>, such as <c>*.cs</c>, matched against the
    ///     name alone and case-sensitively. None means every file.
    /// </param>
    /// <returns>Absolute paths with the platform's separator, in git's order.</returns>
    public static List<string> Files(string directory, params string[] patterns) {
        var root = Path.GetFullPath(directory);

        return [
            .. Listed(root)
                .Where(relative => patterns.Length == 0 || patterns.Any(pattern => Matches(pattern, relative)))
                .Select(relative => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
        ];
    }

    /// <summary>The files under <see cref="Root" /> whose names match any of <paramref name="patterns" />.</summary>
    /// <param name="patterns">File-name patterns, as for <see cref="Files(string, string[])" />.</param>
    /// <returns>Absolute paths with the platform's separator, in git's order.</returns>
    public static List<string> Files(params string[] patterns) => Files(Root, patterns);

    static bool Matches(string pattern, string relative) {
        var slash = relative.LastIndexOf('/');
        var name = slash < 0 ? relative : relative[(slash + 1)..];

        return FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false);
    }

    static IReadOnlyList<string> List(string directory) {
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in (string[])["ls-files", "-z", "--cached", "--others", "--exclude-standard"]) {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start)
            ?? throw new InvalidOperationException($"`git ls-files` could not be started in '{directory}'.");

        // Both streams read before the wait, stderr on its own task: a full stderr pipe with
        // stdout still being drained is a deadlock, not a slow test.
        var error = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();

        git.WaitForExit();

        if (git.ExitCode != 0) {
            throw new InvalidOperationException(
                $"`git ls-files` exited {git.ExitCode} in '{directory}': {error.GetAwaiter().GetResult().Trim()}"
            );
        }

        var files = output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !entry.EndsWith('/'))
            .Distinct(StringComparer.Ordinal)
            .Where(entry => File.Exists(Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        return files.Count > 0
            ? files
            : throw new InvalidOperationException(
                $"`git ls-files` listed no file under '{directory}', which a sweep would report as a clean tree."
            );
    }

    static string FindRoot() {
        for (var directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory)) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }
        }

        throw new InvalidOperationException($"No Vixen.slnx above '{AppContext.BaseDirectory}', so no repository root.");
    }
}
