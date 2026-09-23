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

    /// <summary>Every stylesheet in the repository, loaded into one engine.</summary>
    /// <returns>An engine holding every rule the working tree declares.</returns>
    /// <remarks>
    ///     ⚠ <b>One engine for sheets no single document loads together</b>, and that is the
    ///     conservative direction rather than the sloppy one. A tag some <i>other</i> assembly's
    ///     sheet happens to style can only make a name look reachable and pass, never make a clean
    ///     one fail — so the answer this gives is a floor on the defect, which is the right way
    ///     round for a gate.
    /// </remarks>
    public static StyleEngine Sheets() {
        var engine = new StyleEngine();

        foreach (var path in Files("*.vcss")) {
            engine.Load(File.ReadAllText(path), StyleOrigin.Author);
        }

        return engine;
    }

    /// <summary>
    ///     What the sheets would have given <paramref name="declared" /> and do not give
    ///     <paramref name="written" />.
    /// </summary>
    /// <param name="engine">The loaded sheets.</param>
    /// <param name="written">The tag the element actually carries.</param>
    /// <param name="declared">The spelling to compare against.</param>
    /// <returns>The property names only the second spelling resolves, sorted ordinally.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Resolved rather than looked up</b>, because the question is what the cascade
    ///         computes and not what a selector list contains. A tag can be named by a rule that
    ///         never applies — sealed in a <c>@media</c>, or beaten outright — and reporting that as
    ///         a lost style would be a failure with nothing behind it. Two elements, same parent,
    ///         same absence of classes: the only thing that differs is the spelling, so anything the
    ///         second one has is exactly what the spelling cost.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A shorthand is a resolved property of its own, so it does not answer in
    ///         longhands.</b> <c>overflow</c> survives the cascade under its own name rather than
    ///         being expanded away, so a rule writing <c>overflow-x</c> and <c>overflow-y</c> in
    ///         place of it is reported here as losing <c>overflow</c> — even though
    ///         <c>LayoutStyleBuilder</c> reads the shorthand and then lets the longhands override
    ///         it, so the two describe the same box. That is the one false accusation this
    ///         comparison can make; it is cheap to answer by writing the shorthand, and it is
    ///         pinned by
    ///         <c>RetaggedControlTests.The_comparison_is_over_resolved_properties_and_a_shorthand_is_one_of_them</c>
    ///         so that the day the cascade starts expanding shorthands, that test says so rather
    ///         than this paragraph quietly becoming true. ⚠ It is worth naming because the rules
    ///         most likely to be written per-axis are the sideways-scrolling ones, which is exactly
    ///         where the next caller will arrive.
    ///     </para>
    /// </remarks>
    public static List<string> Missing(StyleEngine engine, string written, string declared) {
        var a = engine.Resolver.Resolve(engine.Tree, engine.Tree.CreateElement(written));
        var b = engine.Resolver.Resolve(engine.Tree, engine.Tree.CreateElement(declared));

        var lost = new List<string>();

        for (var i = 0; i < b.Properties.Length; i++) {
            if (!a.TryGet(b.Properties[i], out _)) {
                lost.Add(engine.Properties.NameOf(b.Properties[i]));
            }
        }

        lost.Sort(StringComparer.Ordinal);

        return lost;
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
