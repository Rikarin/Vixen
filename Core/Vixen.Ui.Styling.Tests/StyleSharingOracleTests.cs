// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using CsCheck;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     The gate for the cascade half of 4b: the sharing cache against a resolver that does not
///     share, over randomised trees and stylesheets.
/// </summary>
/// <remarks>
///     <para>
///         Sharing skips the whole cascade for an element on the grounds that another element with
///         the same key already ran it. That is a pure optimisation or it is a silently wrong UI,
///         and the only way to know which is to have something that does the work every time and
///         compare. Exactly the shape of the selector oracle, one layer up.
///     </para>
///     <para>
///         The comparison is by <i>reference</i>, which it can be because both paths intern. That
///         makes the assertion stronger than value equality would: it says the shared style is
///         literally the object the cascade would have produced.
///     </para>
/// </remarks>
public class StyleSharingOracleTests {
    [Fact]
    public void Sharing_produces_exactly_what_cascading_every_element_produces() {
        var hits = 0;

        Gen.Select(Gen.Int[0, 100_000], Gen.Int[2, 4], Gen.Int[1, 4]).Sample(shape => {
                var (seed, depth, breadth) = shape;

                var shared = new CascadeFixture();
                var oracle = new CascadeFixture();
                var css = BuildStylesheet(seed);

                shared.Load(css);
                oracle.Load(css);
                BuildTree(shared, seed, depth, breadth);
                BuildTree(oracle, seed, depth, breadth);

                var withSharing = shared.Engine.ResolveAll();
                var withoutSharing = ResolveAllWithoutSharing(oracle);

                Assert.Equal(withoutSharing.Length, withSharing.Length);

                // Without this the property is vacuous: one position-dependent rule anywhere in the
                // generated stylesheet turns sharing off, and then both sides are the same code
                // path agreeing with itself.
                Assert.True(shared.Engine.Rules.SharingIsSound);
                Interlocked.Add(ref hits, shared.Engine.Resolver.SharingHits);

                for (var i = 0; i < withSharing.Length; i++) {
                    // Two engines, so not the same object — but the same property table, and the
                    // sharing engine must not have invented one the cascade would not have.
                    Assert.Equal(Describe(shared, withSharing[i]), Describe(oracle, withoutSharing[i]));
                }
            }, iter: 300
        );

        // Counted across the sample rather than per case, because a shape with a breadth of one has
        // no siblings to share between and would fail a per-case guard for being an honest tree.
        Assert.True(hits > 0, "the sharing cache never fired, so the property above proved nothing");
    }

    [Fact]
    public void Sharing_is_refused_when_a_rule_could_tell_two_identical_siblings_apart() {
        // The soundness condition, stated where it bites. Every element here has the same tag, the
        // same class and the same parent — the sharing key cannot tell them apart, and
        // `:nth-child(2n)` can.
        foreach (var css in new[] {
            "li:nth-child(2n) { color: even }",
            ".row + .row { color: later }",
            "li:first-child { color: first }",
            "li:is(:last-child) { color: last }",
            "[data-kind=danger] { color: danger }"
        }) {
            var fixture = new CascadeFixture();
            fixture.Load(css);

            Assert.False(fixture.Engine.Rules.SharingIsSound, css);
        }

        var ordinary = new CascadeFixture();
        ordinary.Load(".a .b > .c { color: fine } #x:hover { color: also-fine }");

        Assert.True(ordinary.Engine.Rules.SharingIsSound);
    }

    [Fact]
    public void The_cache_actually_skips_work() {
        // The oracle proves sharing is *correct*. It says nothing about whether it is *useful*, and
        // a cache that never hit would pass it. This is the other half.
        var fixture = new CascadeFixture();
        fixture.Load(".cell { color: red }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        for (var i = 0; i < 500; i++) {
            fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        }

        fixture.Engine.ResolveAll();

        // The row, and the first cell.
        Assert.Equal(2, fixture.Engine.Resolver.Cascades);
        Assert.Equal(499, fixture.Engine.Resolver.SharingHits);
    }

    [Fact]
    public void Two_children_of_differently_classed_parents_do_not_share() {
        // The counterexample doc 09's key does not survive. Both parents resolve to the *same*
        // computed style — one property, one value — so a key holding the parent's style would let
        // these two children share, and `.a .row` matches only one of them.
        var fixture = new CascadeFixture();
        fixture.Load("""
            .a { color: red }
            .b { color: red }
            .a .row { background: only-under-a }
            """);

        var underA = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var underB = fixture.Tree.CreateElement("div", classNames: ["b"]);
        var firstRow = fixture.Tree.CreateElement("div", underA, classNames: ["row"]);
        var secondRow = fixture.Tree.CreateElement("div", underB, classNames: ["row"]);

        var styles = fixture.Engine.ResolveAll();

        Assert.Same(styles[underA.Index], styles[underB.Index]);
        Assert.Equal("only-under-a", fixture.Read(styles[firstRow.Index], "background"));
        Assert.Null(fixture.Read(styles[secondRow.Index], "background"));
    }

    [Fact]
    public void An_inline_style_stops_an_element_sharing_with_one_that_has_none() {
        var fixture = new CascadeFixture();
        fixture.Load(".cell { color: from-rule }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        var plain = fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        var styled = fixture.Tree.CreateElement("div", row, classNames: ["cell"]);

        var parent = fixture.Engine.Resolver.Resolve(fixture.Tree, row);

        Assert.Equal("from-rule", fixture.Value(plain, parent: parent));
        Assert.Equal(
            "from-inline",
            fixture.Value(styled, parent: parent, inline: fixture.Inline(("color", "from-inline", false)))
        );
    }

    static ComputedStyle[] ResolveAllWithoutSharing(CascadeFixture fixture) {
        var styles = new ComputedStyle[fixture.Tree.Count];

        for (var i = 0; i < fixture.Tree.Count; i++) {
            var element = new StyleNodeId(i);
            var parent = fixture.Tree.GetParent(element);
            styles[i] = fixture.Engine.Resolver.Cascade(
                fixture.Tree,
                element,
                parent.IsValid ? styles[parent.Index] : null
            );
        }

        return styles;
    }

    static string Describe(CascadeFixture fixture, ComputedStyle style) {
        var builder = new StringBuilder();

        for (var i = 0; i < style.Count; i++) {
            builder.Append(fixture.Engine.Properties.NameOf(style.Properties[i]))
                .Append('=')
                .Append(fixture.Engine.Values.NameOf(style.Values[i]))
                .Append(';');
        }

        return builder.ToString();
    }

    static void BuildTree(CascadeFixture fixture, int seed, int depth, int breadth) {
        var tags = new[] { "div", "span", "li", "Button" };
        var classNames = new[] { "row", "cell", "selected", "dark", "sidebar" };
        var random = new Random(seed);

        Build(StyleNodeId.Invalid, 0);
        return;

        void Build(StyleNodeId parent, int level) {
            if (level == depth) {
                return;
            }

            for (var i = 0; i < breadth; i++) {
                var chosen = new List<string>();
                for (var c = 0; c < classNames.Length; c++) {
                    if (random.Next(3) == 0) {
                        chosen.Add(classNames[c]);
                    }
                }

                var element = fixture.Tree.CreateElement(
                    tags[random.Next(tags.Length)],
                    parent,
                    random.Next(5) == 0 ? $"id{random.Next(3)}" : null,
                    [.. chosen]
                );

                if (random.Next(4) == 0) {
                    fixture.Tree.SetState(element, (ElementState) (1u << random.Next(6)));
                }

                Build(element, level + 1);
            }
        }
    }

    static string BuildStylesheet(int seed) {
        var random = new Random(seed ^ 0x2B2B);

        // No position or sibling selectors: with one, sharing turns itself off and the oracle would
        // be comparing the cascade against itself. `Sharing_is_refused_when_…` is what covers those.
        var pieces = new[] {
            "div", "span", "li", "Button", "*",
            ".row", ".cell", ".selected", ".dark", ".sidebar",
            "#id0", "#id1",
            ":hover", ":focus", ":disabled",
            ":not(.selected)", ":is(.row, .cell)"
        };

        var combinators = new[] { " ", " > " };
        var properties = new[] { "color", "background", "padding-left", "font-size", "--accent" };
        var values = new[] { "one", "two", "three", "var(--accent)", "var(--missing, fallback)" };
        var layers = new[] { "", "base", "theme", "utilities" };

        var builder = new StringBuilder();
        builder.Append("@layer base, theme, utilities;\n");

        for (var rule = 0; rule < 30; rule++) {
            var layer = layers[random.Next(layers.Length)];
            if (layer.Length > 0) {
                builder.Append(CultureInfo.InvariantCulture, $"@layer {layer} {{ ");
            }

            var compounds = 1 + random.Next(3);
            for (var c = 0; c < compounds; c++) {
                if (c > 0) {
                    builder.Append(combinators[random.Next(combinators.Length)]);
                }

                var parts = 1 + random.Next(2);
                for (var p = 0; p < parts; p++) {
                    var piece = pieces[random.Next(pieces.Length)];
                    if (p > 0 && (piece == "*" || char.IsLetter(piece[0]))) {
                        piece = ".cell";
                    }

                    builder.Append(piece);
                }
            }

            builder.Append(" { ");
            var declarations = 1 + random.Next(3);
            for (var d = 0; d < declarations; d++) {
                builder.Append(CultureInfo.InvariantCulture,
                    $"{properties[random.Next(properties.Length)]}: {values[random.Next(values.Length)]}"
                );

                if (random.Next(6) == 0) {
                    builder.Append(" !important");
                }

                builder.Append("; ");
            }

            builder.Append('}');
            builder.Append(layer.Length > 0 ? " }\n" : "\n");
        }

        return builder.ToString();
    }
}
