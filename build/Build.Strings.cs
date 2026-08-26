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
    static readonly Regex LooseIdPattern = new(
        """new\s+StringId\(\s*"(?<id>[^"]+)"\s*,""",
        RegexOptions.Compiled
    );

    Target CheckStrings => definition => definition
        .Description("Fails if a declared string id is used nowhere, or if a call site repeats an id a declaration class already declares")
        .Executes(() => {
                var sources = RootDirectory
                    .GlobFiles("**/*.cs", "**/*.vxml")
                    .Where(path => !path.ToString().Contains("/bin/", StringComparison.Ordinal))
                    .Where(path => !path.ToString().Contains("/obj/", StringComparison.Ordinal))
                    .Where(path => !path.ToString().Contains("/artifacts/", StringComparison.Ordinal))

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

                foreach (var violation in violations) {
                    Log.Error("{Violation}", violation);
                }

                Assert.True(
                    violations.Count == 0,
                    $"{violations.Count} string-catalogue violation(s). See the errors above."
                );

                Census(declared, text);

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
    ///     The analyzer refuses this inside an assembly that owns a declaration class. This is the
    ///     cross-assembly case it cannot see, because an id is a *value* in an initialiser and a
    ///     referenced assembly's metadata does not carry it — which is why this half is textual.
    /// </remarks>
    static void Repeated(
        IReadOnlyDictionary<string, (string Class, string Member, string Id, AbsolutePath File)> declared,
        IReadOnlyDictionary<AbsolutePath, string> text,
        List<string> violations
    ) {
        foreach (var (path, contents) in text) {
            foreach (Match loose in LooseIdPattern.Matches(contents)) {
                var id = loose.Groups["id"].Value;

                if (!declared.TryGetValue(id, out var declaration) || declaration.File == path) {
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

    /// <summary>How many ids are built at a call site and declared in no class at all.</summary>
    /// <remarks>
    ///     ⚠ <b>A measurement, not a check, and it is named that way because the difference matters.</b>
    ///     Closing it is a migration of every editor module's command labels into a declaration class,
    ///     and a handful of those ids cannot be declared at all — <c>WaterMode.cs:247</c> builds
    ///     <c>"editor.command." + id</c> in a loop over a mode's tools, which is a legitimate shape a
    ///     declaration class has no way to express. Failing on the population today would either stop
    ///     the build or force a rule with an exception list longer than itself. What this prints is the
    ///     size of the gap, so that it is a number somebody decided to live with rather than one nobody
    ///     had.
    /// </remarks>
    static void Census(
        IReadOnlyDictionary<string, (string Class, string Member, string Id, AbsolutePath File)> declared,
        IReadOnlyDictionary<AbsolutePath, string> text
    ) {
        var undeclared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contents in text.Values) {
            foreach (Match loose in LooseIdPattern.Matches(contents)) {
                if (!declared.ContainsKey(loose.Groups["id"].Value)) {
                    undeclared.Add(loose.Groups["id"].Value);
                }
            }
        }

        if (undeclared.Count == 0) {
            return;
        }

        Log.Warning(
            "{Count} string id(s) are built at a call site and declared in no class, so no All list "
            + "carries them and no translator's template contains them. Not a failure — see "
            + "docs/plan/11 § As built for what closing it costs.",
            undeclared.Count
        );
    }
}
