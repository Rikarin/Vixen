// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     Every <c>A &gt; B</c> the sheets declare between two bare type selectors, held against a
///     committed census.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The domain, and not yet the verdict.</b> <see cref="TypeSelectorReachTests" /> catches
///         a type selector nothing creates. A combinator rule whose parent-child <i>pairing</i> is
///         wrong is a different shape and both its known instances were found by a person reading a
///         sheet — one of them not cosmetic, since <c>compositor-editor &gt; node-canvas</c> never
///         matched (the child is <c>node-graph</c>) and the compositor's graph was drawn at zero
///         width. Both sides of that rule name real tags, so every reach test in this directory was
///         green while it was dead.
///     </para>
///     <para>
///         ⚠ <b>Four passes have tried to adjudicate that and stopped, and they reported four
///         different sizes for the thing they were adjudicating — 88, 72, 75 and 70 pairs, from four
///         hand-rolled parsers over the same seventeen sheets.</b> That is the finding this file
///         acts on. A gate cannot be argued about while its domain is a number somebody re-derives
///         with a regular expression each time, so the extracted <i>set</i> is committed and compared
///         exactly, read out of the compiled selector table rather than off the text — which is what
///         a text census gets wrong about <c>:is()</c>, <c>:not()</c> and a compound carrying classes
///         as well as a tag.
///     </para>
///     <para>
///         <b>What this catches today, which is less than the issue asks for and is not nothing.</b> A
///         combinator rule cannot be added, deleted or re-spelt without a reviewed line moving in
///         <c>CombinatorPairs.txt</c> — so a new <c>compositor-editor &gt; node-canvas</c> arrives in
///         a diff that says so, next to the sheet it came from, instead of arriving invisibly among
///         three hundred and sixty selectors. <b>What it does not do</b> is decide whether a pairing
///         is live: that needs a model of what the tree can build, and the measurements below are the
///         evidence about how far the obvious models get.
///     </para>
///     <para>
///         ⚠ <b>Measured on 2026-09-06, and it refutes the recipe the audit trail keeps recommending
///         for a second time.</b> Markup nesting alone — a lower-case <c>.vxml</c> element directly
///         inside another — proves <b>3</b> of the 88 pairs here. Adding the type→tag map everybody
///         asks for (301 names, from <c>TagName</c> overrides plus every <c>@component</c>/<c>@tag</c>
///         pair) so that a capitalised child resolves to the tag it creates takes that to <b>14</b>.
///         The other 74 are parents built in C#, most of them a control assembling its own parts —
///         <c>expander &gt; expander-header</c>, <c>split-view &gt; split-bar</c>,
///         <c>tabs &gt; tab-panels</c> — which no markup scan can ever see. So the missing piece is
///         not the tag map; it is a model of C# construction, and the runtime oracle the last audit
///         suggests is the only proposal left that does not need one.
///     </para>
///     <para>
///         <b>Child combinators only.</b> A descendant pairing is a much weaker claim — an ancestor at
///         any depth — and every one of the four measurements this file is reconciling was of
///         <c>A &gt; B</c>, so widening the domain here would break the comparison it exists to make.
///         The twelve descendant-only selectors in <c>AssetEditorTheme.vcss</c> are deliberately out.
///     </para>
/// </remarks>
public class CombinatorPairTests {
    /// <summary>Where the census lives, relative to the repository root.</summary>
    const string CensusFile = "Core/Vixen.Ui.Styling.Tests/CombinatorPairs.txt";

    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the census back instead of asserting it.</summary>
    static bool Regenerating =>
        Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>One <c>A &gt; B</c>, and the first sheet that declares it.</summary>
    /// <param name="Parent">The tag on the left of the <c>&gt;</c>.</param>
    /// <param name="Child">The tag on the right.</param>
    /// <param name="Sheet">The sheet, relative to the repository root.</param>
    public readonly record struct Pair(string Parent, string Child, string Sheet) {
        /// <summary>The row as the census writes it, without the sheet.</summary>
        public string Text => $"{Parent} > {Child}";
    }

    /// <summary>The scan, done once.</summary>
    public static IReadOnlyList<Pair> Declared => declared ??= ReadDeclared();

    static IReadOnlyList<Pair>? declared;

    /// <summary>How many sheets the scan read.</summary>
    public static int Sheets => sheets;

    static int sheets;

    /// <summary>The premise every assertion below rests on: the scan found the sheets and read them.</summary>
    /// <remarks>
    ///     ⚠ <b>Anti-vacuity, and it is three claims rather than one floor.</b> A scan that read no
    ///     sheet, or read them and understood no selector, agrees with a census of the same emptiness
    ///     — and this repository has twice had a floor eaten by success. So: the sheets were found,
    ///     enough pairs came out of them to be the real table, and three named pairings that a person
    ///     has chased to the bottom in the issue's own comments are present by name. The third is
    ///     what a floor cannot give: it fails if the extraction starts reading the compound on the
    ///     wrong side of the combinator, which is a bug that keeps the count exactly right.
    /// </remarks>
    [Fact]
    public void The_pair_scan_actually_ran() {
        _ = Declared;

        Assert.True(Sheets >= 15, $"the scan read only {Sheets} stylesheets, which is not this repository");

        Assert.True(
            Declared.Count >= 60,
            $"the scan found only {Declared.Count} child-combinator pairs, against 70-88 in four "
            + "independent measurements — the extraction stopped seeing combinators rather than the "
            + "sheets losing them"
        );

        var text = Declared.Select(pair => pair.Text).ToHashSet(StringComparer.Ordinal);

        // ⚠ Live, all three, each traced to its two ends by hand in `Rikarin/Vixen#531`'s comments:
        // `editor-shell > menu-bar` through `MenuPresenter`'s `host`, `node-graph > node-canvas`
        // through `NodeGraphView`, and `inspector-row > inspector-label` in the inspector's own rows.
        // They are here as the orientation control — a scan that paired each compound with the wrong
        // neighbour would report the same count and none of these three.
        Assert.Contains("editor-shell > menu-bar", text, StringComparer.Ordinal);
        Assert.Contains("node-graph > node-canvas", text, StringComparer.Ordinal);
        Assert.Contains("inspector-row > inspector-label", text, StringComparer.Ordinal);
    }

    /// <summary>The extracted set is the committed one, exactly and in both directions.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exact, not a floor, and the reason is the audit trail this file opens with.</b>
    ///         Four passes measured this domain and got four answers; a count would have agreed with
    ///         any of them. A set cannot: a rule added, deleted, or re-spelt on either side of the
    ///         <c>&gt;</c> moves a line, and the line names the sheet.
    ///     </para>
    ///     <para>
    ///         The census is read off disk and its absence throws rather than emptying the expected
    ///         set — the answer to "what does this print on the day it does not run" is a failure,
    ///         not a pass over two empty lists.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_child_combinator_pair_is_in_the_committed_census() {
        var path = Path.Combine(RepositoryScan.Root(), CensusFile);

        if (Regenerating) {
            Write(path);
        }

        var census = Census(path);
        var found = Declared.ToDictionary(pair => pair.Text, pair => pair.Sheet, StringComparer.Ordinal);

        var arrived = found.Keys.Where(pair => !census.ContainsKey(pair)).Order(StringComparer.Ordinal).ToList();
        var departed = census.Keys.Where(pair => !found.ContainsKey(pair)).Order(StringComparer.Ordinal).ToList();

        var moved = found.Keys
            .Where(pair => census.TryGetValue(pair, out var sheet) && !sheet.Equals(found[pair], StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0 && moved.Count == 0,
            $"""
             The census of child-combinator pairs is out of date.

             Declared by a sheet and not in {CensusFile}:
             {Lines(arrived.Select(pair => $"{pair}  — in {found[pair]}"))}

             In {CensusFile} and declared by no sheet any more — delete the row:
             {Lines(departed)}

             In {CensusFile} under a different sheet:
             {Lines(moved.Select(pair => $"{pair}  — now {found[pair]}, was {census[pair]}"))}

             ⚠ Read the diff rather than regenerating past it. A pairing arriving is a claim that some
             element with the parent tag can hold a child with the child tag — the claim
             `compositor-editor > node-canvas` made falsely for as long as it was in a sheet, with
             both of its tags real and every reach test green. Re-run with VIXEN_REGENERATE=1 to
             write the file back once the diff is what you meant.
             """
        );
    }

    /// <summary>The committed census: the pair, and the sheet it was declared in.</summary>
    static Dictionary<string, string> Census(string path) {
        var lines = File.ReadAllLines(path);

        // "It has rows" cannot stand in for "it was read": a truncated file and a repository with no
        // combinator rules in it are both zero rows, and only one of them still has the header.
        Assert.True(
            lines.Count(line => line.StartsWith('#')) >= 5,
            $"{CensusFile} has lost its header, so it was emptied rather than answered."
        );

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in lines) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var parts = text.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Assert.True(
                parts.Length == 2 && parts[0].Contains(" > ", StringComparison.Ordinal),
                $"{CensusFile} is malformed at '{text}'. Each row is `parent > child<TAB>sheet`."
            );

            rows[parts[0]] = parts[1];
        }

        return rows;
    }

    static void Write(string path) {
        var text = new StringBuilder();

        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith('#') && line.Trim().Length != 0) {
                break;
            }

            text.AppendLine(line);
        }

        foreach (var pair in Declared) {
            text.Append(pair.Text).Append('\t').AppendLine(pair.Sheet);
        }

        File.WriteAllText(path, text.ToString());
    }

    static string Lines(IEnumerable<string> rows) {
        var joined = string.Join("\n", rows.Select(row => $"  {row}"));

        return joined.Length == 0 ? "  (none)" : joined;
    }

    /// <summary>Every <c>A &gt; B</c> between two bare type selectors, from every committed sheet.</summary>
    /// <remarks>
    ///     ⚠ <b>A compound carrying classes as well as a tag still counts, and that is the right
    ///     reading rather than a loose one.</b> <c>fact-value.numeric &gt; numeric-input</c> is a
    ///     narrower rule than <c>fact-value &gt; numeric-input</c>, but the parent-child adjacency it
    ///     needs is the same one — so the pair is a necessary condition of the rule firing either
    ///     way. What is deliberately excluded is a type inside <c>:is()</c>, <c>:not()</c> or
    ///     <c>:has()</c>: <c>:not(toolbar) &gt; button</c> names <c>toolbar</c> and asks for a parent
    ///     that is anything else, so reading it as a pairing would invent one.
    /// </remarks>
    static List<Pair> ReadDeclared() {
        var root = RepositoryScan.Root();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<Pair>();
        var paths = RepositoryScan.Files("*.vcss");

        sheets = paths.Count;

        foreach (var path in paths) {
            var engine = new StyleEngine();
            engine.Load(File.ReadAllText(path), StyleOrigin.Author);

            var sheet = Path.GetRelativePath(root, path).Replace('\\', '/');
            var table = engine.Selectors;

            for (var rule = 0; rule < engine.Rules.Count; rule++) {
                var selector = engine.Rules[rule].Selector;

                for (var index = 1; index < selector.Count; index++) {
                    if (table.Compound(selector.Start + index).Combinator != Combinator.Child) {
                        continue;
                    }

                    if (TypeOf(engine, selector.Start + index - 1) is not { } parent
                        || TypeOf(engine, selector.Start + index) is not { } child) {
                        continue;
                    }

                    if (seen.Add($"{parent} > {child}")) {
                        found.Add(new Pair(parent, child, sheet));
                    }
                }
            }
        }

        found.Sort(static (left, right) => string.CompareOrdinal(left.Text, right.Text));
        return found;
    }

    /// <summary>The tag one compound tests for, at the top level, or null if it tests for none.</summary>
    static string? TypeOf(StyleEngine engine, int compound) {
        var table = engine.Selectors;
        var parts = table.Compound(compound);

        for (var part = 0; part < parts.Count; part++) {
            var simple = table.Simple(parts.Start + part);

            if (simple.Kind == SimpleSelectorKind.Type) {
                return engine.Names.NameOf(simple.NameId);
            }
        }

        return null;
    }
}
