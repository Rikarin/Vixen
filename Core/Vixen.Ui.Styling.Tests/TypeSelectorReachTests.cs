// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     Whether the tag names the repository's C# writes are the ones its stylesheets declare.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An unstyled element still renders</b>, which is the whole reason this file exists.
///         <see cref="NameTable" /> interns ordinally, so <c>Add("World-title")</c> and
///         <c>world-title { … }</c> are two different tags and the rule simply never fires — no
///         exception, no diagnostic, no missing element, just a heading that quietly lost its
///         <c>font-weight</c> and its <c>margin-top</c> for two years. Nothing in the compiler, the
///         binder or the cascade can see it, because <c>tag</c> is a <c>string</c> and every
///         string is a valid tag.
///     </para>
///     <para>
///         ⚠ <b>The assertion is deliberately narrower than "every tag matches some rule".</b> A tag
///         that no rule names is usually fine — a plain container styled entirely by classes is the
///         normal case, and there are hundreds of them. Nor is "every rule has a tag" assertable: the
///         sheets carry well over a hundred type selectors nothing currently creates, and most are a
///         control's parts written ahead of a panel that will use them. Either of those as a gate
///         would be a wall of failures nobody could keep green, and a gate nobody keeps green is a
///         gate nobody reads.
///     </para>
///     <para>
///         ⚠ <b>The mirror direction — a rule with no tag — is two questions and not one, and only
///         the second one has a discriminator this file can use.</b> A type selector that names
///         nothing is the normal case just argued. What is <i>not</i> normal is a type selector whose
///         name the repository does write, somewhere, but never as a tag: that is a name the author
///         had in hand and spelled into the wrong kind of selector, and
///         <see cref="A_type_selector_names_a_tag_rather_than_a_class" /> is that question. It found
///         nine — <c>flame-hue-0 … flame-hue-7</c>, applied with <c>AddClass</c> onto an element whose
///         tag is <c>flame-bar</c> and therefore cannot also be a hue, and <c>asset-tiles</c>, applied
///         with <c>AddClass</c> onto a <c>virtualizing-grid</c>. Nine rules that had never once fired,
///         and one of them was a user-visible feature: every bar in the flame chart drew with no fill
///         at all.
///     </para>
///     <para>
///         ⚠ <b>A misspelling has no such discriminator in general, and this file does not pretend
///         otherwise.</b> <c>text-box</c> against a tag of <c>textbox</c> is not distinguishable from
///         two legitimately different names by shape alone, and edit distance over the actual tag
///         vocabulary is not the answer either: at distance one it pairs <c>asset-tiles</c> with
///         <c>asset-tile</c>, which is a grid and the thing inside it. <i>Hyphenation</i> is the one
///         sub-case that is decidable, because a hyphen in this vocabulary is not free variation —
///         it is the word separator, and no two of the twelve hundred names the repository writes as
///         tags differ only by where the hyphens fall.
///         <see cref="No_two_tags_differ_only_by_hyphenation" /> asserts that premise beside the rule
///         that rests on it, so the day it stops holding the failure says the discriminator is gone
///         rather than accusing an innocent selector.
///     </para>
///     <para>
///         ⚠ <b>What <i>is</i> unambiguous is a hyphenated tag that differs from a declared one only
///         by case.</b> Case carries meaning in VXML — <c>Vixen.Ui.Markup</c>'s rule is that a
///         capitalised tag names a component type and a lowercase one names an intrinsic element — but
///         it can only carry it where a type name is possible, and a C# type name cannot contain a
///         hyphen. <c>ComponentEmitter</c> emits a capitalised tag as <c>Child&lt;Tag&gt;</c>, so
///         <c>&lt;World-title&gt;</c> in a <c>.vxml</c> is a C# syntax error and cannot reach a build.
///         A capitalised hyphenated tag can therefore only exist in a C# string literal, and there it
///         can only ever be a typo.
///     </para>
///     <para>
///         ⚠ <b>The scan is over-inclusive and the assertion is exact</b>, which is what keeps the
///         noise out without a hand-maintained ignore list. The regex below happily matches
///         <c>Add("Sub-graphs/Tint")</c> and <c>Add("Sec-WebSocket-Key: abc")</c>, neither of which is
///         a tag; they survive to the second stage and are dismissed there, because the second stage
///         does not pattern-match at all — it asks the loaded sheets, through the real cascade,
///         whether the lowercase spelling resolves to declarations the written spelling does not.
///         Only a name the sheets actually style can fail.
///     </para>
/// </remarks>
public partial class TypeSelectorReachTests {
    /// <summary>
    ///     A tag literal at an element-creation call site, with a hyphen and at least one capital.
    /// </summary>
    /// <remarks>
    ///     The five ways C# names a tag: <c>UiElement.Add</c>, <c>UiDocument.Create</c>,
    ///     <c>Control.Part</c>, <c>BuildContext.Element</c> and <c>Containers.Prepend</c>. The optional
    ///     leading argument is for <c>Element(parent, "tag")</c>, whose tag is second.
    /// </remarks>
    [GeneratedRegex(""""
        \b(?:Add|Create|Part|Element|Prepend)\s*(?:<[^<>()]{0,80}>)?\s*\(\s*(?:[A-Za-z_][\w.]*\s*,\s*)?"(?<tag>[A-Za-z][A-Za-z0-9-]*)"
        """")]
    private static partial Regex TagLiteral { get; }

    /// <summary>Every hyphenated, non-lowercase tag literal the repository's C# writes.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off disk rather than out of an assembly</b>, the way <c>StylesheetTests</c> and
    ///     <c>SharedUiShaderTests</c> read theirs: the subject is a string in a source file, and by
    ///     the time it is a compiled constant the spelling is exactly as invisible as it is at run
    ///     time. <c>bin</c> and <c>obj</c> are skipped so the answer does not depend on what the last
    ///     build left behind.
    ///     <para>
    ///         ⚠ <b><c>.claude</c> is skipped too, and that is not housekeeping — it is the difference
    ///         between a test about this repository and a test about whatever else is on the disk.</b>
    ///         Agent worktrees live under <c>.claude/worktrees/</c> and are full checkouts, so a sweep
    ///         that walks them asserts against other people's uncommitted work. This test failed a
    ///         gate run by finding the very <c>World-title</c> it exists to prevent, in a worktree
    ///         where it had not been fixed yet — a true statement about a tree nobody was asking
    ///         about. A repository-wide file sweep has to say what it means by "the repository", and
    ///         here that is the working tree and not the tooling beside it.
    ///     </para>
    /// </remarks>
    public static TheoryData<string, string> Suspect {
        get {
            var data = new TheoryData<string, string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in SourceFiles("*.cs")) {
                var line = 0;
                foreach (var text in File.ReadLines(path)) {
                    line++;

                    // A tag name in a doc comment is prose. It is also the one exclusion worth
                    // making by pattern: the second stage cannot tell a documented tag from a
                    // written one, and this file's own remarks would otherwise fail it.
                    var trimmed = text.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)
                        || trimmed.StartsWith('*')) {
                        continue;
                    }

                    foreach (Match match in TagLiteral.Matches(text)) {
                        var tag = match.Groups["tag"].Value;

                        if (!tag.Contains('-', StringComparison.Ordinal)
                            || tag.Equals(Lower(tag), StringComparison.Ordinal)) {
                            continue;
                        }

                        if (seen.Add(tag)) {
                            data.Add(tag, $"{Path.GetRelativePath(RepositoryRoot(), path)}:{line}");
                        }
                    }
                }
            }

            return data;
        }
    }

    /// <summary>
    ///     ⚠ <b>The one that catches the typo.</b> A hyphenated tag written with a capital, whose
    ///     lowercase spelling the stylesheets do style, is an element that has silently never been
    ///     styled.
    /// </summary>
    /// <param name="tag">The tag as written in C#.</param>
    /// <param name="site">Where it is written, for the failure message.</param>
    [Theory]
    [MemberData(nameof(Suspect))]
    public void A_hyphenated_tag_is_spelt_the_way_the_sheets_declare_it(string tag, string site) {
        var engine = Sheets();
        var lost = Missing(engine, tag, Lower(tag));

        Assert.True(
            lost.Count == 0,
            $"'{tag}' ({site}) is interned ordinally, so no rule reaches it — but the sheets style "
            + $"'{Lower(tag)}', which would have given it {string.Join(", ", lost)}. A hyphen cannot "
            + "appear in a component type name, so the capital carries no meaning here and the "
            + "lowercase spelling is the one the sheets declare."
        );
    }

    /// <summary>
    ///     The scan found the repository, and found something in it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without this, the theory above passes loudest on the day it stops running.</b> A
    ///     <c>MemberData</c> that yields nothing is a green theory, and every way this file can break
    ///     — a moved repository root, a regex that stopped matching, a walk that found no
    ///     <c>.vcss</c> — breaks it by yielding nothing. So the corpus is asserted to be a corpus:
    ///     sheets were found, sources were found, and the sheets parsed into rules.
    /// </remarks>
    [Fact]
    public void The_scan_actually_ran() {
        Assert.True(SourceFiles("*.vcss").Count >= 10, "no stylesheets were found to check against.");
        Assert.True(SourceFiles("*.cs").Count >= 100, "no C# sources were found to scan.");
        Assert.True(Sheets().Rules.Count > 100, "the stylesheets loaded into almost no rules.");

        // And the mechanism the theory depends on works: a tag the sheets style, mis-cased, loses
        // something. `alert` is `ControlTheme.vcss`'s, and has been since it was written.
        Assert.NotEmpty(Missing(Sheets(), "Alert-body", "alert-body"));
        Assert.Empty(Missing(Sheets(), "alert-body", "alert-body"));
    }

    /// <summary>
    ///     The two scans the mirror direction rests on found the repository, and can tell its three
    ///     kinds of name apart.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The same argument as <see cref="The_scan_actually_ran" />, and it needs making twice
    ///     because the two theories below fail by <i>finding nothing</i>.</b> An empty
    ///     <see cref="Declared" /> is a green pair of theories, and so is a <see cref="Written" />
    ///     whose tag table swallowed every name — a regex that stopped matching would make every
    ///     candidate look like a tag and nothing would ever be reported again. So the three
    ///     classifications are asserted against names that are what they are for reasons older than
    ///     this file: <c>textbox</c> is a tag, <c>flame-hue-0</c> is written and is not, and
    ///     <c>compiled-scene-facts</c> is a type selector nothing writes at all — one of each of the
    ///     three buckets, which is the only way to know the sort is still sorting.
    /// </remarks>
    [Fact]
    public void The_mirror_scan_actually_ran() {
        var written = Written;

        Assert.True(Declared.Count >= 100, "almost no type selectors were read out of the sheets.");
        Assert.True(written.Tags.Count >= 100, "almost no tag names were found in the sources.");
        Assert.True(written.Literals.Count >= 100, "almost no string literals were found in the sources.");

        // A tag, written as one, and reachable under its de-hyphenated spelling.
        Assert.True(written.Tags.ContainsKey("textbox"), "'textbox' was not recognised as a tag.");
        Assert.Equal("textbox", written.Hyphenless["textbox"]);

        // Written, and never as a tag — the shape the first theory reports.
        Assert.True(written.Literals.ContainsKey("flame-hue-0"), "'flame-hue-0' was not found in any source.");
        Assert.False(written.Tags.ContainsKey("flame-hue-0"), "'flame-hue-0' was mistaken for a tag.");

        // Declared and never written at all — the shape both theories have to let through.
        Assert.Contains(Declared, candidate => candidate.Name == "compiled-scene-facts");
        Assert.False(
            written.Literals.ContainsKey("model-facts"),
            "'model-facts' turned up in a source, so it is no longer the never-written example."
        );
    }

    /// <summary>Every bare type selector the repository's stylesheets declare.</summary>
    /// <remarks>
    ///     ⚠ <b>Every one of them, rather than only the ones that look wrong</b> — the filtering is in
    ///     the assertions, so the two theories below are hundreds of cases on a healthy tree instead
    ///     of nothing at all. A <c>MemberData</c> that narrows to the suspects is a theory that goes
    ///     quiet in exactly the two ways it must not: when it is right, and when it has broken.
    ///     <para>
    ///         One engine per sheet, unlike <see cref="Sheets" />, because a name has to be reported
    ///         with the file it was read from and one engine loses that. Nested selector lists are
    ///         walked, so a type inside <c>:is()</c> or <c>:not()</c> counts — those are the ones a
    ///         text search over the sheets is worst at.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<(string Name, string Sheet)> Declared => declared ??= ReadDeclared();

    static IReadOnlyList<(string Name, string Sheet)>? declared;

    /// <summary>The candidates, as xUnit rows.</summary>
    public static TheoryData<string, string> Selectors {
        get {
            var data = new TheoryData<string, string>();

            foreach (var (name, sheet) in Declared) {
                data.Add(name, sheet);
            }

            return data;
        }
    }

    /// <summary>
    ///     ⚠ <b>A type selector whose name the sources write, but never as a tag.</b> A class selector
    ///     and a type selector are not two spellings of one thing, and the confusion is silent both
    ///     ways round: the rule matches nothing and the class it was meant to be goes unstyled.
    /// </summary>
    /// <param name="name">The declared type selector.</param>
    /// <param name="sheet">The sheet that declares it.</param>
    /// <remarks>
    ///     ⚠ <b>"Never as a tag" is measured over-inclusively and "written" narrowly, which is the
    ///     safe way round.</b> Anything quoted on a line that also calls <c>Add</c>, <c>Create</c>,
    ///     <c>Part</c>, <c>Element</c> or <c>Prepend</c> counts as a tag, so a name built in a ternary
    ///     — <c>Add(live ? "agent-row-live" : "agent-row")</c>, which is real — is seen for what it is;
    ///     a scan that missed it would accuse a rule that works. The cost is that a name a tag call
    ///     merely mentions is exempt, and that is a false negative, which is the direction a gate
    ///     should err in.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Selectors))]
    public void A_type_selector_names_a_tag_rather_than_a_class(string name, string sheet) {
        var written = Written;
        var site = written.Tags.ContainsKey(name) ? null : written.Literals.GetValueOrDefault(name);

        Assert.True(
            site is null,
            $"'{name} {{ … }}' in {sheet} is a type selector, but nothing in the repository ever "
            + $"creates an element with that tag — the name is written at {site}, which is not an "
            + "element-creation call. A type selector and a class selector are not interchangeable: "
            + $"if the intent was a class, the rule wants a dot ('.{name}'); if it was a tag, the call "
            + "site wants to be creating one. Either way, as it stands the rule fires on nothing."
        );
    }

    /// <summary>
    ///     ⚠ <b>A type selector that is a tag the repository writes, once the hyphens come out.</b>
    ///     <c>text-box</c> against <c>textbox</c>: two spellings of one name, and the cascade has no
    ///     opinion about which was meant because <c>NameTable</c> interns ordinally.
    /// </summary>
    /// <param name="name">The declared type selector.</param>
    /// <param name="sheet">The sheet that declares it.</param>
    /// <remarks>
    ///     ⚠ <b>Hyphenation only, and the narrowness is the whole design.</b> Edit distance over the
    ///     tag vocabulary was the obvious generalisation and it does not survive contact with it —
    ///     one substitution away from <c>asset-tiles</c> is <c>asset-tile</c>, and those are a grid
    ///     and a thing in it. What makes hyphens different is that they are not a spelling choice
    ///     here but the word separator, which is why
    ///     <see cref="No_two_tags_differ_only_by_hyphenation" /> can be asserted at all — and it is
    ///     asserted, because this rule is worth nothing the day it stops being true.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Selectors))]
    public void A_type_selector_is_hyphenated_the_way_the_tag_is(string name, string sheet) {
        var written = Written;
        var tag = written.Tags.ContainsKey(name) ? null : written.Hyphenless.GetValueOrDefault(Hyphenless(name));

        Assert.True(
            tag is null || tag.Equals(name, StringComparison.Ordinal),
            $"'{name} {{ … }}' in {sheet} names no tag, and '{tag}' — the same name with the hyphens "
            + "somewhere else — is one the repository writes. Tags are interned ordinally, so the two "
            + "are unrelated strings and the rule reaches nothing."
        );
    }

    /// <summary>
    ///     The premise <see cref="A_type_selector_is_hyphenated_the_way_the_tag_is" /> rests on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A rule that cannot tell a typo from a decision is worse than no rule</b>, and the only
    ///     thing standing between the theory above and that is this: across every name the repository
    ///     writes as a tag, no two of them are the same letters with the hyphens in different places.
    ///     While that holds, a de-hyphenated collision can only be one name written twice. The day it
    ///     stops holding — somebody genuinely wants a <c>dropzone</c> and a <c>drop-zone</c> — this
    ///     fails first and says so, rather than the theory failing on whichever of the two is
    ///     alphabetically unluckier.
    /// </remarks>
    [Fact]
    public void No_two_tags_differ_only_by_hyphenation() {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var tag in Written.Tags.Keys) {
            if (!Lowercase(tag)) {
                continue;
            }

            (groups.TryGetValue(Hyphenless(tag), out var names) ? names : groups[Hyphenless(tag)] = [])
                .Add(tag);
        }

        var collisions = groups.Values
            .Where(names => names.Count > 1)
            .Select(names => string.Join(" / ", names.Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            collisions.Count == 0,
            "two names are written as tags that differ only by hyphenation — "
            + $"{string.Join("; ", collisions)}. A hyphenation collision is no longer evidence of a "
            + "typo, so A_type_selector_is_hyphenated_the_way_the_tag_is has lost its discriminator "
            + "and should be deleted rather than given an exception list."
        );
    }

    /// <summary>What the sources write, sorted into the two kinds this file can tell apart.</summary>
    /// <param name="Tags">Names used at an element-creation site, to the first such site.</param>
    /// <param name="Literals">Every quoted name and markup class, to the first place it appears.</param>
    /// <param name="Hyphenless">Each lowercase tag, keyed by itself with the hyphens removed.</param>
    public sealed record Sources(
        IReadOnlyDictionary<string, string> Tags,
        IReadOnlyDictionary<string, string> Literals,
        IReadOnlyDictionary<string, string> Hyphenless
    );

    /// <summary>The scan, done once.</summary>
    public static Sources Written => sources ??= ReadSources();

    static Sources? sources;

    /// <summary>A call that makes an element, whose arguments are therefore tag-shaped.</summary>
    /// <remarks>
    ///     Matched against the whole line rather than against the argument, so a tag chosen by an
    ///     expression is still recognised. See the remarks on
    ///     <see cref="A_type_selector_names_a_tag_rather_than_a_class" />.
    /// </remarks>
    [GeneratedRegex(@"\b(?:Add|Create|Part|Element|Prepend)\s*(?:<[^<>()]{0,120}>)?\s*\(|\bTagName\b")]
    private static partial Regex TagSite { get; }

    /// <summary>A quoted name.</summary>
    [GeneratedRegex("\"(?<name>[A-Za-z][A-Za-z0-9-]*)\"")]
    private static partial Regex Quoted { get; }

    /// <summary>A markup element, which names an intrinsic tag when it is lowercase.</summary>
    [GeneratedRegex(@"<\s*(?<name>[a-z][A-Za-z0-9-]*)")]
    private static partial Regex MarkupElement { get; }

    /// <summary>Markup's <c>@tag</c> directive, which renames a component's host element.</summary>
    [GeneratedRegex(@"^\s*@tag\s+(?<name>[A-Za-z][A-Za-z0-9-]*)")]
    private static partial Regex MarkupTag { get; }

    /// <summary>A markup <c>class</c> attribute, whose contents are classes and never tags.</summary>
    [GeneratedRegex(@"\bclass\s*=\s*""(?<names>[^""]*)""")]
    private static partial Regex MarkupClasses { get; }

    static Sources ReadSources() {
        var root = RepositoryRoot();
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);

        void Note(Dictionary<string, string> into, string name, string path, int line) =>
            into.TryAdd(name, $"{Path.GetRelativePath(root, path)}:{line}");

        foreach (var path in SourceFiles("*.cs")) {
            // This file writes every name it talks about, so scanning it would make each of them
            // look written — and `flame-hue-0` is one of them, in the remarks above.
            if (Path.GetFileName(path).Equals("TypeSelectorReachTests.cs", StringComparison.Ordinal)) {
                continue;
            }

            var line = 0;

            foreach (var text in File.ReadLines(path)) {
                line++;

                var trimmed = text.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) {
                    continue;
                }

                var creates = TagSite.IsMatch(text);

                foreach (Match match in Quoted.Matches(text)) {
                    var name = match.Groups["name"].Value;

                    Note(literals, name, path, line);

                    if (creates) {
                        Note(tags, name, path, line);
                    }
                }
            }
        }

        foreach (var path in SourceFiles("*.vxml")) {
            var line = 0;

            foreach (var text in File.ReadLines(path)) {
                line++;

                foreach (Match match in MarkupElement.Matches(text)) {
                    Note(tags, match.Groups["name"].Value, path, line);
                    Note(literals, match.Groups["name"].Value, path, line);
                }

                if (MarkupTag.Match(text) is { Success: true } directive) {
                    Note(tags, directive.Groups["name"].Value, path, line);
                    Note(literals, directive.Groups["name"].Value, path, line);
                }

                foreach (Match match in MarkupClasses.Matches(text)) {
                    foreach (var name in match.Groups["names"].Value.Split(
                                 ' ',
                                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                             )) {
                        Note(literals, name, path, line);
                    }
                }
            }
        }

        var hyphenless = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var tag in tags.Keys.Order(StringComparer.Ordinal)) {
            if (Lowercase(tag)) {
                hyphenless.TryAdd(Hyphenless(tag), tag);
            }
        }

        return new Sources(tags, literals, hyphenless);
    }

    static List<(string Name, string Sheet)> ReadDeclared() {
        var root = RepositoryRoot();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(string, string)>();

        foreach (var path in SourceFiles("*.vcss")) {
            var engine = new StyleEngine();
            engine.Load(File.ReadAllText(path), StyleOrigin.Author);

            var sheet = Path.GetRelativePath(root, path);

            for (var rule = 0; rule < engine.Rules.Count; rule++) {
                foreach (var name in TypesIn(engine, engine.Rules[rule].Selector)) {
                    if (seen.Add(name)) {
                        list.Add((name, sheet));
                    }
                }
            }
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    static IEnumerable<string> TypesIn(StyleEngine engine, Selector selector) {
        var table = engine.Selectors;

        for (var index = 0; index < selector.Count; index++) {
            var compound = table.Compound(selector.Start + index);

            for (var part = 0; part < compound.Count; part++) {
                var simple = table.Simple(compound.Start + part);

                if (simple.Kind is SimpleSelectorKind.Type) {
                    yield return engine.Names.NameOf(simple.NameId);
                    continue;
                }

                if (simple.Kind is not (SimpleSelectorKind.Not or SimpleSelectorKind.Is)) {
                    continue;
                }

                for (var nested = 0; nested < simple.NestedCount; nested++) {
                    foreach (var name in TypesIn(engine, table.Nested(simple.NestedStart + nested))) {
                        yield return name;
                    }
                }
            }
        }
    }

    static string Hyphenless(string name) => name.Replace("-", "", StringComparison.Ordinal);

    static bool Lowercase(string name) => name.Equals(Lower(name), StringComparison.Ordinal);

    /// <summary>What the sheets would have given <paramref name="declared" /> and do not give
    /// <paramref name="written" />.</summary>
    /// <param name="engine">The loaded sheets.</param>
    /// <param name="written">The tag as the source writes it.</param>
    /// <param name="declared">The spelling to compare against.</param>
    /// <returns>The property names only the second spelling resolves.</returns>
    /// <remarks>
    ///     ⚠ <b>Resolved rather than looked up</b>, because the question is what the cascade computes
    ///     and not what a selector list contains. A tag can be named by a rule that never applies —
    ///     sealed in a <c>@media</c>, or beaten outright — and reporting that as a lost style would
    ///     be a failure with nothing behind it. Two elements, same parent, same absence of classes:
    ///     the only thing that differs is the spelling, so anything the second one has is exactly
    ///     what the spelling cost.
    /// </remarks>
    static List<string> Missing(StyleEngine engine, string written, string declared) {
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

    /// <summary>Every stylesheet in the repository, in one engine.</summary>
    /// <remarks>
    ///     ⚠ <b>One engine for sheets no single document loads together</b>, and that is the
    ///     conservative direction rather than the sloppy one. A tag some *other* assembly's sheet
    ///     happens to style can only make a candidate look reachable and pass, never make a clean one
    ///     fail — so the answer this gives is a floor on the defect, which is the right way round for
    ///     a gate.
    /// </remarks>
    static StyleEngine Sheets() {
        var engine = new StyleEngine();

        foreach (var path in SourceFiles("*.vcss")) {
            engine.Load(File.ReadAllText(path), StyleOrigin.Author);
        }

        return engine;
    }

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

    static List<string> SourceFiles(string pattern) {
        List<string> found = [];
        Walk(RepositoryRoot(), pattern, found);
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

    static string Lower(string name) => name.ToLowerInvariant();

    static string RepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
