// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Vixen.Ui.Styling.Testing;

/// <summary>Every whole selector the committed sheets spell entirely in tags and combinators.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Compiled into two assemblies rather than written twice, which is why its namespace
///         matches neither.</b> <c>EditorCombinatorPairTests</c>' scoped half asks a running editor
///         whether each of these selectors matches anything, and commits the answer to
///         <c>ScopedSelectors.txt</c>; <c>CombinatorCensusDriftTests</c> in
///         <c>Vixen.Ui.Styling.Tests</c> asks whether that file still names exactly the selectors
///         the sheets declare, which needs no editor. Two readers of "which selectors are type-only"
///         would be two chances to disagree, and the disagreement would read as drift in one suite
///         and as a clean run in the other (<c>Rikarin/Vixen#1349</c>). <c>VxmlLines</c> is shared
///         the same way.
///     </para>
///     <para>
///         ⚠ <b>Off the sheet's text, and the reason that is safe here and was not for the pair
///         domain.</b> Four hand-rolled parsers gave that domain four sizes because a compound
///         carrying a class beside its tag, or a tag inside <c>:is()</c>, is a judgement a regular
///         expression makes differently each time. This domain admits a selector only when the whole
///         of it is tags and combinators, which one pattern decides without judgement — and every
///         selector it admits is then handed to the real compiler by the editor's scoped sweep, which
///         throws on anything that is not a selector. The set is committed regardless.
///     </para>
///     <para>
///         Comments are stripped first because a sheet's prose spells selectors too, and the text
///         before each <c>{</c> is read whatever block it is nested in, so a rule inside
///         <c>@layer components { … }</c> is found without knowing what a layer is.
///     </para>
/// </remarks>
static class TypeOnlySelectors {
    /// <summary>A tag as a sheet spells it: lower case, digits and hyphens.</summary>
    const string Tag = "[a-z][a-z0-9-]*";

    /// <summary>
    ///     A selector made of tags joined by child or descendant combinators and nothing else.
    /// </summary>
    /// <remarks>
    ///     Sibling combinators are out on purpose: <c>a + b</c> is about order among siblings, which
    ///     the sweeps do not claim to fix, and no sheet in the tree spells one between two bare tags.
    /// </remarks>
    static readonly Regex TypeOnly = new($"^{Tag}(?:\\s*>\\s*{Tag}|\\s+{Tag})+$", RegexOptions.Compiled);

    /// <summary>Every type-only selector in every committed sheet, to the first sheet that declares it.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Each selector in one spelling — <c>a &gt; b</c>, single spaces — and its sheet.</returns>
    public static Dictionary<string, string> Read(string root) {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in Sheets(root)) {
            var text = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            var sheet = Path.GetRelativePath(root, path).Replace('\\', '/');

            foreach (Match block in Regex.Matches(text, @"([^{};]+)\{")) {
                var prelude = block.Groups[1].Value.Trim();

                if (prelude.StartsWith('@')) {
                    continue;
                }

                foreach (var part in prelude.Split(',')) {
                    var selector = string.Join(' ', part.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

                    // One spelling per selector, so `a>b` and `a > b` are the same row.
                    selector = Regex.Replace(selector, @"\s*>\s*", " > ");

                    if (TypeOnly.IsMatch(selector)) {
                        found.TryAdd(selector, sheet);
                    }
                }
            }
        }

        return found;
    }

    /// <summary>Every committed stylesheet, walked the way the styling tests walk them.</summary>
    /// <remarks>
    ///     Pruned by directory name during the walk rather than filtered afterwards, for
    ///     <c>RepositoryScan</c>'s reason: <c>.claude/worktrees</c> holds whole checkouts of this
    ///     repository, and a sweep that descended into them would be measuring other people's work.
    /// </remarks>
    static List<string> Sheets(string root) {
        string[] unwalked = [".git", ".claude", "bin", "obj", "artifacts", "node_modules"];
        var found = new List<string>();

        void Walk(string directory) {
            found.AddRange(Directory.EnumerateFiles(directory, "*.vcss"));

            foreach (var child in Directory.EnumerateDirectories(directory)) {
                if (!unwalked.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                    Walk(child);
                }
            }
        }

        Walk(root);
        found.Sort(StringComparer.Ordinal);

        return found;
    }
}
