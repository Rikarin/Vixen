// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling.Tests;

/// <summary>How a reach census reads the repository: which files it walks, and which names it pulls
/// out of a compiled selector.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Shared by the two censuses rather than copied into the second, because the answer to
///         "what is the repository" must not be able to differ between them.</b>
///         <see cref="TypeSelectorReachTests" /> asks whether a type selector names a tag anything
///         creates; <see cref="ClassSelectorReachTests" /> asks the same question of a class. They
///         disagree about nothing except which <see cref="SimpleSelectorKind" /> they collect, and
///         two copies of a directory walk are two chances to skip a different set of directories.
///     </para>
///     <para>
///         ⚠ <b><see cref="Names" /> walks <see cref="SimpleSelectorKind.Has" /> and the walk it was
///         lifted from did not.</b> A name inside <c>:has()</c> would have escaped the type census
///         entirely — silently, because a census that cannot see a selector reports it as absent
///         rather than as unmeasured. Nothing in any sheet writes one today, so this changes no
///         answer; it is the arm that would have been missing on the day one did.
///     </para>
/// </remarks>
static class RepositoryScan {
    /// <summary>Directories a source sweep must not descend into, matched by name at any depth.</summary>
    /// <remarks>
    ///     ⚠ <b>Pruned during the walk rather than filtered after it, and the difference is eleven
    ///     minutes.</b> The obvious spelling — <c>EnumerateFiles(root, pattern, AllDirectories)</c>
    ///     followed by a <c>Where</c> on the path — still visits every file it then discards, and
    ///     <c>.claude/worktrees/</c> held <b>56 full checkouts of this repository</b> on the machine
    ///     where that was measured. Three patterns over fifty-seven copies of the tree is not a
    ///     filter problem, it is a traversal problem, and a gate that costs eleven minutes is one
    ///     somebody eventually deletes.
    ///     <para>
    ///         ⚠ <c>.claude</c> is also the difference between a test about this repository and a
    ///         test about whatever else is on the disk: a worktree is a full checkout of arbitrary
    ///         other work, and this sweep failed a gate run by finding the very <c>World-title</c> it
    ///         exists to prevent in a tree where that fix had not landed yet — a true statement about
    ///         a tree nobody was asking about.
    ///     </para>
    /// </remarks>
    static readonly string[] Unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];

    /// <summary>Every file in the working tree matching a pattern, in a stable order.</summary>
    /// <param name="pattern">A search pattern, such as <c>*.vcss</c>.</param>
    /// <returns>Absolute paths, sorted ordinally.</returns>
    public static List<string> Files(string pattern) {
        List<string> found = [];
        Walk(Root(), pattern, found);
        found.Sort(StringComparer.Ordinal);

        return found;
    }

    static void Walk(string directory, string pattern, List<string> into) {
        into.AddRange(Directory.EnumerateFiles(directory, pattern));

        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (!Unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                Walk(child, pattern, into);
            }
        }
    }

    /// <summary>The working tree's root, found by a directory only it has.</summary>
    public static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>Every name of one kind a compiled selector holds, including the nested ones.</summary>
    /// <param name="engine">The engine the selector was compiled by.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="kind">Which simple selector to collect — <c>Type</c> or <c>Class</c>.</param>
    /// <returns>The names, with duplicates.</returns>
    /// <remarks>
    ///     ⚠ <b>Read out of the compiled table rather than off the sheet's text, and that is what a
    ///     census over regular expressions gets wrong.</b> A name inside <c>:is()</c>, <c>:not()</c>
    ///     or <c>:has()</c> is exactly as real as one at the top level and is the case a text search
    ///     is worst at — and <c>:where()</c> does not even survive to the table under its own name,
    ///     because <c>SelectorCompiler</c> rewrites it to <c>:is()</c> before ExCSS sees it.
    /// </remarks>
    public static IEnumerable<string> Names(StyleEngine engine, Selector selector, SimpleSelectorKind kind) {
        var table = engine.Selectors;

        for (var index = 0; index < selector.Count; index++) {
            var compound = table.Compound(selector.Start + index);

            for (var part = 0; part < compound.Count; part++) {
                var simple = table.Simple(compound.Start + part);

                if (simple.Kind == kind) {
                    yield return engine.Names.NameOf(simple.NameId);
                    continue;
                }

                if (simple.Kind is not (SimpleSelectorKind.Not or SimpleSelectorKind.Is or SimpleSelectorKind.Has)) {
                    continue;
                }

                for (var nested = 0; nested < simple.NestedCount; nested++) {
                    foreach (var name in Names(engine, table.Nested(simple.NestedStart + nested), kind)) {
                        yield return name;
                    }
                }
            }
        }
    }
}
