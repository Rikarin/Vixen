// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     Every member of the four enums the selector language is made of, driven against one document.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is a census before it is a matching test</b>, and the census is the part that
///         cannot be written any other way. ⚠ Adding a member to
///         <see cref="SimpleSelectorKind" />, <see cref="AttributeOperator" />,
///         <see cref="PositionTest" /> or <see cref="Combinator" /> and forgetting the arm that
///         answers it <b>compiles</b>, and a selector using it then matches nothing, silently and
///         for ever — no exception, no diagnostic, a rule that simply never fires. That is the same
///         shape as the tag-name drift <c>TypeSelectorReachTests</c> exists for, one layer down in
///         the language rather than in the sheets.
///     </para>
///     <para>
///         ⚠ <b>So the assertions are the price of a row, not the point of one.</b>
///         <c>SelectorMatchingTests</c> already says what each construct <em>means</em>, in prose
///         and by hand, and says it better than a table can. What it cannot say is that the list is
///         complete: it names the constructs it happens to name, and nothing counts them.
///         <see cref="Every_member_of_the_selector_enums_is_swept" /> reads the enums back and fails
///         naming any member with no row, so the sweep cannot fall behind the language.
///     </para>
///     <para>
///         <b>A row cannot lie about which member it exercises.</b> Each row's selector is compiled
///         and walked — compounds, simples and the selectors nested inside <c>:not()</c>,
///         <c>:is()</c> and <c>:has()</c> — and the member it claims has to actually appear in it.
///         Without that, <c>":first-child"</c> filed under <see cref="PositionTest.NthLastOfType" />
///         would satisfy the census while testing something else entirely, which is the failure mode
///         of every table-shaped test.
///         ⚠ <see cref="SimpleSelector.Operator" /> and <see cref="SimpleSelector.Position" /> are
///         only read where the kind is <see cref="SimpleSelectorKind.Attribute" /> or
///         <see cref="SimpleSelectorKind.Position" />: both are non-nullable with a real default, so
///         every simple selector in the tree otherwise "contains"
///         <see cref="AttributeOperator.Present" /> and <see cref="PositionTest.First" /> and those
///         two rows would pass on anything at all.
///     </para>
///     <para>
///         <b>And each row asserts both halves</b> — an element it matches and an element it does
///         not — because a predicate that cannot be false is worse than no predicate. The one
///         exception is <see cref="SimpleSelectorKind.Universal" />, which has no negative by
///         definition; a row without one has to say why, and <see cref="Rows" /> is checked for that
///         too.
///     </para>
/// </remarks>
public class SelectorSurfaceSweepTests {
    /// <summary>One member of one enum, and the selector that exercises it.</summary>
    /// <param name="Member">The enum member this row is the census entry for.</param>
    /// <param name="Selector">A selector that produces it.</param>
    /// <param name="Matches">The element in <see cref="Scene" /> the selector must match.</param>
    /// <param name="DoesNotMatch">An element it must not match, or null with a reason.</param>
    /// <param name="WhyNoNegative">Why no element can fail this selector.</param>
    public sealed record Row(
        Enum Member,
        string Selector,
        string Matches,
        string? DoesNotMatch,
        string? WhyNoNegative = null
    ) {
        /// <summary>How the row is named in the test list and in the census failure.</summary>
        public string Key => $"{Member.GetType().Name}.{Member}";
    }

    /// <summary>The rows, one per member of each of the four enums.</summary>
    public static readonly Row[] Rows = [
        // Combinator. `None` is the first compound of every selector, so its row is any selector at
        // all; the other four have to reach an element the compound before them did not.
        new(Combinator.None, "section", "root", "firstDiv"),
        new(Combinator.Descendant, "section .leaf", "deep", "strayLeaf"),
        new(Combinator.Child, "section > p", "pOne", "nestedP"),
        new(Combinator.NextSibling, ".selected + div", "thirdDiv", "secondDiv"),
        new(Combinator.SubsequentSibling, "p ~ div", "thirdDiv", "firstDiv"),

        // SimpleSelectorKind.
        new(
            SimpleSelectorKind.Universal,
            "*",
            "root",
            null,
            "`*` matches every element there is, so no element can be the negative half."
        ),
        new(SimpleSelectorKind.Type, "p", "pOne", "firstDiv"),
        new(SimpleSelectorKind.Id, "#app", "root", "firstDiv"),
        new(SimpleSelectorKind.Class, ".selected", "pTwo", "pOne"),
        new(SimpleSelectorKind.Attribute, "[data-role]", "root", "firstDiv"),
        new(SimpleSelectorKind.State, ":hover", "button", "root"),
        new(SimpleSelectorKind.Position, ":first-child", "firstDiv", "pOne"),
        new(SimpleSelectorKind.Not, ":not(.selected)", "pOne", "pTwo"),

        // ⚠ `:where()` compiles to `Is` as well — there is no `Where` kind, deliberately, and
        // `WhereSelectorTests` is where the difference (a specificity, not a match) is pinned.
        new(SimpleSelectorKind.Is, ":is(p, span)", "pOne", "firstDiv"),
        new(SimpleSelectorKind.Empty, ":empty", "deep", "root"),

        // ⚠ The positive is a *descendant* of the element that declares the language, which is the
        // whole reason this is a kind rather than a spelling of `[lang|=en]`.
        new(SimpleSelectorKind.Lang, ":lang(en)", "deep", "strayLeaf"),

        // ⚠ `:has(> .leaf)` is refused on the raw text — ExCSS drops the leading combinator — so the
        // row is the descendant form. `HasInvalidationTests` carries that refusal.
        new(SimpleSelectorKind.Has, ":has(.leaf)", "firstDiv", "secondDiv"),

        // AttributeOperator. Every negative is `secondDiv`, which carries the same two attributes
        // with different values — so a failure is the comparison being wrong rather than the
        // attribute being absent.
        new(AttributeOperator.Present, "[data-role]", "root", "secondDiv"),
        new(AttributeOperator.Equals, "[lang=en-GB]", "root", "secondDiv"),
        new(AttributeOperator.Includes, "[rel~=prev]", "root", "secondDiv"),
        new(AttributeOperator.DashMatch, "[lang|=en]", "root", "secondDiv"),
        new(AttributeOperator.Prefix, "[lang^=en]", "root", "secondDiv"),
        new(AttributeOperator.Suffix, "[lang$=GB]", "root", "secondDiv"),
        new(AttributeOperator.Substring, "[lang*=n-G]", "root", "secondDiv"),

        // PositionTest. ⚠ The of-type rows are answered by a document whose children are
        // `div p div p div button`, so every of-type index differs from the child index it would be
        // confused with — in a run of one tag the two families agree on every element.
        new(PositionTest.First, ":first-child", "firstDiv", "pOne"),
        new(PositionTest.Last, ":last-child", "button", "thirdDiv"),
        new(PositionTest.Only, ":only-child", "deep", "pOne"),
        new(PositionTest.Nth, ":nth-child(3)", "secondDiv", "pTwo"),
        new(PositionTest.NthLast, ":nth-last-child(2)", "thirdDiv", "button"),
        new(PositionTest.FirstOfType, ":first-of-type", "pOne", "pTwo"),
        new(PositionTest.LastOfType, ":last-of-type", "pTwo", "pOne"),
        new(PositionTest.OnlyOfType, ":only-of-type", "button", "pOne"),
        new(PositionTest.NthOfType, ":nth-of-type(2)", "pTwo", "pOne"),
        new(PositionTest.NthLastOfType, ":nth-last-of-type(1)", "pTwo", "pOne")
    ];

    /// <summary>The rows, by key, for the theory.</summary>
    public static TheoryData<string> Keys {
        get {
            var keys = new TheoryData<string>();

            foreach (var row in Rows) {
                keys.Add(row.Key);
            }

            return keys;
        }
    }

    /// <summary>Each row matches the element it names, fails the one it names, and is what it says.</summary>
    /// <param name="key">Which row.</param>
    [Theory]
    [MemberData(nameof(Keys))]
    public void A_row_exercises_the_member_it_is_filed_under(string key) {
        var row = Rows.Single(candidate => candidate.Key == key);
        var scene = new Scene();

        var compiled = scene.Fixture.Compile(row.Selector);
        var produced = Produced(scene.Fixture.Table, compiled);

        Assert.True(
            produced.Contains(row.Member),
            $"'{row.Selector}' is filed under {key}, and compiling it produces "
            + $"[{string.Join(", ", produced.Select(member => $"{member.GetType().Name}.{member}"))}] — "
            + "which does not include it. A row that does not produce its own member satisfies the "
            + "census while testing something else."
        );

        Assert.True(
            scene.Fixture.Matcher.Matches(scene.Fixture.Tree, scene[row.Matches], compiled),
            $"'{row.Selector}' ({key}) did not match '{row.Matches}'."
        );

        if (row.DoesNotMatch is null) {
            Assert.False(
                string.IsNullOrWhiteSpace(row.WhyNoNegative),
                $"{key} has no negative element and no reason. A selector nothing can fail is a "
                + "predicate that cannot be false, and this row would pass against a matcher that "
                + "returned true unconditionally."
            );

            return;
        }

        Assert.False(
            scene.Fixture.Matcher.Matches(scene.Fixture.Tree, scene[row.DoesNotMatch], compiled),
            $"'{row.Selector}' ({key}) matched '{row.DoesNotMatch}', which it must not."
        );
    }

    /// <summary>⚠ Every member of the four enums has a row, so the sweep cannot fall behind them.</summary>
    /// <remarks>
    ///     This is the assertion the file exists for. A new <see cref="SimpleSelectorKind" /> with no
    ///     arm in <c>SelectorMatcher</c> is a rule that never fires and nothing else in the tree
    ///     notices; a new member with no row here fails by name, on the commit that adds it.
    /// </remarks>
    [Fact]
    public void Every_member_of_the_selector_enums_is_swept() {
        var swept = Rows.Select(row => row.Member).ToHashSet();

        var missing = new List<string>();

        foreach (var kind in (Type[])[
                     typeof(Combinator), typeof(SimpleSelectorKind), typeof(AttributeOperator), typeof(PositionTest)
                 ]) {
            missing.AddRange(
                Enum.GetValues(kind)
                    .Cast<Enum>()
                    .Where(member => !swept.Contains(member))
                    .Select(member => $"{kind.Name}.{member}")
            );
        }

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} selector-language member(s) have no row in this sweep: "
            + $"{string.Join(", ", missing)}. A member nothing exercises is a selector that compiles "
            + "and matches nothing — the arm that answers it can be missing from SelectorMatcher "
            + "without failing a build. Add a row rather than deleting this assertion."
        );
    }

    /// <summary>Which enum members compiling a selector actually produced.</summary>
    /// <param name="table">Where the compiler put them.</param>
    /// <param name="selector">The compiled selector.</param>
    /// <returns>Every combinator, kind, operator and position test in it, nested ones included.</returns>
    static HashSet<Enum> Produced(SelectorTable table, Selector selector) {
        var found = new HashSet<Enum>();
        Walk(table, selector, found);

        return found;

        static void Walk(SelectorTable table, Selector selector, HashSet<Enum> found) {
            for (var index = selector.Start; index < selector.Start + selector.Count; index++) {
                var compound = table.Compound(index);
                found.Add(compound.Combinator);

                for (var simpleIndex = compound.Start; simpleIndex < compound.Start + compound.Count; simpleIndex++) {
                    var simple = table.Simple(simpleIndex);
                    found.Add(simple.Kind);

                    // Only where the kind says the field means something: both have a real default,
                    // so reading them unconditionally would find Present and First everywhere.
                    if (simple.Kind == SimpleSelectorKind.Attribute) {
                        found.Add(simple.Operator);
                    }

                    if (simple.Kind == SimpleSelectorKind.Position) {
                        found.Add(simple.Position);
                    }

                    for (var nested = simple.NestedStart; nested < simple.NestedStart + simple.NestedCount; nested++) {
                        Walk(table, table.Nested(nested), found);
                    }
                }
            }
        }
    }

    /// <summary>The one document every row is answered against.</summary>
    /// <remarks>
    ///     ⚠ The children of <c>root</c> are <c>div p div p div button</c> rather than a run of one
    ///     tag, because <c>:nth-of-type(n)</c> and <c>:nth-child(n)</c> select the same element for
    ///     every <c>n</c> when every sibling shares a tag — which is most fixtures and no real panel,
    ///     and would let an of-type test compiled into a child test pass the whole table.
    /// </remarks>
    sealed class Scene {
        readonly Dictionary<string, StyleNodeId> elements = [];

        public Scene() {
            Fixture = new StyleFixture();
            var tree = Fixture.Tree;

            var root = tree.CreateElement("section", id: "app", classNames: ["panel", "dark"]);
            tree.SetAttribute(root, "data-role", "container");
            tree.SetAttribute(root, "lang", "en-GB");
            tree.SetAttribute(root, "rel", "next prev up");

            var firstDiv = tree.CreateElement("div", root, classNames: ["leading"]);
            var pOne = tree.CreateElement("p", root);
            var secondDiv = tree.CreateElement("div", root);
            var pTwo = tree.CreateElement("p", root, classNames: ["selected"]);
            var thirdDiv = tree.CreateElement("div", root, classNames: ["trailing"]);
            var button = tree.CreateElement("button", root);

            tree.SetState(button, ElementState.Hover);

            // The same two attributes as root and different values, so an operator row's negative
            // fails on the comparison rather than on the attribute being absent.
            tree.SetAttribute(secondDiv, "lang", "fr-FR");
            tree.SetAttribute(secondDiv, "rel", "up down");

            // firstDiv's only child, and childless and textless itself: :only-child and :empty.
            var deep = tree.CreateElement("span", firstDiv, classNames: ["leaf"]);

            // A `p` that is not a child of the section, so `section > p` has something to refuse.
            var nestedP = tree.CreateElement("p", secondDiv);

            // Rooted nowhere: no ancestor declares a language and no ancestor is a section.
            var strayLeaf = tree.CreateElement("span", classNames: ["leaf"]);

            elements["root"] = root;
            elements["firstDiv"] = firstDiv;
            elements["pOne"] = pOne;
            elements["secondDiv"] = secondDiv;
            elements["pTwo"] = pTwo;
            elements["thirdDiv"] = thirdDiv;
            elements["button"] = button;
            elements["deep"] = deep;
            elements["nestedP"] = nestedP;
            elements["strayLeaf"] = strayLeaf;
        }

        public StyleFixture Fixture { get; }

        public StyleNodeId this[string name] => elements[name];
    }
}
