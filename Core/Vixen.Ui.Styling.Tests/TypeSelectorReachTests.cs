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

    static IReadOnlyList<string> SourceFiles(string pattern) {
        var separator = Path.DirectorySeparatorChar;

        return [
            .. Directory.EnumerateFiles(RepositoryRoot(), pattern, SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                    && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                    && !path.Contains($"{separator}.claude{separator}", StringComparison.Ordinal)
                    && !path.Contains($"{separator}.git{separator}", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
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
