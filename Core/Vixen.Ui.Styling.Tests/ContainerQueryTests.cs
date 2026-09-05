// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>What <c>@container</c> resolves to, asserted as a computed value and never as a parse.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every case here asserts positively <i>and</i> negatively, because a rule that applies
///         unconditionally passes every positive assertion ever written about it.</b> That is the
///         lesson <c>VariantCoverageTests</c> paid for — <c>aria-expanded:</c> matched a collapsed
///         disclosure for a release because the only assertions were "true matches" and "absent does
///         not", and the discriminating case was never written. For a container query the
///         discriminating case is the <i>same rule</i> and the <i>same element</i> in a box of a
///         different size, so nearly every test below builds two.
///     </para>
///     <para>
///         ⚠ <b>None of them asserts that a rule was parsed.</b> <c>@container</c> parsed before this
///         change too — ExCSS 4.3.2 hands back a <c>ContainerRule</c> with its name and condition
///         already split out — and the loader dropped it on the floor of a <c>switch</c> without so
///         much as a diagnostic. A test that had checked the parse would have been green throughout.
///     </para>
/// </remarks>
public class ContainerQueryTests {
    [Fact]
    public void One_rule_answers_differently_in_two_containers_at_the_same_instant() {
        // The headline, and the thing `@media` structurally cannot do: two boxes, one rule set, one
        // moment, two answers.
        var fixture = new CascadeFixture();
        fixture.Load("@container (min-width: 400px) { .body { color: wide } }");

        var roomy = fixture.Tree.CreateElement("div");
        fixture.Contain(roomy, width: 900f);

        var cramped = fixture.Tree.CreateElement("div");
        fixture.Contain(cramped, width: 200f);

        var inRoomy = fixture.Tree.CreateElement("div", roomy, classNames: ["body"]);
        var inCramped = fixture.Tree.CreateElement("div", cramped, classNames: ["body"]);

        Assert.Equal("wide", fixture.Value(inRoomy));
        Assert.Null(fixture.Value(inCramped));
    }

    [Fact]
    public void A_container_does_not_answer_its_own_query() {
        // ⚠ CSS Containment 3 § 5.1: a container query is about the elements *inside* the container.
        // The failure mode of getting this wrong is a query that matches slightly too often, which no
        // test of the ordinary case can see — so it gets its own.
        var fixture = new CascadeFixture();
        fixture.Load("@container (min-width: 400px) { .panel { color: inside } }");

        var container = fixture.Tree.CreateElement("div", classNames: ["panel"]);
        fixture.Contain(container, width: 900f);

        var child = fixture.Tree.CreateElement("div", container, classNames: ["panel"]);

        Assert.Null(fixture.Value(container));
        Assert.Equal("inside", fixture.Value(child));
    }

    [Fact]
    public void A_named_query_asks_the_container_with_that_name_and_not_the_nearest() {
        // The whole point of a name: the box you want is not always the box you are in.
        var fixture = new CascadeFixture();
        fixture.Load("@container outer (min-width: 400px) { .leaf { color: outer-is-wide } }");

        var outer = fixture.Tree.CreateElement("div");
        fixture.Contain(outer, width: 900f, name: "outer");

        var inner = fixture.Tree.CreateElement("div", outer);
        fixture.Contain(inner, width: 100f, name: "inner");

        var leaf = fixture.Tree.CreateElement("div", inner, classNames: ["leaf"]);

        // Asking `outer` skips the narrow `inner` it is nested in.
        Assert.Equal("outer-is-wide", fixture.Value(leaf));

        // And the same tree with the outer box narrow answers the other way, which is what proves the
        // walk read `outer`'s width rather than defaulting to true on finding the name.
        var narrow = new CascadeFixture();
        narrow.Load("@container outer (min-width: 400px) { .leaf { color: outer-is-wide } }");

        var narrowOuter = narrow.Tree.CreateElement("div");
        narrow.Contain(narrowOuter, width: 300f, name: "outer");

        var narrowInner = narrow.Tree.CreateElement("div", narrowOuter);
        narrow.Contain(narrowInner, width: 900f, name: "inner");

        Assert.Null(narrow.Value(narrow.Tree.CreateElement("div", narrowInner, classNames: ["leaf"])));
    }

    [Fact]
    public void A_named_query_does_not_fall_back_to_an_unnamed_container() {
        // ⚠ Found by sabotage rather than by design: relaxing the name test to "matches, or the
        // candidate is unnamed" left every other test in this file green. A `@container card (…)`
        // that silently answers off whatever box happens to be nearest is worse than one that never
        // matches, because it is right until somebody adds a wrapper.
        var fixture = new CascadeFixture();
        fixture.Load("@container card (min-width: 400px) { .leaf { color: named } }");

        var anonymous = fixture.Tree.CreateElement("div");
        fixture.Contain(anonymous, width: 900f);

        Assert.Null(fixture.Value(fixture.Tree.CreateElement("div", anonymous, classNames: ["leaf"])));

        // The same box with the name it was asked for does match, so the null above is the name and
        // not the width.
        var named = fixture.Tree.CreateElement("div");
        fixture.Contain(named, width: 900f, name: "card");

        Assert.Equal("named", fixture.Value(fixture.Tree.CreateElement("div", named, classNames: ["leaf"])));
    }

    [Fact]
    public void An_unnamed_query_asks_the_nearest_container_whatever_its_name() {
        // ⚠ A name is a label a box carries, not a category it joins. Skipping named containers for an
        // unnamed query would make *adding* a name to a container silently retarget every unnamed
        // query below it — a change with no visible cause.
        var fixture = new CascadeFixture();
        fixture.Load("@container (min-width: 400px) { .leaf { color: near-is-wide } }");

        var outer = fixture.Tree.CreateElement("div");
        fixture.Contain(outer, width: 900f);

        var inner = fixture.Tree.CreateElement("div", outer);
        fixture.Contain(inner, width: 100f, name: "sidebar");

        var leaf = fixture.Tree.CreateElement("div", inner, classNames: ["leaf"]);

        // The nearest box is the named narrow one, so the query fails despite the wide box above it.
        Assert.Null(fixture.Value(leaf));

        // Directly under the wide box, the same rule applies.
        Assert.Equal("near-is-wide", fixture.Value(fixture.Tree.CreateElement("div", outer, classNames: ["leaf"])));
    }

    [Fact]
    public void Nested_container_queries_conjoin() {
        // The same property `MediaConditions` has, and it has to hold independently: flattening to the
        // outermost or the innermost passes two of these three and fails the middle one.
        const string Css = """
            @container (min-width: 300px) {
              @container (min-width: 600px) {
                .leaf { color: both }
              }
            }
            """;

        Assert.Equal("both", Resolve(Css, 900f));
        Assert.Null(Resolve(Css, 450f));
        Assert.Null(Resolve(Css, 100f));

        static string? Resolve(string css, float width) {
            var fixture = new CascadeFixture();
            fixture.Load(css);

            var container = fixture.Tree.CreateElement("div");
            fixture.Contain(container, width);

            return fixture.Value(fixture.Tree.CreateElement("div", container, classNames: ["leaf"]));
        }
    }

    [Fact]
    public void A_container_query_nests_through_a_media_query_in_either_order() {
        // ⚠ Two tables, two subjects, and a rule carries one id from each. This is the case that would
        // have forced a tagged union if the container group had been squeezed into `Conditions`.
        const string Inside = """
            @media (min-width: 1000px) {
              @container (min-width: 400px) { .leaf { color: both } }
            }
            """;

        const string Outside = """
            @container (min-width: 400px) {
              @media (min-width: 1000px) { .leaf { color: both } }
            }
            """;

        foreach (var css in new[] { Inside, Outside }) {
            Assert.Equal("both", Resolve(css, surface: 1200f, container: 900f));

            // The window is wide and the panel is not.
            Assert.Null(Resolve(css, surface: 1200f, container: 100f));

            // The panel is wide and the window is not.
            Assert.Null(Resolve(css, surface: 500f, container: 900f));
        }

        static string? Resolve(string css, float surface, float container) {
            var fixture = new CascadeFixture();

            // ⚠ `null` rather than the fixture's default, which is a *fixed* 0×0 context — that is the
            // load-time form, where a `@media` block is kept or dropped there and then. Registering a
            // group per block is the form the surface answers, and it is the only one a container
            // query can nest inside meaningfully.
            fixture.Engine.Load(css, StyleOrigin.Author, media: null);
            fixture.Engine.SetMedia(new MediaContext(surface, 800f));

            var box = fixture.Tree.CreateElement("div");
            fixture.Contain(box, container);

            return fixture.Value(fixture.Tree.CreateElement("div", box, classNames: ["leaf"]));
        }
    }

    [Fact]
    public void An_inline_size_container_cannot_be_asked_about_its_height() {
        // ⚠ The containment, expressed as a refusal to match. An `inline-size` container's height is
        // still its content's, so there is no well-defined number to compare — and answering from the
        // measured height anyway would be a query that reads correct and moves when the text reflows.
        var fixture = new CascadeFixture();
        fixture.Load("@container (min-height: 100px) { .leaf { color: tall } }");

        var inline = fixture.Tree.CreateElement("div");
        fixture.Contain(inline, width: 900f, height: 900f, kind: ContainerKind.InlineSize);

        Assert.Null(fixture.Value(fixture.Tree.CreateElement("div", inline, classNames: ["leaf"])));

        // The same box, the same height, contained on both axes: now it answers.
        var both = fixture.Tree.CreateElement("div");
        fixture.Contain(both, width: 900f, height: 900f, kind: ContainerKind.Size);

        Assert.Equal("tall", fixture.Value(fixture.Tree.CreateElement("div", both, classNames: ["leaf"])));
    }

    [Fact]
    public void A_query_with_no_eligible_container_above_it_matches_nothing() {
        // CSS Containment 3 § 5.1: no container to ask is false, not an error and not a match.
        var fixture = new CascadeFixture();
        fixture.Load(".leaf { color: base } @container (min-width: 1px) { .leaf { color: contained } }");

        // Loose in the document, with nothing containing it.
        Assert.Equal("base", fixture.Value(fixture.Tree.CreateElement("div", classNames: ["leaf"])));

        // ⚠ And a `container-type: normal` ancestor is not an eligible container either — being in the
        // tree is not being a query container.
        var plain = fixture.Tree.CreateElement("div");
        fixture.Contain(plain, width: 900f, kind: ContainerKind.Normal);

        Assert.Equal("base", fixture.Value(fixture.Tree.CreateElement("div", plain, classNames: ["leaf"])));
    }

    [Fact]
    public void A_container_block_is_loaded_rather_than_dropped_and_an_unreadable_one_is_a_diagnostic() {
        // ⚠ The defect this change closes. `@container` arrives from ExCSS as a `ContainerRule` and
        // not as `RuleType.Unknown`, so it never reached `LoadUnknown` — it fell out of the loader's
        // `switch` through `default`, silently, while `StyleDiagnosticDrainTests` and the
        // stylesheet-diagnostics guide both said in prose that it produced a warning.
        var loaded = new CascadeFixture();
        loaded.Load("@container (min-width: 10px) { .leaf { color: kept } }");

        Assert.Empty(loaded.Engine.Loader.Diagnostics);

        var box = loaded.Tree.CreateElement("div");
        loaded.Contain(box, width: 900f);

        Assert.Equal("kept", loaded.Value(loaded.Tree.CreateElement("div", box, classNames: ["leaf"])));

        // A condition that cannot be read is refused once, at load, against no box at all.
        var refused = new CascadeFixture();
        refused.Load("@container (prefers-color-scheme: dark) { .leaf { color: nope } }");

        var diagnostic = Assert.Single(refused.Engine.Loader.Diagnostics);
        Assert.Contains("prefers-color-scheme", diagnostic.Reason, StringComparison.Ordinal);

        var refusedBox = refused.Tree.CreateElement("div");
        refused.Contain(refusedBox, width: 900f);

        Assert.Null(refused.Value(refused.Tree.CreateElement("div", refusedBox, classNames: ["leaf"])));
    }

    [Fact]
    public void Two_containers_of_the_same_size_intern_to_one_scope_and_two_sizes_do_not() {
        // ⚠ Why interning is a correctness question and not a memory one: `StyleSharingKey` carries
        // the scope, so a scope per container *element* would give every row of a list a distinct key
        // and silently disable the sharing cache for any document using a container query.
        var fixture = new CascadeFixture();
        fixture.Load("@container (min-width: 400px) { .row { color: wide } }");

        var first = fixture.Tree.CreateElement("div");
        var second = fixture.Tree.CreateElement("div");
        var third = fixture.Tree.CreateElement("div");

        fixture.Contain(first, width: 500f);
        fixture.Contain(second, width: 500f);
        fixture.Contain(third, width: 300f);

        Assert.Equal(fixture.Tree.GetProvidedContainerScope(first), fixture.Tree.GetProvidedContainerScope(second));
        Assert.NotEqual(fixture.Tree.GetProvidedContainerScope(first), fixture.Tree.GetProvidedContainerScope(third));

        // And the collapsed scope still answers for both, which is the thing interning must not break.
        Assert.Equal("wide", fixture.Value(fixture.Tree.CreateElement("div", first, classNames: ["row"])));
        Assert.Equal("wide", fixture.Value(fixture.Tree.CreateElement("div", second, classNames: ["row"])));
        Assert.Null(fixture.Value(fixture.Tree.CreateElement("div", third, classNames: ["row"])));
    }

    [Fact]
    public void Sharing_is_unsound_only_where_a_positional_rule_sealed_in_a_container_actually_reaches() {
        // The pair, rather than either half. A positional rule behind a container query no box is wide
        // enough for must not turn the sharing cache off for the whole document.
        var fixture = new CascadeFixture();
        fixture.Load("@container (min-width: 400px) { .row:nth-child(2n) { color: even } }");

        var narrow = fixture.Tree.CreateElement("div");
        fixture.Contain(narrow, width: 100f);

        var wide = fixture.Tree.CreateElement("div");
        fixture.Contain(wide, width: 900f);

        var verdicts = fixture.Engine.Scopes.VerdictsOf(MediaScopes.Document);

        Assert.True(
            fixture.Engine.Rules.SharingIsSound(
                verdicts,
                fixture.Engine.ContainerScopes.VerdictsOf(fixture.Tree.GetProvidedContainerScope(narrow))
            )
        );

        Assert.False(
            fixture.Engine.Rules.SharingIsSound(
                verdicts,
                fixture.Engine.ContainerScopes.VerdictsOf(fixture.Tree.GetProvidedContainerScope(wide))
            )
        );
    }

    [Fact]
    public void A_reload_keeps_the_scopes_and_re_registers_the_groups() {
        // ⚠ A hot edit of a stylesheet does not move the box an element is inside, so `Build` resets
        // the condition table and must not reset the scopes — doing so would leave every element
        // pointing at a chain that no longer exists, answering every query false until the next
        // layout pass, which is a silence that looks exactly like the feature not working.
        var fixture = new CascadeFixture();
        fixture.Load("@container (min-width: 400px) { .leaf { color: first } }");

        var box = fixture.Tree.CreateElement("div");
        fixture.Contain(box, width: 900f);

        var leaf = fixture.Tree.CreateElement("div", box, classNames: ["leaf"]);
        Assert.Equal("first", fixture.Value(leaf));

        var scope = fixture.Tree.GetContainerScope(leaf);

        fixture.Engine.Reload();

        Assert.Equal(scope, fixture.Tree.GetContainerScope(leaf));
        Assert.Equal("first", fixture.Value(leaf));
    }

    [Theory]
    // Range features, both spellings of each axis.
    [InlineData("(min-width: 400px)", 500f, 100f, ContainerKind.Size, true)]
    [InlineData("(min-width: 400px)", 300f, 100f, ContainerKind.Size, false)]
    [InlineData("(max-width: 400px)", 300f, 100f, ContainerKind.Size, true)]
    [InlineData("(min-inline-size: 400px)", 500f, 100f, ContainerKind.Size, true)]
    [InlineData("(min-block-size: 400px)", 100f, 500f, ContainerKind.Size, true)]
    [InlineData("(min-block-size: 400px)", 100f, 500f, ContainerKind.InlineSize, false)]
    // Conjunction.
    [InlineData("(min-width: 400px) and (max-width: 800px)", 500f, 100f, ContainerKind.Size, true)]
    [InlineData("(min-width: 400px) and (max-width: 800px)", 900f, 100f, ContainerKind.Size, false)]
    // Both-axis features, which an `inline-size` container may not be asked.
    [InlineData("(orientation: landscape)", 500f, 100f, ContainerKind.Size, true)]
    [InlineData("(orientation: portrait)", 500f, 100f, ContainerKind.Size, false)]
    [InlineData("(orientation: landscape)", 500f, 100f, ContainerKind.InlineSize, false)]
    [InlineData("(min-aspect-ratio: 2/1)", 500f, 100f, ContainerKind.Size, true)]
    [InlineData("(min-aspect-ratio: 2/1)", 500f, 400f, ContainerKind.Size, false)]
    // The boolean form, and a unit that is not pixels.
    [InlineData("(width)", 500f, 100f, ContainerKind.Size, true)]
    [InlineData("(width)", 0f, 100f, ContainerKind.Size, false)]
    public void Size_features_evaluate(
        string condition,
        float width,
        float height,
        ContainerKind kind,
        bool expected
    ) {
        var box = new ContainerBox(width, height, kind);

        Assert.True(ContainerQuery.TryEvaluate(condition, box, out var matches, out var reason));
        Assert.Null(reason);
        Assert.Equal(expected, matches);
    }

    [Theory]
    // ⚠ The media-only features, refused rather than answered off whatever surface the element is on.
    [InlineData("(prefers-color-scheme: dark)")]
    [InlineData("(color-gamut: p3)")]
    [InlineData("(min-resolution: 2x)")]
    [InlineData("(min-width: banana)")]
    [InlineData("screen")]
    // ⚠ Range syntax that is not a range. A comparison with a side missing, two values compared with
    // each other, a pair of operators pointing opposite ways, and the two spellings mixed — each of
    // these has an obvious wrong reading, and taking it would make the query mean something the
    // author did not write.
    [InlineData("(width <)")]
    [InlineData("(400px < 600px)")]
    [InlineData("(400px < width > 600px)")]
    [InlineData("(min-width: 400px < 600px)")]
    [InlineData("(orientation > landscape)")]
    public void Features_a_box_does_not_have_are_refused(string condition) {
        var box = new ContainerBox(500f, 500f, ContainerKind.Size);

        Assert.False(ContainerQuery.TryEvaluate(condition, box, out _, out var reason));

        Assert.NotNull(reason);
    }

    [Theory]
    // ⚠ **The threshold, which is the only width where the two spellings differ at all.** A reader
    // that dropped the operator and kept the `max-` reading passes every other row here.
    [InlineData("(width < 400px)", 400f, false)]
    [InlineData("(max-width: 400px)", 400f, true)]
    [InlineData("(width > 400px)", 400f, false)]
    [InlineData("(min-width: 400px)", 400f, true)]
    // A texel either side of it the four agree, which is what makes the threshold the whole test.
    [InlineData("(width < 400px)", 399f, true)]
    [InlineData("(max-width: 400px)", 399f, true)]
    [InlineData("(width > 400px)", 401f, true)]
    [InlineData("(min-width: 400px)", 401f, true)]
    // The inclusive operators, which are the prefixes' exact synonyms and must stay so.
    [InlineData("(width <= 400px)", 400f, true)]
    [InlineData("(width >= 400px)", 400f, true)]
    [InlineData("(width = 400px)", 400f, true)]
    [InlineData("(width = 400px)", 401f, false)]
    // ⚠ Written the other way round, which CSS allows and which flips the operator rather than the
    // sides: `400px > width` is `width < 400px`, so it must be false at 400 and not true.
    [InlineData("(400px > width)", 400f, false)]
    [InlineData("(400px > width)", 399f, true)]
    [InlineData("(400px <= width)", 400f, true)]
    // The two-sided form, whose lower bound is inclusive and whose upper bound is not — v4's own
    // `@min-sm:@max-lg:` written as one term.
    [InlineData("(400px <= width < 600px)", 400f, true)]
    [InlineData("(400px <= width < 600px)", 599f, true)]
    [InlineData("(400px <= width < 600px)", 600f, false)]
    [InlineData("(400px <= width < 600px)", 399f, false)]
    // The logical spelling of the same axis reads the same operators.
    [InlineData("(inline-size < 400px)", 400f, false)]
    public void A_range_comparison_and_its_prefix_spelling_part_company_at_the_threshold(
        string condition,
        float width,
        bool expected
    ) {
        var box = new ContainerBox(width, 100f, ContainerKind.Size);

        Assert.True(ContainerQuery.TryEvaluate(condition, box, out var matches, out var reason), reason);
        Assert.Equal(expected, matches);
    }

    [Fact]
    public void A_range_condition_survives_the_stylesheet_parser_and_not_only_the_evaluator() {
        // ⚠ The evaluator is not the whole path: `@container`'s prelude reaches it as ExCSS's
        // `ConditionText`, so a parser that normalised or swallowed `<` would leave every assertion
        // above green while no stylesheet in the tree could spell the exclusive form.
        var fixture = new CascadeFixture();
        fixture.Load("@container (width < 400px) { .leaf { color: narrow } }");

        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var atThreshold = fixture.Tree.CreateElement("div");
        fixture.Contain(atThreshold, width: 400f);

        Assert.Null(fixture.Value(fixture.Tree.CreateElement("div", atThreshold, classNames: ["leaf"])));

        var below = fixture.Tree.CreateElement("div");
        fixture.Contain(below, width: 399f);

        Assert.Equal("narrow", fixture.Value(fixture.Tree.CreateElement("div", below, classNames: ["leaf"])));
    }
}
