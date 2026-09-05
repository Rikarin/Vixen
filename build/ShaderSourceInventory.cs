// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
///     Every Raven source outside the library whose compiled modules are committed beside it.
/// </summary>
/// <remarks>
///     <para>
///         <b>This walk <em>is</em> the list.</b> <c>CheckShaders</c>' editor half used to be four
///         hand-written tuples with nothing asserting they were all of them — the same shape a third
///         time, after <c>MaterialCompiler.OptionalSlots</c> and #371. It was then given a
///         completeness assertion, which turned a missing entry into a red gate rather than a silent
///         hole; this walk goes one step further and hands <c>Build.DiscoverEditorSources</c> the
///         entries themselves, so a source added under a project is compiled and diffed by the next
///         run and there is nothing left to remember.
///     </para>
///     <para>
///         ⚠ <b>Committed modules, not a source, are what make a source owed.</b> A glob for
///         <c>**/Shaders/*.rvn</c> would pull in
///         <c>Platform/Vixen.Graphics.Golden.Tests/Shaders</c>, whose four probe sources are compiled
///         by the test that uses them and commit nothing — and two of those four <c>import</c> the
///         core package, so compiling them one file at a time would not even succeed. So the question
///         asked of each source is whether a <c>{shader}.{stage}.spv</c> for one of the shaders it
///         declares is sitting next to it: that, and only that, is a binary a person can leave stale.
///     </para>
///     <para>
///         ⚠ <b>A <c>.spv</c>, not any generated artefact, and the difference has one name.</b>
///         <c>Core/Vixen.Shaders.Tests/Fixtures/Lighting.rvn</c> has a committed
///         <c>Lighting.reflect.json</c> beside it and no module, and that file is checked in
///         <em>deliberately</em> — its own <c>README.md</c> says the generator's contract is with the
///         schema rather than with the compiler, so recompiling it here would make a shader-language
///         change look like a generator failure. Widening this property to reflection files would
///         quietly conscript that fixture into a gate its README explains why it is outside.
///     </para>
///     <para>
///         ⚠ <b><c>Raven/</c> is excluded because the library is not committed as modules</b> — over a
///         hundred shaders across a hundred and twelve files, bound as one compilation by
///         <c>LibraryReflectionTests</c>. The entries in <c>EditorShaders</c> are the five of them
///         whose bytes the editor carries, and those have no <c>.rvn</c> beside the <c>.spv</c>, so
///         they never reach this walk from either side.
///     </para>
///     <para>
///         ⚠ <b><c>.claude/worktrees</c> holds a whole checkout per agent.</b> A walk from the
///         repository root that does not skip it compares one agent's copy of a file with another's,
///         and reports work against a tree nobody is editing.
///     </para>
/// </remarks>
static class ShaderSourceInventory {
    /// <summary>Directory names a walk from the repository root must not descend into.</summary>
    static readonly string[] Skipped = [".git", ".claude", ".vs", ".idea", "bin", "obj", "artifacts", "Raven"];

    /// <summary>
    ///     Walks <paramref name="root" /> for the sources this gate has to know about.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>
    ///     Repository-relative paths with <c>/</c> separators, sorted, of every <c>.rvn</c> that has a
    ///     committed module for at least one shader it declares.
    /// </returns>
    public static List<string> WithCommittedModules(string root) {
        var found = new List<string>();

        foreach (var file in Sources(root)) {
            var beside = Path.GetDirectoryName(file)!;

            // ⚠ Matched here rather than by a search pattern: `EnumerateFiles(dir, "Mesh.*.spv")`
            // goes through the 8.3 short-name rules, where a `.` and a `*` together match more than
            // they read as — and this has to be able to tell `Mesh` from `MeshInstanced`.
            var modules = Directory
                .EnumerateFiles(beside, "*.spv")
                .Select(Path.GetFileName)
                .ToList();

            var committed = Declares(file).Any(shader => modules.Exists(module =>
                module!.StartsWith($"{shader}.", StringComparison.Ordinal)
            ));

            if (committed) {
                found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        found.Sort(StringComparer.Ordinal);

        return found;
    }

    /// <summary>
    ///     Every <c>.rvn</c> under <paramref name="root" />, skipping what a repository walk must not
    ///     read.
    /// </summary>
    /// <remarks>
    ///     Hand-rolled rather than <c>EnumerateFiles(..., AllDirectories)</c> because that one cannot
    ///     be told to skip a directory — it walks every agent worktree and every <c>obj</c> first and
    ///     hands the caller the results afterwards.
    /// </remarks>
    static IEnumerable<string> Sources(string root) {
        var pending = new Stack<string>();

        pending.Push(root);

        while (pending.Count > 0) {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current)) {
                if (!Skipped.Contains(Path.GetFileName(directory), StringComparer.Ordinal)) {
                    pending.Push(directory);
                }
            }

            foreach (var file in Directory.EnumerateFiles(current, "*.rvn")) {
                yield return file;
            }
        }
    }

    /// <summary>
    ///     Whether a source reaches into another package, which is what decides it cannot be compiled
    ///     on its own.
    /// </summary>
    /// <param name="file">The source to read.</param>
    /// <returns><c>true</c> if it carries at least one <c>import</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked of the file rather than kept as an exclusion list.</b> The sources this walk
    ///     hands back are compiled one file at a time, with no <c>--source</c> closure, because none
    ///     of them imports anything. That is a property of the file and it can stop being true — a
    ///     kernel that starts importing <c>Vixen.Shaders.Core</c> is a real thing to write — so the
    ///     gate reads it out of the source and refuses loudly, rather than carrying a list of names to
    ///     skip that nobody updates.
    /// </remarks>
    public static bool Imports(string file) {
        foreach (var line in File.ReadLines(file)) {
            if (line.Trim().StartsWith("import ", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The shaders a source declares, which is what its committed modules are named after.
    /// </summary>
    /// <remarks>
    ///     ⚠ The name is the module's, not the file's: <c>Line.rvn</c> declares <c>LineVertex</c> and
    ///     <c>LineFragment</c>, and <c>Ui.rvn</c> declares eight. A check keyed on the file name would
    ///     have found neither.
    /// </remarks>
    static IEnumerable<string> Declares(string file) {
        foreach (var line in File.ReadLines(file)) {
            var text = line.Trim();

            if (!text.StartsWith("shader ", StringComparison.Ordinal)) {
                continue;
            }

            var name = text[7..].Trim();
            var end = name.IndexOfAny([' ', '\t', '{', ':', '(']);

            if (end >= 0) {
                name = name[..end];
            }

            if (name.Length > 0) {
                yield return name;
            }
        }
    }
}
