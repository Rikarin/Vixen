// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using CsCheck;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>An incremental restyle against a cold one, over randomised trees and mutations.</summary>
/// <remarks>
///     <para>
///         <see cref="InvalidationTests" /> counts what a change restyled. It cannot say the result
///         was <i>right</i> — an invalidator that skipped an element it should have visited passes
///         every count assertion by producing a smaller number. This is the other half, and it is the
///         one that would have caught the equivalent bug in the layout tree: a property test against
///         a cold-computed oracle is what found rounding compounding across incremental passes, and
///         nothing else did.
///     </para>
///     <para>
///         The property is exact and by value: after any sequence of class, state and inline changes,
///         every element's computed style must equal what a full pass from scratch would have
///         produced. Not "mostly" and not "for the elements we invalidated" — <i>every</i> element,
///         because the elements an invalidator wrongly skips are precisely the ones it did not think
///         to look at.
///     </para>
/// </remarks>
public class IncrementalRestyleOracleTests {
    [Fact]
    public void An_incremental_restyle_produces_exactly_what_a_cold_one_produces() {
        Gen.Select(Gen.Int[0, 100_000], Gen.Int[2, 4], Gen.Int[1, 3], Gen.Int[1, 6]).Sample(shape => {
                var (seed, depth, breadth, mutations) = shape;
                var css = BuildStylesheet(seed);
                var random = new Random(seed ^ 0x7717);

                var live = new CascadeFixture();
                live.Load(css);
                BuildTree(live, seed, depth, breadth);

                var updater = new StyleUpdater(live.Engine);
                updater.ResolveAll();

                for (var m = 0; m < mutations; m++) {
                    var mutation = Mutate(live, random);

                    if (mutation.IsState) {
                        updater.StateChanged(mutation.Element);
                    } else {
                        updater.ClassChanged(mutation.Element, mutation.ClassName!);
                    }
                }

                // The oracle: a second engine with the same stylesheet, and a tree built *directly*
                // in the state the mutations left the first one in.
                //
                // Not "the same tree with the same mutations replayed" — that was the first version
                // of this and it was weaker than it looked. Replaying the mutations means both sides
                // reach their final state through the same mutation code, so anything that code gets
                // wrong is wrong identically on both and the comparison sees nothing. It let a
                // sabotage through: deleting the ancestor-bloom propagation in `AddClass` broke
                // matching and this test stayed green, because the oracle's blooms were broken the
                // same way. Built from scratch, the oracle's blooms are right by construction.
                var cold = new CascadeFixture();
                cold.Load(css);
                CopyTree(live, cold);

                var reference = new StyleUpdater(cold.Engine);
                reference.ResolveAll();

                for (var i = 0; i < live.Tree.Count; i++) {
                    var element = new StyleNodeId(i);

                    Assert.Equal(
                        Describe(cold, reference.StyleOf(element)),
                        Describe(live, updater.StyleOf(element))
                    );
                }
            }, iter: 300
        );
    }

    [Fact]
    public void The_same_holds_when_the_sharing_cache_is_in_play() {
        // The property above cannot see the sharing cache at all: every stylesheet it generates
        // contains a sibling or position selector, which turns sharing off for the whole rule set.
        // So a sabotage that left the cache in place across passes — handing an element a style
        // cached before its parent changed — sailed straight through it.
        //
        // This runs the same property over stylesheets deliberately free of anything that would
        // disable sharing, and asserts sharing was actually on, so that the pass-scoping is under
        // test rather than merely unreachable.
        var sharedPasses = 0;

        Gen.Select(Gen.Int[0, 100_000], Gen.Int[2, 4], Gen.Int[2, 3], Gen.Int[1, 6]).Sample(shape => {
                var (seed, depth, breadth, mutations) = shape;
                var css = BuildStylesheet(seed, sharingSafe: true);
                var random = new Random(seed ^ 0x4C4C);

                var live = new CascadeFixture();
                live.Load(css);
                BuildTree(live, seed, depth, breadth);

                Assert.True(live.Engine.Rules.SharingIsSound);

                var updater = new StyleUpdater(live.Engine);
                updater.ResolveAll();

                for (var m = 0; m < mutations; m++) {
                    var mutation = Mutate(live, random);

                    if (mutation.IsState) {
                        updater.StateChanged(mutation.Element);
                    } else {
                        updater.ClassChanged(mutation.Element, mutation.ClassName!);
                    }
                }

                Interlocked.Add(ref sharedPasses, live.Engine.Resolver.SharingHits);

                var cold = new CascadeFixture();
                cold.Load(css);
                CopyTree(live, cold);

                var reference = new StyleUpdater(cold.Engine);
                reference.ResolveAll();

                for (var i = 0; i < live.Tree.Count; i++) {
                    var element = new StyleNodeId(i);

                    Assert.Equal(
                        Describe(cold, reference.StyleOf(element)),
                        Describe(live, updater.StyleOf(element))
                    );
                }
            }, iter: 300
        );

        Assert.True(sharedPasses > 0, "the sharing cache never fired, so the property proved nothing about it");
    }

    [Fact]
    public void The_incremental_pass_is_doing_less_work_than_the_cold_one() {
        // The oracle proves the incremental pass is *right*. An implementation that restyled
        // everything on every change would also be right, and would be pointless. This is the other
        // half, on a shape big enough for the difference to be a difference.
        var fixture = new CascadeFixture();
        fixture.Load(".row { background: normal } .row.selected { background: highlighted }");

        var grid = fixture.Tree.CreateElement("div", classNames: ["grid"]);
        var rows = new StyleNodeId[50];

        for (var r = 0; r < rows.Length; r++) {
            rows[r] = fixture.Tree.CreateElement("div", grid, classNames: ["row"]);
            for (var c = 0; c < 50; c++) {
                fixture.Tree.CreateElement("div", rows[r], classNames: ["cell"]);
            }
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Equal(2_551, updater.LastPassResolved);

        fixture.Tree.AddClass(rows[7], "selected");

        Assert.Equal(1, updater.ClassChanged(rows[7], "selected"));
    }

    [Fact]
    public void Toggling_a_class_back_returns_the_very_same_style_object() {
        // Interning makes this the strongest statement available: not "an equal style" but the same
        // object, so anything downstream comparing references sees no change at all.
        var fixture = new CascadeFixture();
        fixture.Load(".row { color: normal } .row.selected { color: highlighted }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        fixture.Tree.CreateElement("div", row, classNames: ["cell"]);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        var before = updater.StyleOf(row);
        var childBefore = updater.StyleOf(fixture.Tree.GetChild(row, 0));

        fixture.Tree.AddClass(row, "selected");
        updater.ClassChanged(row, "selected");

        Assert.NotSame(before, updater.StyleOf(row));

        fixture.Tree.RemoveClass(row, "selected");
        updater.ClassChanged(row, "selected");

        Assert.Same(before, updater.StyleOf(row));
        Assert.Same(childBefore, updater.StyleOf(fixture.Tree.GetChild(row, 0)));
    }

    readonly record struct Mutation(StyleNodeId Element, bool IsState, string? ClassName, ElementState State, bool Add);

    static Mutation Mutate(CascadeFixture fixture, Random random) {
        var element = new StyleNodeId(random.Next(fixture.Tree.Count));

        if (random.Next(4) == 0) {
            var state = (ElementState) (1u << random.Next(6));
            fixture.Tree.SetState(element, state);
            return new Mutation(element, true, null, state, true);
        }

        var className = Classes[random.Next(Classes.Length)];
        var add = !fixture.Tree.HasClass(element, className);

        if (add) {
            fixture.Tree.AddClass(element, className);
        } else {
            fixture.Tree.RemoveClass(element, className);
        }

        return new Mutation(element, false, className, ElementState.None, add);
    }

    /// <summary>Builds a fresh tree in exactly the state another one is currently in.</summary>
    /// <remarks>
    ///     Elements are created parents-before-children, so ascending index is a valid build order
    ///     and a parent's id in the copy is the same integer it has in the original.
    /// </remarks>
    static void CopyTree(CascadeFixture from, CascadeFixture to) {
        for (var i = 0; i < from.Tree.Count; i++) {
            var element = new StyleNodeId(i);
            var parent = from.Tree.GetParent(element);

            var created = to.Tree.CreateElement(
                from.Tree.GetTagName(element),
                parent.IsValid ? parent : null,
                from.Tree.GetId(element),
                from.Tree.GetClassNames(element)
            );

            to.Tree.SetState(created, from.Tree.GetState(element));
        }
    }

    static readonly string[] Classes = ["row", "cell", "selected", "dark", "sidebar", "label"];

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
        var random = new Random(seed);

        Build(StyleNodeId.Invalid, 0);
        return;

        void Build(StyleNodeId parent, int level) {
            if (level == depth) {
                return;
            }

            for (var i = 0; i < breadth; i++) {
                var chosen = new List<string>();
                for (var c = 0; c < Classes.Length; c++) {
                    if (random.Next(3) == 0) {
                        chosen.Add(Classes[c]);
                    }
                }

                var element = fixture.Tree.CreateElement(
                    tags[random.Next(tags.Length)],
                    parent,
                    random.Next(6) == 0 ? $"id{random.Next(3)}" : null,
                    [.. chosen]
                );

                if (random.Next(5) == 0) {
                    fixture.Tree.SetState(element, (ElementState) (1u << random.Next(6)));
                }

                Build(element, level + 1);
            }
        }
    }

    static string BuildStylesheet(int seed, bool sharingSafe = false) {
        var random = new Random(seed ^ 0x1D1D);

        // Sibling and position selectors are in, unlike the sharing oracle's generator: they turn
        // *sharing* off, and invalidation has to handle them rather than avoid them. If anything,
        // they are where it is most likely to be wrong.
        var pieces = sharingSafe
            ? [
                "div", "span", "li", "Button", "*",
                ".row", ".cell", ".selected", ".dark", ".sidebar", ".label",
                "#id0", "#id1",
                ":hover", ":focus", ":disabled",
                ":not(.selected)", ":is(.row, .cell)"
            ]
            : new[] {
                "div", "span", "li", "Button", "*",
                ".row", ".cell", ".selected", ".dark", ".sidebar", ".label",
                "#id0", "#id1",
                ":hover", ":focus", ":disabled", ":first-child", ":nth-child(2n)",
                ":not(.selected)", ":is(.row, .cell)"
            };

        var combinators = sharingSafe ? [" ", " > "] : new[] { " ", " > ", " + ", " ~ " };
        var properties = new[] { "color", "background", "padding-left", "font-size", "--accent" };
        var values = new[] { "one", "two", "three", "var(--accent)", "var(--missing, fallback)" };

        var builder = new StringBuilder();
        builder.Append("@layer base, theme;\n");

        for (var rule = 0; rule < 30; rule++) {
            var layered = random.Next(3) == 0;
            if (layered) {
                builder.Append(random.Next(2) == 0 ? "@layer base { " : "@layer theme { ");
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
            builder.Append(layered ? " }\n" : "\n");
        }

        return builder.ToString();
    }
}
