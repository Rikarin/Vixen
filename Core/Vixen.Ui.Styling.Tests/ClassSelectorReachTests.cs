// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>Whether the class names the repository's stylesheets declare are ones it ever writes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The third dead shape, and until this file nothing measured it.</b>
///         <see cref="TypeSelectorReachTests" /> asks two questions — whether a written tag is spelt
///         the way the sheets declare it, and whether a declared <i>type</i> selector names a tag
///         anything creates. Neither can see a <c>.class</c> whose name is written nowhere at all:
///         <c>A_type_selector_names_a_tag_rather_than_a_class</c> asks the opposite question, and
///         <c>Every_type_selector_names_a_tag_the_repository_writes</c> walks bare type selectors
///         only.
///     </para>
///     <para>
///         ⚠ <b>And it is the commonest shape an abandoned view leaves behind</b>, because a rebuilt
///         view usually keeps its tags and renames its classes. The finding that produced this file is
///         <c>button.asset-picker-row</c>, the sibling of the <c>asset-picker-list</c> rules #693
///         removed: <c>AssetPicker.Build</c> makes an <c>asset-grid</c> with
///         <c>.asset-picker-grid</c> and a label with <c>.asset-picker-empty</c>, and has never made
///         a per-row button. Two rules that fired on nothing, next to five that had already been
///         found by the tag direction alone.
///     </para>
///     <para>
///         ⚠ <b>A class is composed at run time far more often than a tag is, which is why the tag
///         direction landed first and why this one needs a rule the other did not.</b>
///         <c>group.AddClass("axis-" + Axes[axis].ToLowerInvariant())</c> is in this tree, and
///         <c>InspectorTheme.vcss</c> declares <c>.axis-x</c> through <c>.axis-w</c> — four rules that
///         work, whose names appear in no source. A census that could not see them would accuse four
///         innocent rules on its first run, which is how a gate loses its reader. So a quoted name
///         ending in a hyphen, written on a line that also names one of the class APIs, is read as a
///         <i>prefix</i> and reaches every class that starts with it — see <see cref="Prefixes" />.
///     </para>
///     <para>
///         ⚠ <b>The prefix rule is deliberately line-local, and the loose version of it is nearly
///         useless.</b> Taking every hyphen-terminated literal in the repository gives 87 prefixes,
///         which between them excuse most of the vocabulary; requiring the literal to sit on a line
///         that says <c>AddClass</c>, <c>RemoveClass</c>, <c>ToggleClass</c>, <c>HasClass</c> or
///         <c>classNames</c> gives <b>two</b> — <c>axis-</c> and <c>lines-</c> — and both of them are
///         real. That is the same trick <c>A_type_selector_names_a_tag_rather_than_a_class</c> plays
///         with <c>TagSite</c>: match the line rather than the argument, so a name assembled by an
///         expression is still recognised.
///     </para>
///     <para>
///         ⚠ <b>Over-inclusive on "written" and exact on the census, which is the safe way round.</b>
///         Any quoted, class-shaped name anywhere in a <c>.cs</c> or a <c>.vxml</c> counts as written,
///         including one that is not a class at all. The cost is a false negative — a dead rule whose
///         name happens to be a word somewhere else survives — and the benefit is that no working
///         rule is ever accused. A gate that cries wolf about a rule that works is a gate nobody keeps
///         green.
///     </para>
///     <para>
///         ⚠ <b>A <c>.vxml</c> is scanned for quoted names as well as for its <c>class</c>
///         attributes, and the attribute scan alone is not enough.</b>
///         <c>class="@(Alive ? "add-component" : "add-component hidden")"</c> is real —
///         <c>ComponentsView.vxml</c> — and a <c>class\s*=\s*"([^"]*)"</c> over it captures
///         <c>@(Alive ?</c> and nothing else, because the value's own quotes end the match. The
///         quoted-name scan finds <c>"add-component"</c> anyway, since a regular expression does not
///         have to agree with the markup about where a string starts.
///     </para>
/// </remarks>
public partial class ClassSelectorReachTests {
    /// <summary>Where the census lives, relative to the repository root.</summary>
    const string CensusFile = "Core/Vixen.Ui.Styling.Tests/UnreachedClasses.txt";

    /// <summary>A quoted, class-shaped name.</summary>
    [GeneratedRegex("\"(?<name>[A-Za-z][A-Za-z0-9-]*)\"")]
    private static partial Regex Quoted { get; }

    /// <summary>A call that puts a class on an element, or hands a control one.</summary>
    /// <remarks>
    ///     Matched against the whole line rather than against the argument, so a class assembled from
    ///     a literal and an expression is still recognised as one. That is what makes
    ///     <see cref="Prefixes" /> two names rather than eighty-seven.
    /// </remarks>
    [GeneratedRegex(@"\b(?:AddClass|RemoveClass|ToggleClass|HasClass|SetClass|classNames|ClassNames)\b")]
    private static partial Regex ClassSite { get; }

    /// <summary>A markup <c>class</c> attribute, whose contents are classes.</summary>
    [GeneratedRegex(@"\bclass\s*=\s*""(?<names>[^""]*)""")]
    private static partial Regex MarkupClasses { get; }

    /// <summary>What the sources write: whole names, and the prefixes classes are built from.</summary>
    /// <param name="Names">Every quoted class-shaped name and markup class, to where it first appears.</param>
    /// <param name="Prefixes">Hyphen-terminated names written at a class site, to where they appear.</param>
    public sealed record Sources(
        IReadOnlyDictionary<string, string> Names,
        IReadOnlyDictionary<string, string> Prefixes
    );

    /// <summary>The scan, done once.</summary>
    public static Sources Written => sources ??= ReadSources();

    static Sources? sources;

    /// <summary>Every class selector the repository's stylesheets declare, to the first sheet.</summary>
    public static IReadOnlyList<(string Name, string Sheet)> Declared => declared ??= ReadDeclared();

    static IReadOnlyList<(string Name, string Sheet)>? declared;

    /// <summary>
    ///     Every class selector in every sheet is a name the repository writes, and the ones that are
    ///     not are the committed census — exactly, in both directions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Committed and compared exactly rather than counted</b>, which is
    ///         <c>UnreachedSelectors.txt</c>'s bargain and for its reason. A floor — "no more than
    ///         one" — cannot say that a name came back to life, so a row would outlive the rule it
    ///         excuses and the next dead class would take the seat it vacated. Equality fails in both
    ///         directions: a name that starts being written must leave the file, and a new one cannot
    ///         arrive without a line naming the issue that will close it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The file is empty, and that is the answer rather than the absence of one.</b> The
    ///         measured set was one name — <c>asset-picker-row</c> — and #778 deleted the two rules
    ///         that declared it rather than writing it down. What stops this reading green on the day
    ///         the scan breaks is therefore <see cref="The_class_scan_actually_ran" /> and not this
    ///         file: an empty census compared against a collapsed measurement is exactly what a
    ///         broken regular expression produces.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_class_selector_names_a_class_the_repository_writes() {
        var written = Written;

        var unreached = Declared
            .Where(candidate => !Reached(written, candidate.Name))
            .Select(candidate => candidate.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        var census = Census();

        var arrived = unreached.Where(name => !census.ContainsKey(name)).ToList();
        var departed = census.Keys.Where(name => !unreached.Contains(name)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The census of class selectors nothing writes is out of date.

             Declared by a sheet and written by nothing, and not in {CensusFile}:
             {Lines(arrived.Select(name => $"{name}  — declared in {Declared.First(d => d.Name == name).Sheet}"))}

             In {CensusFile}, and now written after all — delete the row:
             {Lines(departed)}

             A rule whose class selector names a class no C# and no markup ever applies fires on
             nothing, silently — and a class is the half of a rebuilt view that gets renamed, so
             this is where an abandoned feature's stylesheet ends up. If the class was renamed, the
             rule wants the new name; if the view went away, the rule goes with it; if the class is
             composed at run time from a prefix this scan cannot see, the prefix wants to be a
             literal at the AddClass call and not an interpolation.
             """
        );
    }

    /// <summary>
    ///     The scan found the repository, found both kinds of name in it, and can still tell a
    ///     reached class from an unreached one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without this the census above passes loudest on the day it stops running.</b> Every
    ///     way this file can break — a moved root, a regular expression that stopped matching, a walk
    ///     that found no <c>.vcss</c> — breaks it by measuring nothing, and nothing compared against
    ///     an empty census is a pass. So each stage is asserted against a name that is what it is for
    ///     reasons older than this file: <c>hidden</c> is written all over the tree, <c>axis-</c> is
    ///     the prefix that rescues four working rules, and <c>axis-x</c> is a declared class reachable
    ///     only through it.
    /// </remarks>
    [Fact]
    public void The_class_scan_actually_ran() {
        var written = Written;

        Assert.True(RepositoryScan.Files("*.vcss").Count >= 10, "no stylesheets were found to check against.");
        Assert.True(RepositoryScan.Files("*.cs").Count >= 100, "no C# sources were found to scan.");
        Assert.True(Declared.Count >= 100, "almost no class selectors were read out of the sheets.");
        Assert.True(written.Names.Count >= 100, "almost no quoted names were found in the sources.");

        // Written outright, and reached without the prefix rule needing to say anything.
        Assert.True(written.Names.ContainsKey("hidden"), "'hidden' was not found in any source.");
        Assert.True(Reached(written, "hidden"), "'hidden' is written everywhere and was not reached.");

        // ⚠ The prefix rule, and the class it exists for. `VectorDrawers` writes `"axis-"` and never
        // `"axis-x"`, so all four axis rules are reachable only through the prefix — and the prefix
        // is only found because the scan reads the line rather than the argument.
        Assert.True(written.Prefixes.ContainsKey("axis-"), "'axis-' was not recognised as a class prefix.");
        Assert.False(written.Names.ContainsKey("axis-x"), "'axis-x' is written outright now, so it proves nothing.");
        Assert.True(Reached(written, "axis-x"), "'axis-x' was not reached through its prefix.");
        Assert.Contains(Declared, candidate => candidate.Name == "axis-x");

        // ⚠ And the rule is not vacuous in the other direction: a class-shaped name nobody writes and
        // no prefix covers is still unreached, which is the state the census is a list of.
        Assert.False(Reached(written, "asset-picker-row"), "the scan reaches a name #778 deleted.");
        Assert.DoesNotContain(Declared, candidate => candidate.Name == "asset-picker-row");
    }

    /// <summary>
    ///     ⚠ The prefix rule is line-local, and the loose version of it would excuse the vocabulary.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The number is the argument, so it is asserted rather than written in a remark.</b> A
    ///     prefix reaches every class that starts with it, so the set has to stay small enough to read
    ///     — the day it is thirty, a dead rule can hide behind one of them and the census means less
    ///     than it says. This is the tripwire for a widened <see cref="ClassSite" />, which is the
    ///     change that would do it.
    /// </remarks>
    [Fact]
    public void The_prefix_rule_stays_narrow_enough_to_read() {
        var prefixes = Written.Prefixes;

        Assert.True(
            prefixes.Count <= 8,
            "the class-prefix rule now excuses "
            + $"{prefixes.Count} prefixes — {string.Join(", ", prefixes.Keys.Order(StringComparer.Ordinal))}. Each "
            + "of them reaches every class that starts with it, so a set this size is an exemption "
            + "list rather than a rule and the census behind it means less than it claims."
        );
    }

    /// <summary>Whether the sources write a class, outright or as a prefix a run-time name is built from.</summary>
    static bool Reached(Sources written, string name) =>
        written.Names.ContainsKey(name)
        || written.Prefixes.Keys.Any(prefix =>
            name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)
        );

    /// <summary>The committed census: a name, the issue that will close it, and the reason.</summary>
    static Dictionary<string, string> Census() {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);

        var lines = File.ReadAllLines(Path.Combine(RepositoryScan.Root(), CensusFile));

        // "It has rows" cannot stand in for "it was read" on a census that is legitimately empty: an
        // answered file and a truncated one are both zero rows, and only one of them still carries
        // the header saying what the file is for.
        Assert.True(
            lines.Count(line => line.StartsWith('#')) >= 5,
            $"{CensusFile} has lost its header, so it was emptied rather than answered."
        );

        foreach (var line in lines) {
            var text = line.Trim();

            if (text.Length == 0 || text.StartsWith('#')) {
                continue;
            }

            var parts = text.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Assert.True(
                parts.Length >= 3 && parts[1].StartsWith('#'),
                $"{CensusFile} is malformed at '{text}'. Each row is `name<TAB>#issue<TAB>reason`."
            );

            rows[parts[0]] = parts[2];
        }

        return rows;
    }

    static string Lines(IEnumerable<string> names) {
        var joined = string.Join("\n", names.Select(name => $"  {name}"));

        return joined.Length == 0 ? "  (none)" : joined;
    }

    static Sources ReadSources() {
        var root = RepositoryScan.Root();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);

        void Note(Dictionary<string, string> into, string name, string path, int line) =>
            into.TryAdd(name, $"{Path.GetRelativePath(root, path)}:{line}");

        foreach (var path in RepositoryScan.Files("*.cs")) {
            // The two census files write every name they talk about, so scanning either would make
            // each of them look written — `asset-picker-row` is in this one's own remarks.
            if (Census(path)) {
                continue;
            }

            var line = 0;

            foreach (var text in File.ReadLines(path)) {
                line++;

                // A class name in a doc comment is prose, and this file's remarks are full of them.
                var trimmed = text.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) {
                    continue;
                }

                var applies = ClassSite.IsMatch(text);

                foreach (Match match in Quoted.Matches(text)) {
                    var name = match.Groups["name"].Value;

                    Note(names, name, path, line);

                    if (applies && name.EndsWith('-')) {
                        Note(prefixes, name, path, line);
                    }
                }
            }
        }

        foreach (var path in RepositoryScan.Files("*.vxml")) {
            var line = 0;

            foreach (var text in File.ReadLines(path)) {
                line++;

                foreach (Match match in Quoted.Matches(text)) {
                    Note(names, match.Groups["name"].Value, path, line);
                }

                foreach (Match match in MarkupClasses.Matches(text)) {
                    foreach (var name in match.Groups["names"].Value.Split(
                                 ' ',
                                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                             )) {
                        Note(names, name, path, line);
                    }
                }
            }
        }

        return new Sources(names, prefixes);
    }

    /// <summary>Whether a source file is one of the two reach censuses, which write every name they discuss.</summary>
    internal static bool Census(string path) =>
        Path.GetFileName(path) is "TypeSelectorReachTests.cs" or "ClassSelectorReachTests.cs";

    static List<(string Name, string Sheet)> ReadDeclared() {
        var root = RepositoryScan.Root();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(string, string)>();

        foreach (var path in RepositoryScan.Files("*.vcss")) {
            var engine = new StyleEngine();
            engine.Load(File.ReadAllText(path), StyleOrigin.Author);

            var sheet = Path.GetRelativePath(root, path);

            for (var rule = 0; rule < engine.Rules.Count; rule++) {
                foreach (var name in RepositoryScan.Names(
                             engine,
                             engine.Rules[rule].Selector,
                             SimpleSelectorKind.Class
                         )) {
                    if (seen.Add(name)) {
                        list.Add((name, sheet));
                    }
                }
            }
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }
}
