// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using CsCheck;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     The gate [doc 14](../../docs/plan/14-roadmap.md) names for 4b: the bucketed matcher against a
///     brute-force one, over randomised trees.
/// </summary>
/// <remarks>
///     <para>
///         Two things make matching fast and neither is allowed to change an answer. Rule bucketing
///         hands an element only the rules whose rightmost compound names something the element has,
///         and the ancestor bloom rejects a descendant combinator without climbing the tree. Both
///         are filters in front of one shared matcher, so the property to hold is exact: for every
///         element, the set of rules the fast path finds is the set the slow path finds.
///     </para>
///     <para>
///         A filter that is too aggressive loses rules and the UI is quietly unstyled; a bloom that
///         is too permissive costs a tree walk and nothing else. Only one of those is a bug, and it
///         is the one this catches.
///     </para>
/// </remarks>
public class SelectorOracleTests {
    [Fact]
    public void The_bucketed_matcher_finds_exactly_what_brute_force_finds() {
        Gen.Select(Gen.Int[0, 100_000], Gen.Int[2, 5], Gen.Int[1, 3]).Sample(shape => {
                var (seed, depth, breadth) = shape;
                var fixture = new StyleFixture();
                var elements = BuildTree(fixture, seed, depth, breadth);
                fixture.Load(BuildStylesheet(seed));

                foreach (var element in elements) {
                    var indexed = fixture.MatchIndexed(element);
                    var brute = fixture.MatchBruteForce(element);

                    Assert.Equal(brute, indexed);
                }
            }, iter: 400
        );
    }

    [Fact]
    public void The_bloom_filter_never_rejects_an_ancestor_that_is_there() {
        // The same property stated where it bites hardest: deep trees, and selectors written so that
        // the ancestor part is usually present. A bloom with a bad hash passes the test above by
        // rejecting nothing; this one makes it work.
        Gen.Int[0, 100_000].Sample(seed => {
                var fixture = new StyleFixture();
                var chain = new List<StyleNodeId>();
                var parent = StyleNodeId.Invalid;

                for (var i = 0; i < 24; i++) {
                    parent = fixture.Tree.CreateElement("div", parent, classNames: [$"level{i}"]);
                    chain.Add(parent);
                }

                var builder = new StringBuilder();
                for (var i = 0; i < 24; i++) {
                    builder.Append(CultureInfo.InvariantCulture, $".level{i} .level23 {{ color: red }}\n");
                }

                fixture.Load(builder.ToString());

                var leaf = chain[^1];

                Assert.Equal(fixture.MatchBruteForce(leaf), fixture.MatchIndexed(leaf));

                // Twenty-three of the twenty-four rules match — every ancestor level is above the
                // leaf except its own. A bloom that lost one would show up here.
                Assert.Equal(23, fixture.MatchIndexed(leaf).Count);
                Assert.True(seed >= 0);
            }, iter: 20
        );
    }

    [Fact]
    public void The_index_actually_narrows_the_candidates() {
        // The oracle proves the index is *correct*. It says nothing about whether it is *useful*,
        // and an index that returned every rule would pass it. This is the other half.
        var fixture = new StyleFixture();
        var builder = new StringBuilder();
        for (var i = 0; i < 500; i++) {
            builder.Append(CultureInfo.InvariantCulture, $".class{i} {{ color: red }}\n");
            builder.Append(CultureInfo.InvariantCulture, $"tag{i} {{ color: red }}\n");
            builder.Append(CultureInfo.InvariantCulture, $"#id{i} {{ color: red }}\n");
        }

        fixture.Load(builder.ToString());

        var element = fixture.Tree.CreateElement("tag7", id: "id7", classNames: ["class7"]);
        var candidates = new List<int>();
        fixture.Index.Collect(fixture.Tree, element, candidates);

        Assert.Equal(1500, fixture.Index.Count);
        Assert.Equal(0, fixture.Index.UniversalCount);
        Assert.Equal(3, candidates.Count);
        Assert.Equal(3, fixture.MatchIndexed(element).Count);
    }

    [Fact]
    public void Candidates_come_back_in_document_order() {
        // The cascade's last tie-break is which rule came later in the source. Merging the buckets
        // rather than concatenating them is what preserves it, and nothing downstream re-sorts.
        var fixture = new StyleFixture();
        fixture.Load("""
            .a { color: red }
            div { color: green }
            #x { color: blue }
            .a { color: black }
            """);

        var element = fixture.Tree.CreateElement("div", id: "x", classNames: ["a"]);
        var candidates = new List<int>();
        fixture.Index.Collect(fixture.Tree, element, candidates);

        Assert.Equal([0, 1, 2, 3], candidates);
    }

    static List<StyleNodeId> BuildTree(StyleFixture fixture, int seed, int depth, int breadth) {
        var tags = new[] { "div", "span", "ul", "li", "Button", "Panel" };
        var classNames = new[] { "row", "cell", "selected", "dark", "sidebar", "item" };
        var elements = new List<StyleNodeId>();
        var random = new Random(seed);

        Build(StyleNodeId.Invalid, 0);
        return elements;

        void Build(StyleNodeId parent, int level) {
            if (level == depth) {
                return;
            }

            for (var i = 0; i < breadth; i++) {
                var tag = tags[random.Next(tags.Length)];
                var id = random.Next(4) == 0 ? $"id{random.Next(3)}" : null;

                var chosen = new List<string>();
                for (var c = 0; c < classNames.Length; c++) {
                    if (random.Next(3) == 0) {
                        chosen.Add(classNames[c]);
                    }
                }

                var element = fixture.Tree.CreateElement(tag, parent, id, [.. chosen]);
                if (random.Next(3) == 0) {
                    fixture.Tree.SetState(element, (ElementState) (1u << random.Next(6)));
                }

                if (random.Next(3) == 0) {
                    fixture.Tree.SetAttribute(element, "data-kind", $"k{random.Next(3)}");
                }

                // So that `:empty` varies on both of the things it reads. Depth alone would vary the
                // child count and leave every leaf textless, and a rule ending in `:empty` would
                // then be a rule ending in "is a leaf" — which the index and the bloom happen to
                // treat identically, so the oracle would agree for the wrong reason.
                if (random.Next(3) == 0) {
                    fixture.Tree.SetHasText(element, true);
                }

                elements.Add(element);
                Build(element, level + 1);
            }
        }
    }

    static string BuildStylesheet(int seed) {
        var random = new Random(seed ^ 0x5F5F);
        var pieces = new[] {
            "div", "span", "ul", "li", "Button", "Panel", "*",
            ".row", ".cell", ".selected", ".dark", ".sidebar", ".item",
            "#id0", "#id1", "#id2",
            "[data-kind]", "[data-kind=k1]", "[data-kind^=k]",
            ":hover", ":focus", ":disabled", ":first-child", ":last-child", ":nth-child(2n+1)",
            ":empty", ":not(.selected)", ":is(.row, .cell)"
        };

        var combinators = new[] { " ", " > ", " + ", " ~ " };
        var builder = new StringBuilder();

        for (var rule = 0; rule < 40; rule++) {
            var compounds = 1 + random.Next(3);
            for (var c = 0; c < compounds; c++) {
                if (c > 0) {
                    builder.Append(combinators[random.Next(combinators.Length)]);
                }

                var parts = 1 + random.Next(2);
                for (var p = 0; p < parts; p++) {
                    var piece = pieces[random.Next(pieces.Length)];

                    // `*` and a type name are only legal at the front of a compound.
                    if (p > 0 && (piece == "*" || char.IsLetter(piece[0]))) {
                        piece = ".item";
                    }

                    builder.Append(piece);
                }
            }

            builder.Append(" { color: red }\n");
        }

        return builder.ToString();
    }
}
