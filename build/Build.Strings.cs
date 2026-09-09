// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

/// <summary>
///     The half of doc 11's `Strings.Resource` property that no single compilation can see.
/// </summary>
/// <remarks>
///     <para>
///         Doc 46 § A3 states the property as <i>"an id used nowhere and an id declared nowhere are
///         both build errors"</i>. <c>StringDeclarationAnalyzer</c> closes the second half inside an
///         assembly that owns a declaration class, and the compiler closes it for a member name — a
///         call site naming <c>EditorStrings.Whatever</c> that does not exist is CS0117 and always
///         was. What neither can answer is the first half: <b>six of <c>ControlStrings</c>' fifteen
///         declarations are used only from <c>Vixen.Ui.Controls.Advanced</c></b>, so an analyzer
///         running over <c>Vixen.Ui.Controls</c> that called an unreferenced declaration dead would
///         be wrong about six of them, and one that counted the <c>All</c> list as a use would be
///         vacuous — every declaration is in <c>All</c> by construction.
///     </para>
///     <para>
///         So this reads the tree. It is textual rather than semantic on purpose: a declaration is
///         <c>Class.Member</c> at every site that uses one, in C# and in <c>.vxml</c> alike, and the
///         markup half is the reason a Roslyn answer would have needed the generated code as well.
///     </para>
///     <para>
///         ⚠ <b>It found seven on the day it was written</b>, which is the only evidence that it can
///         fail: <c>MenuView</c> (the menu is Window), <c>NotificationsTitle</c> /
///         <c>NotificationsEmpty</c> (the panel is the message log and has its own),
///         <c>KeyBindingConflict</c> (superseded by <c>KeysConflict</c>), <c>DialogOk</c> (no shell
///         dialog says OK) — all five deleted — and <c>CommandUndo</c> / <c>CommandRedo</c>, which
///         were the interesting pair: the editor registered Undo and Redo with a
///         <c>new StringId("editor.command.undo", "Undo")</c> written at the call site, so the id in
///         every translator's template was <c>editor.command.edit.undo</c> and the id the running
///         editor looked up was <c>editor.command.undo</c>. Translating the editor's Undo item was
///         impossible and nothing said so.
///     </para>
///     <para>
///         ⚠ <b>And it was still true of Save when the census closed.</b> <c>EditorStrings.CommandSave</c>
///         declared <c>editor.command.file.save</c> = "Save"; the shell registered <c>file.save</c>
///         with <c>new StringId("editor.command.save", "Save Scene")</c>. Neither half of this gate
///         could see it — <c>Unused</c> counted a localisation test naming the declaration as a use,
///         and <c>Repeated</c> compares ids rather than the commands that carry them. What found it
///         was the migration <see cref="Undeclared" /> now enforces: two declarations wanting the
///         same member name is how a duplicated command shows up.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>Where a declaration class may live. Everything else is scanned only for uses.</summary>
    /// <remarks>
    ///     The declaration class is recognised by its shape rather than by its path — a static class
    ///     with <c>StringId</c> properties and an <c>All</c> list beside them — which is the shape
    ///     doc 46 § A3 says must stay unchanged so that a generator outside this repository emits
    ///     the same thing. Recognising it by name would make this gate a rule about two files
    ///     instead of about the shape.
    /// </remarks>
    static readonly Regex DeclarationPattern = new(
        """public\s+static\s+StringId\s+(?<member>\w+)\s*\{\s*get;\s*\}\s*=\s*new\(\s*"(?<id>[^"]+)"\s*,""",
        RegexOptions.Compiled
    );

    static readonly Regex DeclarationClassPattern = new(
        @"public\s+static\s+class\s+(?<name>\w+)",
        RegexOptions.Compiled
    );

    static readonly Regex AllListPattern = new(
        @"IReadOnlyList<StringId>\s+All\s*\{\s*get;\s*\}",
        RegexOptions.Compiled
    );

    /// <summary>Every construction of a <c>StringId</c> whose id is a literal, anywhere.</summary>
    /// <remarks>
    ///     ⚠ <b>Two shapes, because for a year this saw only the first and the second is the one a
    ///     call site is most naturally written in.</b> A field or property initialiser target-types
    ///     its <c>new</c> — <c>static readonly StringId CategoryWater = new("editor.category.water",
    ///     "Water");</c> — and the type name that this pattern anchors on is then simply not in the
    ///     text. Twenty-one production ids were built that way and neither the census nor
    ///     <see cref="Repeated" /> could see any of them; <c>editor.category.scene</c> was
    ///     constructed three times, in three files, and the gate written to stop exactly that
    ///     reported nothing. The second pattern is anchored on the declared type instead, which is
    ///     what an initialiser does carry.
    /// </remarks>
    static readonly Regex[] LooseIdPatterns = [
        new("""new\s+StringId\(\s*"(?<id>[^"]+)"\s*,""", RegexOptions.Compiled),
        new(
            """StringId\s+\w+\s*(?:\{\s*get;\s*\}\s*)?=\s*new\(\s*"(?<id>[^"]+)"\s*,""",
            RegexOptions.Compiled
        )
    ];

    /// <summary>Every id a file builds, under either shape.</summary>
    static IEnumerable<Match> LooseIds(string contents) =>
        LooseIdPatterns.SelectMany(pattern => pattern.Matches(contents));

    /// <summary>
    ///     This checkout's own <c>.claude/</c>, with a separator, so a prefix test cannot match a
    ///     sibling directory whose name merely starts the same way.
    /// </summary>
    string ClaudeDirectory => (RootDirectory / ".claude").ToString() + "/";

    Target CheckStrings => definition => definition
        .Description("Fails if a declared string id is used nowhere, if a call site repeats an id a declaration class already declares, if a shipping call site builds one no class declares, or if it builds one out of a run-time value")
        .Executes(() => {
                var sources = RootDirectory
                    .GlobFiles("**/*.cs", "**/*.vxml")
                    .Where(path => !path.ToString().Contains("/bin/", StringComparison.Ordinal))
                    .Where(path => !path.ToString().Contains("/obj/", StringComparison.Ordinal))
                    .Where(path => !path.ToString().Contains("/artifacts/", StringComparison.Ordinal))

                    // ⚠ Other checkouts of this same repository, and ONLY the other ones. A git
                    // worktree lives under .claude/worktrees/ and holds a full copy of the tree at
                    // whatever commit it was made at, so without this the gate reports every
                    // violation once per worktree and — worse — reports declarations that were
                    // deleted on master but survive in a checkout from before the deletion. Found
                    // exactly that way: four ids this gate's own change had already removed came
                    // back three times each, out of three stale copies.
                    //
                    // ⚠ Anchored at the root, never matched as a substring, and that is the whole
                    // difference between a gate that skips the sibling checkouts and one that cannot
                    // run inside a worktree at all. A worktree's own RootDirectory *is*
                    // …/.claude/worktrees/<name>, so every path under it contains "/.claude/": a
                    // substring test excludes the entire tree, and the assertion below then fires
                    // with "the glob is wrong". Which it was not — the exclusion was.
                    //
                    // Both halves of that were got wrong once each, a day apart, and two agents
                    // working in worktrees found the second half independently. The assertion below
                    // is what made it a broken gate rather than a silent one: with no sources the
                    // census finds no declarations and no call sites, and CheckStrings would report
                    // Succeeded having read nothing.
                    .Where(path => !path.ToString().StartsWith(ClaudeDirectory, StringComparison.Ordinal))

                    // ⚠ The analyzer's own tests, whose C# is *data*: every fixture is a declaration
                    // class inside a raw string literal, written to be reported on. Reading them as
                    // source makes this gate fail on the tests that prove the other half of the same
                    // property works. Excluded by name and with a reason, the way CheckArchitecture
                    // excludes Tools/Vixen.Templates/templates/ — which is not this repository's code
                    // either.
                    .Where(path => !path.ToString().Contains("/Vixen.Ui.Generators.Tests/", StringComparison.Ordinal))
                    .ToList();

                Assert.True(sources.Count > 0, "Found no sources to check — the glob is wrong.");

                var text = sources.ToDictionary(path => path, path => path.ReadAllText());
                var declarations = new List<(string Class, string Member, string Id, AbsolutePath File)>();

                foreach (var (path, contents) in text) {
                    if (!AllListPattern.IsMatch(contents)) {
                        // Not a declaration class. A `.vxml` code block or a fixture may hold a single
                        // StringId without claiming to be where an assembly's ids live; `All` is what
                        // makes the claim, because `All` is the whole of what a translator sees.
                        continue;
                    }

                    if (DeclarationClassPattern.Match(contents) is not { Success: true } owner) {
                        continue;
                    }

                    foreach (Match declaration in DeclarationPattern.Matches(contents)) {
                        declarations.Add(
                            (owner.Groups["name"].Value,
                                declaration.Groups["member"].Value,
                                declaration.Groups["id"].Value,
                                path)
                        );
                    }
                }

                Assert.True(
                    declarations.Count > 0,
                    "Found no string declarations at all. Either the tree has none — in which case "
                    + "this gate is checking nothing and should be deleted rather than left passing "
                    + "— or DeclarationPattern no longer matches the shape."
                );

                var declared = declarations
                    .GroupBy(declaration => declaration.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                var violations = new List<string>();

                Unused(declarations, text, violations);
                Repeated(declared, text, violations);
                Undeclared(declared, text, violations);
                Constructed(text, violations);

                foreach (var violation in violations) {
                    Log.Error("{Violation}", violation);
                }

                Assert.True(
                    violations.Count == 0,
                    $"{violations.Count} string-catalogue violation(s). See the errors above."
                );

                Log.Information(
                    "Checked {Declarations} declarations in {Classes} declaration class(es) against {Files} files; no violations.",
                    declarations.Count,
                    declarations.Select(declaration => declaration.Class).Distinct(StringComparer.Ordinal).Count(),
                    sources.Count
                );
            }
        );

    /// <summary>"An id used nowhere is a build error."</summary>
    /// <remarks>
    ///     ⚠ <b>The declaring file does not count as a use</b>, and that is the whole difficulty. Every
    ///     declaration appears twice in its own file — as the property and as a name in <c>All</c> —
    ///     so a check that looked for <c>Class.Member</c> anywhere would find both and pass on a
    ///     string nothing shows. The <c>All</c> list is the duplication this gate exists because of;
    ///     it cannot also be the evidence against it.
    /// </remarks>
    static void Unused(
        IReadOnlyList<(string Class, string Member, string Id, AbsolutePath File)> declarations,
        IReadOnlyDictionary<AbsolutePath, string> text,
        List<string> violations
    ) {
        foreach (var declaration in declarations) {
            var reference = new Regex(
                @"\b" + Regex.Escape(declaration.Class) + @"\." + Regex.Escape(declaration.Member) + @"\b"
            );

            var used = text.Any(file => file.Key != declaration.File && reference.IsMatch(file.Value));

            if (!used) {
                violations.Add(
                    $"{declaration.Class}.{declaration.Member} ('{declaration.Id}') is declared and used nowhere. "
                    + "It is in every translator's template and on no surface, so somebody is paid to "
                    + "translate a word the application does not say. Use it, or delete it."
                );
            }
        }
    }

    /// <summary>An id written a second time at a call site, where the two sides can drift.</summary>
    /// <remarks>
    ///     <para>
    ///         The analyzer refuses this inside an assembly that owns a declaration class. This is the
    ///         cross-assembly case it cannot see, because an id is a *value* in an initialiser and a
    ///         referenced assembly's metadata does not carry it — which is why this half is textual.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>///</c> line is not a call site</b>, and this file is where that stopped
    ///         being theoretical: the remarks above quote the initialiser shape the gate had been
    ///         blind to, using a real id, and the gate then failed on its own explanation of itself.
    ///         Nothing drifts when prose and a declaration disagree — a reader sees both.
    ///     </para>
    /// </remarks>
    static void Repeated(
        IReadOnlyDictionary<string, (string Class, string Member, string Id, AbsolutePath File)> declared,
        IReadOnlyDictionary<AbsolutePath, string> text,
        List<string> violations
    ) {
        foreach (var (path, contents) in text) {
            foreach (var loose in LooseIds(contents)) {
                var id = loose.Groups["id"].Value;

                if (!declared.TryGetValue(id, out var declaration) || declaration.File == path) {
                    continue;
                }

                if (InDocComment(contents, loose.Index)) {
                    continue;
                }

                violations.Add(
                    $"{RootDirectory.GetRelativePathTo(path)} builds a StringId for '{id}', which "
                    + $"{declaration.Class}.{declaration.Member} already declares. The id and its source "
                    + "text are then written twice and nothing keeps them equal — use the declaration."
                );
            }
        }
    }

    /// <summary>
    ///     How many ids a shipping surface builds at a call site and declares in no class at all —
    ///     a ceiling now, where it used to be a line in the log.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This was a measurement and not a check, and the measurement was 178.</b> Every
    ///         one of them was a word the editor says and no translator's template contains, because
    ///         <c>Strings.Template</c> exports <c>All</c> lists and an id nothing declares is in no
    ///         <c>All</c> list. They are declared now — <c>EditorStrings</c> carries all of them —
    ///         so the number a maintainer has to keep is zero and the gate says so rather than
    ///         logging a warning nobody reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The old note said a handful of the population could not be declared and named
    ///         <c>WaterMode</c>'s <c>"editor.command." + id</c> as the example. That was never in the
    ///         population.</b> Both patterns need a string literal where the id goes, so a
    ///         concatenation is invisible to this check — which means it is invisible to the
    ///         translator's template as well, and the ceiling can be zero precisely because the
    ///         genuinely irreducible ids were never being counted. <see cref="Constructed" /> is
    ///         that half, and <c>Vixen.Ui.StringFamily</c> is the declaration shape that lets one
    ///         stop being irreducible.
    ///     </para>
    ///     <para>
    ///         Two exclusions, both because they are not surfaces:
    ///         <list type="bullet">
    ///             <item>
    ///                 A <c>.Tests</c> assembly. A fixture invents <c>test.brush</c> to feed a
    ///                 registry; nobody translates it and declaring it would put it in a template.
    ///             </item>
    ///             <item>
    ///                 A <c>///</c> line. Three of the ids this check saw on the day it was written
    ///                 were prose — <c>IEditorPlugin</c>'s worked example, and this file's own
    ///                 account of the <c>CommandUndo</c> defect. A documented example is not a call
    ///                 site, and rewriting one to satisfy a gate makes the documentation worse.
    ///             </item>
    ///         </list>
    ///     </para>
    /// </remarks>
    const int UndeclaredCeiling = 0;

    /// <summary>Applies <see cref="UndeclaredCeiling" />.</summary>
    /// <param name="declared">Every id a declaration class carries.</param>
    /// <param name="text">Every source file, by path.</param>
    /// <param name="violations">Where a breach is recorded.</param>
    static void Undeclared(
        IReadOnlyDictionary<string, (string Class, string Member, string Id, AbsolutePath File)> declared,
        IReadOnlyDictionary<AbsolutePath, string> text,
        List<string> violations
    ) {
        var undeclared = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (path, contents) in text) {
            if (path.ToString().Contains(".Tests/", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var loose in LooseIds(contents)) {
                var id = loose.Groups["id"].Value;

                if (declared.ContainsKey(id) || InDocComment(contents, loose.Index)) {
                    continue;
                }

                undeclared.TryAdd(id, RootDirectory.GetRelativePathTo(path).ToString());
            }
        }

        if (undeclared.Count > UndeclaredCeiling) {
            foreach (var (id, path) in undeclared) {
                violations.Add(
                    $"{path} builds a StringId for '{id}', which no declaration class declares. No "
                    + "All list carries it, so it is in no translator's template and the editor says "
                    + "a word nobody can translate — declare it in EditorStrings and use that."
                );
            }

            return;
        }

        // ⚠ The other half, and the reason this is a constant rather than a literal zero: a ceiling
        // that only ever fails upwards is one nobody lowers. `CheckWhitespace`'s exemption list has
        // the same rule, for the same reason.
        Assert.True(
            undeclared.Count == UndeclaredCeiling,
            $"{undeclared.Count} undeclared string id(s), under a ceiling of {UndeclaredCeiling}. "
            + "Lower UndeclaredCeiling to what the tree now has, so the number stays one somebody "
            + "decided rather than one that drifted."
        );
    }

    /// <summary>Every construction of a <c>StringId</c>, whatever its first argument is.</summary>
    /// <remarks>
    ///     Anchored on the <c>new</c> alone rather than on a literal, because the whole point of this
    ///     half is the constructions the literal patterns cannot see. What follows the bracket is
    ///     read separately by <see cref="LiteralFirstArgument" />.
    /// </remarks>
    static readonly Regex[] ConstructionPatterns = [
        new("""new\s+StringId\(""", RegexOptions.Compiled),
        new("""\bStringId\s+\w+\s*(?:\{\s*get;\s*\}\s*)?=\s*new\(""", RegexOptions.Compiled)
    ];

    /// <summary>A first argument that is a plain string literal and nothing else.</summary>
    static readonly Regex LiteralFirstArgument = new("""^\s*"(?:[^"\\]|\\.)*"\s*,""", RegexOptions.Compiled);

    /// <summary>
    ///     How many ids a shipping surface builds out of a run-time value — the half of the census
    ///     that no pattern anchored on a literal could ever have counted.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the population <see cref="Undeclared" />'s ceiling of zero was measured
    ///         without.</b> <c>new StringId("editor.command." + id, label)</c> has no literal where
    ///         the id goes, so it was in neither pattern, so it was in no violation and in no
    ///         warning — and, for exactly the same reason, in no <c>All</c> list and no translator's
    ///         template. The ids that genuinely could not be declared were the ones nothing counted,
    ///         which is the wrong way round for a measurement to be wrong.
    ///     </para>
    ///     <para>
    ///         <b>What makes compliance possible is <c>Vixen.Ui.StringFamily</c></b>: a declaration
    ///         that stands for a whole set of ids under one prefix, keyed by what the call site
    ///         already has. So the rule is not "declare every id" — some of these are one command per
    ///         tool and one per digit — it is that a family is declared once and indexed at the call
    ///         site rather than rebuilt there.
    ///     </para>
    ///     <para>
    ///         Three exclusions, and a ceiling rather than zero:
    ///         <list type="bullet">
    ///             <item>
    ///                 A file holding an <c>All</c> list. A declaration class is where a family is
    ///                 built, and building one is <see cref="Regex" />-indistinguishable from
    ///                 building an id at a call site.
    ///             </item>
    ///             <item>
    ///                 A <c>.Tests</c> assembly and a <c>///</c> line, for <see cref="Undeclared" />'s
    ///                 two reasons.
    ///             </item>
    ///             <item>
    ///                 <c>Tools/Vixen.Templates/templates</c>, which is not this repository's code —
    ///                 <c>CheckArchitecture</c> excludes it by name for the same reason.
    ///             </item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ceiling cannot honestly be zero, and one of the survivors says why.</b>
    ///         <c>DeclaredContributions</c> builds a label for a command a *plugin* declared with an
    ///         attribute; the id is in an assembly this build has never seen, so no declaration in
    ///         this tree can cover it and a family is the wrong answer as well. That one wants
    ///         <c>Strings.Template</c> to take a plugin's own declarations, which is a different
    ///         piece of work.
    ///     </para>
    /// </remarks>
    const int ConstructedCeiling = 46;

    /// <summary>Applies <see cref="ConstructedCeiling" />.</summary>
    /// <param name="text">Every source file, by path.</param>
    /// <param name="violations">Where a breach is recorded.</param>
    static void Constructed(IReadOnlyDictionary<AbsolutePath, string> text, List<string> violations) {
        var built = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (path, contents) in text) {
            if (path.ToString().Contains(".Tests/", StringComparison.Ordinal)
                || path.ToString().Contains("/Vixen.Templates/templates/", StringComparison.Ordinal)

                // ⚠ The one file that is *supposed* to build an id out of a run-time value: turning a
                // prefix and a key into a StringId is what a family is. Excluded by name and with a
                // reason rather than by widening the shape test, because the shape test is what
                // recognises a declaration class and StringFamily is not one.
                || path.ToString().EndsWith("Core/Vixen.Ui/StringFamily.cs", StringComparison.Ordinal)
                || AllListPattern.IsMatch(contents)) {
                continue;
            }

            foreach (var pattern in ConstructionPatterns) {
                foreach (Match construction in pattern.Matches(contents)) {
                    var argument = construction.Index + construction.Length;

                    if (LiteralFirstArgument.IsMatch(contents[argument..])
                        || InDocComment(contents, construction.Index)) {
                        continue;
                    }

                    var line = contents.AsSpan(0, construction.Index).Count('\n') + 1;

                    built.Add($"{RootDirectory.GetRelativePathTo(path)}:{line}");
                }
            }
        }

        if (built.Count > ConstructedCeiling) {
            foreach (var site in built) {
                violations.Add(
                    $"{site} builds a StringId out of a run-time value. The id exists only while the "
                    + "editor runs, so it is in no All list, Strings.Template does not export it and "
                    + "no translator's template contains the word — declare the set as a StringFamily "
                    + "and index it here."
                );
            }

            return;
        }

        // ⚠ Downwards only, on UndeclaredCeiling's terms.
        Assert.True(
            built.Count == ConstructedCeiling,
            $"{built.Count} constructed string id(s), under a ceiling of {ConstructedCeiling}. Lower "
            + "ConstructedCeiling to what the tree now has, so the number stays one somebody decided "
            + "rather than one that drifted."
        );
    }

    /// <summary>Whether an offset falls on a <c>///</c> line.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="index">Where the match started.</param>
    /// <returns>Whether the line it is on is a documentation comment.</returns>
    static bool InDocComment(string contents, int index) {
        var start = contents.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;

        return contents.AsSpan(start, index - start).TrimStart().StartsWith("///", StringComparison.Ordinal);
    }
}
