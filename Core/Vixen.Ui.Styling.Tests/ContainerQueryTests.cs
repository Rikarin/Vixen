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

    /// <summary>
    ///     ⚠ <c>container-name</c> is a list, and a size query asks for any one name in it (#273).
    /// </summary>
    /// <remarks>
    ///     CSS Containment 3 § 3.1: <c>container-name: card side</c> gives the box both names. The
    ///     style query already read it that way (<c>StyleQuery.Names</c>). The size query compared the
    ///     whole written list with the one name asked for, so <c>@container side (…)</c> never found a
    ///     box named <c>card side</c>, silently. The two halves of a mixed query have to pick the same
    ///     box, so they have to agree on what a name is. <c>card-like</c> is the negative: a name
    ///     matches whole, never as a prefix or a substring.
    /// </remarks>
    [Theory]
    [InlineData("card side", "side", true)]
    [InlineData("card side", "card", true)]
    [InlineData("card  side", "side", true)]
    [InlineData("card-like side", "card", false)]
    [InlineData("cardside", "side", false)]
    public void A_size_query_finds_a_container_by_any_name_in_its_list(string names, string asked, bool matches) {
        var fixture = new CascadeFixture();
        fixture.Load($"@container {asked} (min-width: 400px) {{ .leaf {{ color: named }} }}");

        var container = fixture.Tree.CreateElement("div");
        fixture.Contain(container, width: 900f, name: names);

        Assert.Equal(matches ? "named" : null, fixture.Value(fixture.Tree.CreateElement("div", container, classNames: ["leaf"])));
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

    /// <summary>
    ///     ⚠ A container that cannot answer every feature is skipped, and the query asks the next one
    ///     up that can (#1429).
    /// </summary>
    /// <remarks>
    ///     CSS Containment 3 § 5.1: the container a query asks is the nearest ancestor that is a valid
    ///     query container for every feature in it. The walk used to stop at the first non-<c>normal</c>
    ///     box, so a block-axis query under an <c>inline-size</c> container resolved <c>false</c> however
    ///     tall the <c>size</c> container above it was. Each row is the same two boxes — an outer
    ///     <c>size</c> container and an inner <c>inline-size</c> one 100 wide and 50 tall — with only the
    ///     outer box's size changed, so a row that flips can only have read the outer box. The width
    ///     rows are the control: a width query is answerable by the inner box, so it must still stop
    ///     there, and <c>(max-width: 400px)</c> holds off the inner 100 and not the outer 900.
    /// </remarks>
    [Theory]
    [InlineData("(min-height: 200px)", 900f, 900f, true)]
    [InlineData("(min-height: 200px)", 900f, 150f, false)]
    [InlineData("(min-block-size: 200px)", 900f, 900f, true)]
    [InlineData("(height >= 200px)", 900f, 900f, true)]
    [InlineData("(orientation: portrait)", 300f, 900f, true)]
    [InlineData("(orientation: portrait)", 900f, 300f, false)]
    [InlineData("(min-aspect-ratio: 2/1)", 900f, 300f, true)]
    [InlineData("(min-aspect-ratio: 2/1)", 300f, 900f, false)]
    [InlineData("(min-width: 400px) and (min-height: 200px)", 900f, 900f, true)]
    [InlineData("(max-width: 400px) and (min-height: 200px)", 900f, 900f, false)]
    [InlineData("(max-width: 400px)", 900f, 900f, true)]
    [InlineData("(min-width: 400px)", 900f, 900f, false)]
    // ⚠ A feature after `or` counts as much as one after `and`: the inner box can answer the width
    // half, and would, if the requirement were read off the first feature only.
    [InlineData("(max-width: 50px) or (min-height: 200px)", 900f, 900f, true)]
    [InlineData("(max-width: 50px) or (min-height: 200px)", 900f, 150f, false)]
    [InlineData("not (min-height: 200px)", 900f, 150f, true)]
    [InlineData("not (min-height: 200px)", 900f, 900f, false)]
    public void A_query_skips_a_container_that_cannot_answer_every_feature(
        string condition,
        float outerWidth,
        float outerHeight,
        bool matches
    ) {
        var fixture = new CascadeFixture();
        fixture.Load($"@container {condition} {{ .leaf {{ color: asked }} }}");

        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var outer = fixture.Tree.CreateElement("div");
        fixture.Contain(outer, width: outerWidth, height: outerHeight, kind: ContainerKind.Size);

        var inner = fixture.Tree.CreateElement("div", outer);
        fixture.Contain(inner, width: 100f, height: 50f, kind: ContainerKind.InlineSize);

        var leaf = fixture.Tree.CreateElement("div", inner, classNames: ["leaf"]);

        Assert.Equal(matches ? "asked" : null, fixture.Value(leaf));
    }

    /// <summary>
    ///     The skip happens before the name is looked at: an <c>inline-size</c> box carrying the name is
    ///     not the container a height query with that name asks (#1429).
    /// </summary>
    [Fact]
    public void A_named_height_query_skips_a_named_inline_size_container() {
        var fixture = new CascadeFixture();
        fixture.Load("@container card (min-height: 200px) { .leaf { color: tall-card } }");

        var outer = fixture.Tree.CreateElement("div");
        fixture.Contain(outer, width: 900f, height: 900f, name: "card", kind: ContainerKind.Size);

        var inner = fixture.Tree.CreateElement("div", outer);
        fixture.Contain(inner, width: 900f, height: 50f, name: "card", kind: ContainerKind.InlineSize);

        Assert.Equal("tall-card", fixture.Value(fixture.Tree.CreateElement("div", inner, classNames: ["leaf"])));

        // With no `size` card above it, there is no eligible container and the query is false — the
        // skip is not a fall-through to "true".
        var alone = new CascadeFixture();
        alone.Load("@container card (min-height: 200px) { .leaf { color: tall-card } }");

        var only = alone.Tree.CreateElement("div");
        alone.Contain(only, width: 900f, height: 900f, name: "card", kind: ContainerKind.InlineSize);

        Assert.Null(alone.Value(alone.Tree.CreateElement("div", only, classNames: ["leaf"])));
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

    /// <summary>
    ///     ⚠ <c>@container not (…)</c> negates, and it used to be a query for a container called
    ///     <c>not</c> (#273).
    /// </summary>
    /// <remarks>
    ///     ExCSS reads the first word of the prelude as the name, so the rule loaded with no diagnostic,
    ///     asked for a box named <c>not</c>, which CSS forbids as a name and no box carries, and never
    ///     applied. The three boxes are the whole truth table: narrow is a match, wide is not, and no
    ///     container at all is unknown, which does not negate into a match.
    /// </remarks>
    [Fact]
    public void A_negated_size_query_negates_and_is_not_a_container_named_not() {
        var fixture = new CascadeFixture();
        fixture.Load("@container not (min-width: 400px) { .leaf { color: narrow } }");

        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var narrow = fixture.Tree.CreateElement("div");
        fixture.Contain(narrow, width: 300f);

        var wide = fixture.Tree.CreateElement("div");
        fixture.Contain(wide, width: 900f);

        Assert.Equal("narrow", fixture.Value(fixture.Tree.CreateElement("div", narrow, classNames: ["leaf"])));
        Assert.Null(fixture.Value(fixture.Tree.CreateElement("div", wide, classNames: ["leaf"])));
        Assert.Null(fixture.Value(fixture.Tree.CreateElement("div", classNames: ["leaf"])));
    }

    /// <summary>
    ///     ⚠ <c>or</c> joins size features, and every such query used to be refused as "'not all' is
    ///     not a container feature" (#273).
    /// </summary>
    /// <remarks>
    ///     ExCSS does not know <c>or</c> in a container prelude and hands its condition over as
    ///     <c>not all</c>, so the loader re-reads the source text. Named and unnamed, each row on both
    ///     sides of both halves.
    /// </remarks>
    [Theory]
    [InlineData("", 500f, 100f, true)]
    [InlineData("", 300f, 500f, true)]
    [InlineData("", 300f, 100f, false)]
    [InlineData("card ", 500f, 100f, true)]
    [InlineData("card ", 300f, 100f, false)]
    public void Or_joins_size_features(string name, float width, float height, bool matches) {
        var fixture = new CascadeFixture();
        fixture.Load($"@container {name}(min-width: 400px) or (min-height: 400px) {{ .leaf {{ color: either }} }}");

        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var box = fixture.Tree.CreateElement("div");
        fixture.Contain(box, width: width, height: height, name: name.Trim(), kind: ContainerKind.Size);

        Assert.Equal(matches ? "either" : null, fixture.Value(fixture.Tree.CreateElement("div", box, classNames: ["leaf"])));
    }

    /// <summary>
    ///     ⚠ A comment in a prelude the loader re-reads from source is ignored, as CSS ignores it, and
    ///     it used to be read as a word.
    /// </summary>
    /// <remarks>
    ///     <c>@container /* c */ (a) or (b)</c> takes the re-read path, because ExCSS hands an
    ///     <c>or</c> over as <c>not all</c>, and the splitter took <c>/*</c> as the container name and
    ///     refused the rest as "not a container feature". The same prelude without <c>or</c> loaded,
    ///     because ExCSS strips the comment on its own path. Each prelude is asked of a box on both
    ///     sides of its threshold, so a rule that loaded and applied unconditionally fails too. The
    ///     last row hides a brace in the comment, which used to end the prelude early.
    /// </remarks>
    [Theory]
    [InlineData("/* c */ (min-width: 400px) or (min-height: 400px)", "")]
    [InlineData("(min-width: 400px) /* c */ or /* c */ (min-height: 400px)", "")]
    [InlineData("card /* c */ (min-width: 400px) or (min-height: 400px)", "card")]
    [InlineData("/* c */ card (min-width: 400px) or (min-height: 400px)", "card")]
    [InlineData("/* c */ not (max-width: 399px)", "")]
    [InlineData("(min-width: 400px) or /* { */ (min-height: 400px)", "")]
    public void A_comment_in_a_re_read_prelude_is_ignored(string prelude, string name) {
        var fixture = new CascadeFixture();
        fixture.Load($"@container {prelude} {{ .leaf {{ color: either }} }}");

        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var wide = fixture.Tree.CreateElement("div");
        fixture.Contain(wide, width: 500f, height: 100f, name: name, kind: ContainerKind.Size);

        var narrow = fixture.Tree.CreateElement("div");
        fixture.Contain(narrow, width: 300f, height: 100f, name: name, kind: ContainerKind.Size);

        Assert.Equal("either", fixture.Value(fixture.Tree.CreateElement("div", wide, classNames: ["leaf"])));
        Assert.Null(fixture.Value(fixture.Tree.CreateElement("div", narrow, classNames: ["leaf"])));
    }

    /// <summary>What the grammar still refuses, each with a reason, and the names CSS reserves.</summary>
    [Theory]
    [InlineData("@container (min-width: 1px) and (max-width: 9px) or (min-height: 1px) { .leaf { color: x } }", "cannot be mixed")]
    [InlineData("@container not (min-width: 1px) and (max-width: 9px) { .leaf { color: x } }", "'not' applies to one feature")]
    [InlineData("@container ((min-width: 1px) and (max-width: 9px)) { .leaf { color: x } }", "parenthesised group")]
    [InlineData("@container (min-width: 1px) xor (max-width: 9px) { .leaf { color: x } }", "does not join")]
    [InlineData("@container none (min-width: 1px) { .leaf { color: x } }", "'none' cannot be a container name")]
    [InlineData("@container or (min-width: 1px) or (max-width: 9px) { .leaf { color: x } }", "'or' cannot be a container name")]
    public void A_size_query_this_grammar_cannot_read_is_a_diagnostic(string css, string because) {
        var fixture = new CascadeFixture();
        fixture.Load(css);

        var diagnostic = Assert.Single(fixture.Engine.Loader.Diagnostics);
        Assert.Contains(because, diagnostic.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ An unreadable <i>length</i> is a load diagnostic too, and it was not: the row above
    ///     refuses a feature name, which is read before the box is consulted, and a value was read
    ///     after.
    /// </summary>
    [Fact]
    public void A_container_block_with_an_unreadable_length_is_a_diagnostic_at_load() {
        var refused = new CascadeFixture();
        refused.Load("@container (min-width: 30furlongs) { .leaf { color: nope } }");

        var diagnostic = Assert.Single(refused.Engine.Loader.Diagnostics);
        Assert.Contains("30furlongs", diagnostic.Reason, StringComparison.Ordinal);

        // And a font-relative one is not refused: it loads, and it answers.
        var loaded = new CascadeFixture();
        loaded.Load("@container (min-width: 30rem) { .leaf { color: kept } }");

        Assert.Empty(loaded.Engine.Loader.Diagnostics);

        var box = loaded.Tree.CreateElement("div");
        loaded.Contain(box, width: 900f);

        Assert.Equal("kept", loaded.Value(loaded.Tree.CreateElement("div", box, classNames: ["leaf"])));
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
    // Disjunction and negation (#273).
    [InlineData("(min-width: 400px) or (min-height: 400px)", 500f, 100f, ContainerKind.Size, true)]
    [InlineData("(min-width: 400px) or (min-height: 400px)", 300f, 500f, ContainerKind.Size, true)]
    [InlineData("(min-width: 400px) or (min-height: 400px)", 300f, 100f, ContainerKind.Size, false)]
    [InlineData("not (min-width: 400px)", 300f, 100f, ContainerKind.Size, true)]
    [InlineData("not (min-width: 400px)", 500f, 100f, ContainerKind.Size, false)]
    [InlineData("NOT (min-width: 400px)", 300f, 100f, ContainerKind.Size, true)]
    // ⚠ A feature the box cannot answer is unknown, and `not` does not turn unknown into a match.
    [InlineData("not (min-height: 400px)", 300f, 100f, ContainerKind.InlineSize, false)]
    [InlineData("not (min-height: 400px)", 300f, 500f, ContainerKind.InlineSize, false)]
    // ⚠ Unknown in a list is CSS's three-valued logic: true beside a true under `or`, and false beside
    // a false under `and`. Any unknown feature used to make the whole condition false, so the first
    // row answered no although its known half holds. Every row that expects false has a height of
    // 500, so an evaluator that read the height anyway would find that feature holding.
    [InlineData("(min-width: 400px) or (min-height: 400px)", 500f, 100f, ContainerKind.InlineSize, true)]
    [InlineData("(min-height: 400px) or (min-width: 400px)", 500f, 100f, ContainerKind.InlineSize, true)]
    [InlineData("(min-width: 400px) or (min-height: 400px)", 300f, 500f, ContainerKind.InlineSize, false)]
    [InlineData("(min-width: 400px) and (min-height: 400px)", 500f, 500f, ContainerKind.InlineSize, false)]
    [InlineData("(min-width: 400px) and (min-height: 400px)", 300f, 500f, ContainerKind.InlineSize, false)]
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
    [InlineData("(aspect-ratio: banana)")]
    [InlineData("(orientation: sideways)")]
    public void Features_a_box_does_not_have_are_refused(string condition) {
        var box = new ContainerBox(500f, 500f, ContainerKind.Size);

        Assert.False(ContainerQuery.TryEvaluate(condition, box, out _, out var reason));

        Assert.NotNull(reason);
    }

    /// <summary>
    ///     ⚠ An unreadable value is refused by a box that answers nothing, too, which is the box the
    ///     loader asks.
    /// </summary>
    /// <remarks>
    ///     <c>StyleSheetLoader.LoadContainer</c> decides readability once, against <c>default</c>, whose
    ///     <c>Kind</c> is <c>Normal</c>. The containment test used to run before the value was parsed,
    ///     so that box returned "readable, no match" for any length at all. <c>(min-width: banana)</c>
    ///     loaded with no diagnostic and then failed per element per frame, where
    ///     <c>ContainerConditions</c> reads a refusal as "no". That is the silent never-match the
    ///     loader's remark says cannot happen.
    /// </remarks>
    [Theory]
    [InlineData("(min-width: banana)")]
    [InlineData("(width < 30furlongs)")]
    [InlineData("(400px <= width < banana)")]
    [InlineData("(min-height: 2fr)")]
    [InlineData("(aspect-ratio: banana)")]
    [InlineData("(orientation: sideways)")]
    public void An_unreadable_value_is_refused_by_a_box_that_answers_nothing(string condition) {
        Assert.False(ContainerQuery.TryEvaluate(condition, default, out _, out var reason));
        Assert.NotNull(reason);

        Assert.False(ContainerQuery.TryEvaluate(condition, new ContainerBox(500f, 500f, ContainerKind.InlineSize), out _, out _));
    }

    /// <summary>
    ///     A container query's <c>em</c> is the container's own font and its <c>rem</c> is the root's
    ///     (#1373), and each row is asked on both sides of its threshold.
    /// </summary>
    /// <remarks>
    ///     The box's font is 20 and the root's is 10, so the two units disagree by a factor of two
    ///     and a reader that swapped them, or that took either one at sixteen, fails a row. These rows
    ///     were refusals until the unit was read.
    /// </remarks>
    [Theory]
    [InlineData("(min-width: 20em)", 400f, true)]
    [InlineData("(min-width: 20em)", 399f, false)]
    [InlineData("(width < 20em)", 399f, true)]
    [InlineData("(width < 20em)", 400f, false)]
    [InlineData("(min-width: 30rem)", 300f, true)]
    [InlineData("(min-width: 30rem)", 299f, false)]
    [InlineData("(200px <= width < 30rem)", 299f, true)]
    [InlineData("(200px <= width < 30rem)", 300f, false)]
    public void A_font_relative_width_is_measured_against_the_containers_font_and_the_roots(string condition, float width, bool expected) {
        var box = new ContainerBox(width, 100f, ContainerKind.InlineSize) { FontSize = 20f, RootFontSize = 10f };

        Assert.True(ContainerQuery.TryEvaluate(condition, box, out var matches, out var reason), reason);
        Assert.Equal(expected, matches);
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
