// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     The scoped half of #531: whether each whole type-only selector the sheets declare matches
///     anything in the running editor, rather than whether its rightmost pairing occurs somewhere.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The pair gate cannot tell <c>shape-fields fact-value &gt; numeric-input</c> from
///         <c>node-inspector fact-value &gt; numeric-input</c>, and the second is #525's own dead
///         rule.</b> Both end in the same pairing, the pairing is live under <c>shape-fields</c>,
///         and so the pair gate is satisfied by the live one while the scoped one matches nothing.
///         What distinguishes them is the whole selector — the ancestor that scopes the pairing —
///         and a whole selector is exactly what the cascade's matcher evaluates. So this asks it:
///         for every selector made only of tags and combinators, does it match at least one element
///         of an editor at some depth of the ladder the pair sweeps already climb?
///     </para>
///     <para>
///         ⚠ <b>Type-only selectors and nothing else, which is a domain a static tree can
///         decide.</b> A compound carrying a class or a state — <c>.selected</c>, <c>:hover</c>,
///         <c>:disabled</c> — depends on what a fixture did to the element, so its not matching says
///         nothing about the rule. A selector spelled entirely in tags is a claim about construction,
///         which is what the sweeps measure. The domain here is therefore wider than the pair
///         domain in one direction (descendant combinators, three-compound chains) and narrower in
///         the other (no compound with a class beside its tag), and it is committed as a set for
///         the reason <c>CombinatorPairs.txt</c> is: a count somebody re-derives is not a domain.
///     </para>
///     <para>
///         ⚠ <b>Over the same four editors, and that is why it is a partial of the pair sweep
///         rather than a class of its own.</b> The document sweep opens forty-odd documents and is
///         most of this assembly's minutes; a second class starting its own editors would pay that
///         again to read the same trees. Each sweep hands its editor here before disposing it, and
///         a selector's verdict is the shallowest depth that matched it.
///     </para>
///     <para>
///         ⚠ <b>The census is proofs plus explicit non-proofs, and the loud direction is a row's
///         verdict moving to <c>-</c>.</b> A selector that matched at some depth and now matches at
///         none is a rule that stopped applying where it scopes — the <c>compositor-editor &gt;
///         node-canvas</c> shape, as a whole selector rather than as a pairing. A row moving the
///         other way, from <c>-</c> to a depth, is ordinary and is regenerated after reading. What
///         the file does <em>not</em> claim is that a <c>-</c> row is dead: the ladder reaches what
///         it reaches, and a rule under a dialog nobody opens or a control the editor never builds
///         is unjudged here exactly as it is in the pair census.
///     </para>
/// </remarks>
public partial class EditorCombinatorPairTests {
    /// <summary>The scoped census: every type-only selector, and the shallowest depth that matched it.</summary>
    const string ScopedFile = "Editor/Vixen.Editor.App.Tests/ScopedSelectors.txt";

    /// <summary>What a row's verdict says when no sweep matched it.</summary>
    const string Unmatched = "-";

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

    /// <summary>The order the verdicts are decided in: a selector is credited to the first depth that matched it.</summary>
    static readonly Depth[] Ladder = [Depth.Started, Depth.Panels, Depth.Documents, Depth.Overlays];

    /// <summary>What each sweep matched, filled by <see cref="Scope" /> as the sweep runs.</summary>
    static readonly Dictionary<Depth, HashSet<string>> Matched = [];

    /// <summary>How many selectors the shallowest sweep asked about, so that "it ran" can be asserted.</summary>
    static int scopedAsked;

    /// <summary>
    ///     Three whole selectors the scoped census must credit, one per combinator shape, named so a
    ///     scan that matched nothing or matched the wrong way round cannot pass.
    /// </summary>
    /// <remarks>
    ///     <c>editor-shell &gt; menu-bar</c> is the presenter pairing as a whole selector;
    ///     <c>toolbar icon-button</c> is a descendant rule a started editor's own toolbars satisfy;
    ///     <c>import-settings &gt; scroll-view &gt; scroll-content</c> is the one three-compound child
    ///     chain the sheets spell, reached only by a document.
    /// </remarks>
    static readonly (string Selector, Depth AtMost)[] Scoped = [
        ("editor-shell > menu-bar", Depth.Started),
        ("toolbar icon-button", Depth.Started),
        ("import-settings > scroll-view > scroll-content", Depth.Documents)
    ];

    /// <summary>The three named selectors spelled the wrong way round, asked of every editor on the ladder.</summary>
    /// <remarks>
    ///     ⚠ Asked, not merely looked up. A verdict is read out of the sets the sweeps filled, and
    ///     those hold domain selectors only — so a mirror that was never asked is absent from them
    ///     whatever the matcher would have said, and a control written as a lookup passes by
    ///     construction. Each sweep evaluates these alongside the domain and records what matched.
    /// </remarks>
    static readonly string[] Mirrored = [
        "menu-bar > editor-shell",
        "icon-button toolbar",
        "scroll-content > scroll-view > import-settings"
    ];

    /// <summary>Which of <see cref="Mirrored" /> some sweep credited, which must stay empty.</summary>
    static readonly HashSet<string> MirrorSightings = new(StringComparer.Ordinal);

    /// <summary>The domain: every type-only selector a committed sheet declares, with the first sheet declaring it.</summary>
    static IReadOnlyDictionary<string, string> ScopedDomain => scopedDomain ??= ReadScopedDomain();

    static IReadOnlyDictionary<string, string>? scopedDomain;

    /// <summary>The premise: the scan found the sheets, the selectors, and a running editor answered them.</summary>
    /// <remarks>
    ///     ⚠ Three claims rather than one floor, on the pair sweep's reasoning. The domain has the
    ///     size of the real sheets; the editor was asked about every selector in it; and three named
    ///     selectors of three shapes are credited no deeper than the depth that first builds them.
    ///     The third is what a floor cannot give: a matcher that answered true for everything, or a
    ///     domain read from the wrong files, keeps every count and fails here by name.
    /// </remarks>
    [Fact]
    public void The_scoped_scan_actually_ran() {
        Assert.True(ScopedDomain.Count >= 150, $"the sheets declare only {ScopedDomain.Count} type-only selectors, which is not the real table.");

        // Force the shallowest sweep, which is what fills `scopedAsked`.
        _ = Observed;

        Assert.Equal(ScopedDomain.Count, scopedAsked);

        foreach (var (selector, atMost) in Scoped) {
            Assert.True(ScopedDomain.ContainsKey(selector), $"'{selector}' is no longer a rule any sheet declares.");

            var verdict = VerdictOf(selector);

            Assert.True(
                verdict is not null && Array.IndexOf(Ladder, verdict.Value) <= Array.IndexOf(Ladder, atMost),
                $"'{selector}' was expected at {atMost} at the latest and was {(verdict is null ? "matched by no sweep" : $"first matched at {verdict}")}."
            );
        }

        // ⚠ The mirror control: a whole selector spelled the wrong way round must not be credited.
        // The pair sweep's own reversed-walk sabotage collapses to zero there; here a matcher that
        // ignored the combinator's direction, or a scan that credited whatever it was handed,
        // would credit both spellings, and only this sees it.
        _ = VerdictOf(Scoped[0].Selector);
        Assert.Empty(MirrorSightings);
    }

    /// <summary>The scoped census is exactly what is committed, verdict for verdict.</summary>
    /// <remarks>
    ///     <para>
    ///         A row is <c>selector&lt;TAB&gt;verdict</c>, where the verdict is the shallowest depth
    ///         that matched it or <c>-</c>. ⚠ <b>A verdict moving to <c>-</c> is the loud one</b>:
    ///         the rule still stands in a sheet and no editor on the ladder builds anything under
    ///         the scope it names any more. A verdict moving between depths, or from <c>-</c> to a
    ///         depth, is the ladder reaching more or less of the same tree and is regenerated after
    ///         reading.
    ///     </para>
    ///     <para>
    ///         Read against the sheets and against the four sweeps, in both directions, so that a
    ///         selector arriving in a sheet is a row somebody has to write, and one leaving a sheet
    ///         is a row somebody has to delete.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_type_only_selector_the_sheets_declare_is_in_the_scoped_census_with_its_verdict() {
        var root = Root();
        var path = Path.Combine(root, ScopedFile);

        var measured = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var selector in ScopedDomain.Keys) {
            measured[selector] = VerdictOf(selector)?.ToString() ?? Unmatched;
        }

        if (Regenerating) {
            Write(path, measured.Select(static row => $"{row.Key}\t{row.Value}"));
        }

        var census = ScopedCensus(path);

        var arrived = measured.Keys.Where(selector => !census.ContainsKey(selector)).ToList();
        var departed = census.Keys.Where(selector => !measured.ContainsKey(selector)).Order(StringComparer.Ordinal).ToList();

        var lost = measured
            .Where(row => census.TryGetValue(row.Key, out var was) && was != Unmatched && row.Value == Unmatched)
            .Select(static row => row.Key)
            .ToList();

        var moved = measured
            .Where(row => census.TryGetValue(row.Key, out var was) && was != row.Value && !(was != Unmatched && row.Value == Unmatched))
            .Select(row => $"{row.Key}: {census[row.Key]} -> {row.Value}")
            .ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0 && lost.Count == 0 && moved.Count == 0,
            $"""
             The scoped census is out of date.

             ⚠ Matched by some sweep before and by NO sweep now — a sheet still declares each of these
             and nothing on the ladder builds anything under the scope it names:
             {Lines(lost)}

             Declared and not in {ScopedFile} — regenerate once you have read them:
             {Lines(arrived)}

             In {ScopedFile} and declared by no sheet any more — regenerate to drop them:
             {Lines(departed)}

             Credited to a different depth than the census says — regenerate after reading:
             {Lines(moved)}

             Re-run with VIXEN_REGENERATE=1 to write this back, after reading the first list.
             """
        );

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"scoped gate: {measured.Count} declared, {measured.Count(static row => row.Value != Unmatched)} matched somewhere, "
            + $"{measured.Count(static row => row.Value == Unmatched)} matched by no sweep"
        );
    }

    /// <summary>Asks one editor about every selector in the domain, and records what it matched.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through <c>UiTest.Get</c>, which is the cascade's own compiler and matcher.</b> A
    ///         selector that matched here is one the style engine would have applied a rule for, on
    ///         this tree, with the combinators meaning what they mean in a sheet. A second matcher
    ///         written for the test would agree on <c>a &gt; b</c> and disagree on <c>a b c</c>, and
    ///         disagree silently.
    ///     </para>
    ///     <para>
    ///         Compiled into the document's own tables on each call, which is what <c>Get</c> does;
    ///         two hundred selectors at four depths is well under a second of compiling.
    ///     </para>
    /// </remarks>
    static void Scope(EditorSession fixture, Depth depth) {
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var selector in ScopedDomain.Keys) {
            if (fixture.Ui.Get(selector).Count > 0) {
                matched.Add(selector);
            }
        }

        foreach (var mirror in Mirrored) {
            if (fixture.Ui.Get(mirror).Count > 0) {
                MirrorSightings.Add($"{mirror} ({depth})");
            }
        }

        if (depth == Depth.Started) {
            scopedAsked = ScopedDomain.Count;
        }

        Matched[depth] = matched;
    }

    /// <summary>The shallowest depth that matched a selector, or null when none did.</summary>
    /// <remarks>Forces every sweep, since a verdict is over the whole ladder.</remarks>
    static Depth? VerdictOf(string selector) {
        _ = Observed;
        _ = Opened;
        _ = Documents;
        _ = Overlays;

        foreach (var depth in Ladder) {
            if (Matched.TryGetValue(depth, out var matched) && matched.Contains(selector)) {
                return depth;
            }
        }

        return null;
    }

    /// <summary>Every type-only selector in every committed sheet, with the first sheet that declares it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Off the sheet's text, and the reason that is safe here and was not for the pair
    ///         domain.</b> Four hand-rolled parsers gave that domain four sizes because a compound
    ///         carrying a class beside its tag, or a tag inside <c>:is()</c>, is a judgement a regular
    ///         expression makes differently each time. This domain admits a selector only when the
    ///         whole of it is tags and combinators, which one pattern decides without judgement — and
    ///         every selector it admits is then handed to the real compiler by <see cref="Scope" />,
    ///         which throws on anything that is not a selector. The set is committed regardless.
    ///     </para>
    ///     <para>
    ///         Comments are stripped first because a sheet's prose spells selectors too, and the text
    ///         before each <c>{</c> is read whatever block it is nested in, so a rule inside
    ///         <c>@layer components { … }</c> is found without knowing what a layer is.
    ///     </para>
    /// </remarks>
    static Dictionary<string, string> ReadScopedDomain() {
        var root = Root();
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

    /// <summary>The committed scoped census: selector to verdict.</summary>
    static Dictionary<string, string> ScopedCensus(string path) {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in Rows(path, ScopedFile)) {
            var columns = row.Split('\t');

            Assert.True(
                columns.Length == 2 && (columns[1] == Unmatched || Enum.TryParse<Depth>(columns[1], out _)),
                $"{ScopedFile} is malformed at '{row}'. Each row is `selector<TAB>depth-or-dash`."
            );

            Assert.True(rows.TryAdd(columns[0].Trim(), columns[1].Trim()), $"'{columns[0]}' is listed twice in {ScopedFile}.");
        }

        return rows;
    }
}
