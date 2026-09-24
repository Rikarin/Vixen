// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary><c>@container style(--x: y)</c>: a query about a custom property's value on the parent.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A different subject from a size query, and the difference is the whole design.</b> A
///         size query asks a <i>box</i> that a layout pass measured, which is why its verdicts hang
///         off <see cref="ContainerScopes" />; a style query asks a <i>computed value</i>, and CSS
///         Conditional 5 makes every element a style container, so the unnamed form is about the
///         parent — whose resolved style the cascade already holds when it resolves the child. So
///         it is answered in the cascade, per element, against that style, and never touches a
///         scope.
///     </para>
///     <para>
///         Every case is a pair — the same rule under a parent whose value matches and one whose
///         value does not — because a rule that applied unconditionally, or never, passes half of
///         them.
///     </para>
/// </remarks>
public class ContainerStyleQueryTests {
    const string Sheet = """
        .one { --variant: primary; }
        .two { --variant: secondary; }
        .spaced { --variant:   primary  ; }
        .flag { --flag: on; }
        @container style(--variant: primary) { .leaf { color: styled; } }
        @container style(--flag) { .leaf { background-color: flagged; } }
        @container style(--variant: primary) and style(--flag: on) { .leaf { border-color: both; } }
        """;

    static (CascadeFixture Fixture, StyleNodeId Leaf, ComputedStyle Parent) Scene(params string[] parentClasses) {
        var fixture = new CascadeFixture();
        fixture.Load(Sheet);

        var parent = fixture.Tree.CreateElement("div", classNames: parentClasses);
        var leaf = fixture.Tree.CreateElement("div", parent, classNames: ["leaf"]);

        return (fixture, leaf, fixture.Engine.Resolver.Resolve(fixture.Tree, parent));
    }

    [Fact]
    public void The_sheet_loads_without_a_diagnostic() {
        // ⚠ The parser is half the path: ExCSS hands the prelude over as `ConditionText`, and a
        // `style(` it swallowed or split would leave every verdict below answering a group that was
        // never registered.
        var fixture = new CascadeFixture();
        fixture.Load(Sheet);

        Assert.Empty(fixture.Engine.Loader.Diagnostics);
    }

    [Fact]
    public void A_style_query_asks_the_parents_value() {
        var (matching, leaf, parent) = Scene("one");
        Assert.Equal("styled", matching.Value(leaf, parent: parent));

        var (other, otherLeaf, otherParent) = Scene("two");
        Assert.Null(other.Value(otherLeaf, parent: otherParent));

        // And a parent with no value at all, which CSS reads as the guaranteed-invalid initial value
        // and never as a match.
        var (bare, bareLeaf, bareParent) = Scene();
        Assert.Null(bare.Value(bareLeaf, parent: bareParent));
    }

    [Fact]
    public void A_value_compares_as_its_tokens_and_not_its_spacing() {
        var (fixture, leaf, parent) = Scene("spaced");

        Assert.Equal("styled", fixture.Value(leaf, parent: parent));
    }

    /// <summary>
    ///     A comment in a style query's prelude is ignored. The prelude is read off the source text,
    ///     and the comment used to be read as part of it.
    /// </summary>
    [Theory]
    [InlineData("/* c */ style(--variant: primary)")]
    [InlineData("style(--variant: /* c */ primary)")]
    [InlineData("style(--variant: primary) /* c */ and style(--flag: on)")]
    public void A_comment_in_a_style_query_prelude_is_ignored(string prelude) {
        var fixture = new CascadeFixture();
        fixture.Load($$"""
            .one { --variant: primary; --flag: on; }
            .two { --variant: secondary; --flag: on; }
            @container {{prelude}} { .leaf { color: styled; } }
            """);

        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var one = fixture.Tree.CreateElement("div", classNames: ["one"]);
        var two = fixture.Tree.CreateElement("div", classNames: ["two"]);
        var matching = fixture.Tree.CreateElement("div", one, classNames: ["leaf"]);
        var other = fixture.Tree.CreateElement("div", two, classNames: ["leaf"]);
        var styles = fixture.Engine.ResolveAll();

        Assert.Equal("styled", fixture.Read(styles[matching.Index], "color"));
        Assert.Null(fixture.Read(styles[other.Index], "color"));
    }

    [Fact]
    public void The_bare_form_asks_whether_the_property_has_a_value() {
        var (flagged, leaf, parent) = Scene("flag");
        Assert.Equal("flagged", flagged.Value(leaf, "background-color", parent));

        var (bare, bareLeaf, bareParent) = Scene("one");
        Assert.Null(bare.Value(bareLeaf, "background-color", bareParent));
    }

    [Fact]
    public void Two_style_features_conjoin() {
        var (both, leaf, parent) = Scene("one", "flag");
        Assert.Equal("both", both.Value(leaf, "border-color", parent));

        var (half, halfLeaf, halfParent) = Scene("one");
        Assert.Null(half.Value(halfLeaf, "border-color", halfParent));
    }

    [Fact]
    public void An_inherited_value_answers_the_same_as_a_declared_one() {
        // Custom properties inherit, so the grandparent's value is the parent's computed value —
        // which is what the query is about, not where it was written.
        var fixture = new CascadeFixture();
        fixture.Load(Sheet);

        var grandparent = fixture.Tree.CreateElement("div", classNames: ["one"]);
        var parent = fixture.Tree.CreateElement("div", grandparent);
        var leaf = fixture.Tree.CreateElement("div", parent, classNames: ["leaf"]);

        var resolvedGrandparent = fixture.Engine.Resolver.Resolve(fixture.Tree, grandparent);
        var resolvedParent = fixture.Engine.Resolver.Resolve(fixture.Tree, parent, resolvedGrandparent);

        Assert.Equal("styled", fixture.Value(leaf, parent: resolvedParent));
    }

    [Fact]
    public void An_element_does_not_answer_its_own_style_query() {
        // ⚠ The container is an ANCESTOR. An element declaring `--variant: primary` itself, under a
        // parent that does not, must not see the rule — the defect a size query's own wiring was
        // written against, arriving by the other door.
        var fixture = new CascadeFixture();
        fixture.Load(Sheet);

        var parent = fixture.Tree.CreateElement("div");
        var leaf = fixture.Tree.CreateElement("div", parent, classNames: ["leaf", "one"]);

        Assert.Null(fixture.Value(leaf, parent: fixture.Engine.Resolver.Resolve(fixture.Tree, parent)));

        // And a root has no container at all, which CSS resolves to false.
        var root = fixture.Tree.CreateElement("div", classNames: ["leaf", "one"]);
        Assert.Null(fixture.Value(root));
    }

    [Theory]
    // Mixed with a size feature by `or` is answered now (#273, `Or_across_the_halves_…` below), but
    // not mixed with `and` as well: that is the precedence rule again, across the halves.
    [InlineData("@container (min-width: 400px) or style(--a: 1) and style(--b: 1) { .leaf { color: x } }", "mixed without parentheses")]
    // A size half that does not read is refused for the reason a size query alone would be.
    [InlineData("@container (min-width: 30furlongs) and style(--variant: primary) { .leaf { color: x } }", "30furlongs")]
    // `not` over a mixed list is the list rule, whichever half it would negate.
    [InlineData("@container not style(--a: 1) and (min-width: 400px) { .leaf { color: x } }", "'not' applies to one feature")]
    // A standard property, which no engine answers either and which this cascade has no computed
    // value to compare for without re-deriving one.
    [InlineData("@container style(color: red) { .leaf { color: x } }", "custom properties")]
    // ⚠ `and` and `or` have no precedence over each other in CSS Conditional 5, so mixing them without
    // parentheses is invalid rather than read one way. A parenthesised group is refused as such.
    [InlineData("@container style(--a: 1) and style(--b: 1) or style(--c: 1) { .leaf { color: x } }", "mixed without parentheses")]
    [InlineData("@container (style(--a: 1) or style(--b: 1)) and style(--c: 1) { .leaf { color: x } }", "parenthesised group")]
    // `not` negates one query in parentheses, never a list.
    [InlineData("@container not style(--a: 1) and style(--b: 1) { .leaf { color: x } }", "'not' applies to one feature")]
    // A name with nothing to ask, and a word CSS reserves.
    [InlineData("@container none style(--a: 1) { .leaf { color: x } }", "cannot be a container name")]
    public void A_style_query_this_cascade_cannot_answer_is_a_diagnostic(string css, string because) {
        var fixture = new CascadeFixture();
        fixture.Load(css);

        Assert.Contains(fixture.Engine.Loader.Diagnostics, diagnostic => diagnostic.Reason.Contains(because, StringComparison.Ordinal));

        var parent = fixture.Tree.CreateElement("div");
        var leaf = fixture.Tree.CreateElement("div", parent, classNames: ["leaf"]);
        Assert.Null(fixture.Value(leaf, parent: fixture.Engine.Resolver.Resolve(fixture.Tree, parent)));
    }

    /// <summary>The named form, the joins and the negation, over one sheet.</summary>
    /// <remarks>
    ///     <c>.card</c> is named and <c>.panel</c> is named twice, because <c>container-name</c> is a
    ///     list. <c>.shadow</c> sets <c>--variant</c> without a name, which is how a named query differs
    ///     from an unnamed one: the unnamed form reads the parent, whatever it is called.
    /// </remarks>
    const string Named = """
        .card { container-name: card; }
        .panel { container: side card-like / normal; }
        .primary { --variant: primary; }
        .secondary { --variant: secondary; }
        .flag { --flag: on; }
        @container card style(--variant: primary) { .leaf { color: named; } }
        @container card-like style(--variant: primary) { .leaf { background-color: listed; } }
        @container style(--variant: primary) or style(--flag: on) { .leaf { border-color: either; } }
        @container not style(--variant: primary) { .leaf { outline-color: not-primary; } }
        """;

    /// <summary>Resolves a chain of elements top down with the engine, and returns the last one's style.</summary>
    static (CascadeFixture Fixture, ComputedStyle Leaf) Chain(params string[][] classes) {
        var fixture = new CascadeFixture();
        fixture.Load(Named);

        StyleNodeId? at = null;

        foreach (var names in classes) {
            at = fixture.Tree.CreateElement("div", at, classNames: names);
        }

        return (fixture, fixture.Engine.ResolveAll()[at!.Value.Index]);
    }

    [Fact]
    public void The_named_sheet_loads_without_a_diagnostic() {
        var fixture = new CascadeFixture();
        fixture.Load(Named);

        Assert.Empty(fixture.Engine.Loader.Diagnostics);
    }

    /// <summary>
    ///     ⚠ A named query asks the nearest ancestor with that name, over an element between them
    ///     that says something else (#273).
    /// </summary>
    /// <remarks>
    ///     The middle element sets <c>--variant: secondary</c>, so the parent's value is
    ///     <c>secondary</c> and the unnamed reading would say no. The named card says
    ///     <c>primary</c>. Reversed, the answer reverses.
    /// </remarks>
    [Fact]
    public void A_named_query_asks_the_named_ancestor_and_not_the_parent() {
        var (matching, leaf) = Chain(["card", "primary"], ["secondary"], ["leaf"]);
        Assert.Equal("named", matching.Read(leaf, "color"));

        var (other, otherLeaf) = Chain(["card", "secondary"], ["primary"], ["leaf"]);
        Assert.Null(other.Read(otherLeaf, "color"));

        // No ancestor called `card` is no container, which is false and never an error.
        var (unnamed, unnamedLeaf) = Chain(["primary"], ["leaf"]);
        Assert.Null(unnamed.Read(unnamedLeaf, "color"));
    }

    /// <summary>The nearest one wins, and an unnamed ancestor between them is skipped over.</summary>
    /// <remarks>
    ///     ⚠ The unnamed ancestor between them declares the opposite value. With a <c>plain</c> one
    ///     there it inherited the nearer card's, so an evaluator reading the parent gave the same
    ///     answer as one reading the nearest card, and both rows stayed green when the lookup was
    ///     replaced by the parent. Each row now has three distinct answers: the nearest card's, the
    ///     farther card's and the parent's, and only the first is right.
    /// </remarks>
    [Fact]
    public void The_nearest_named_ancestor_answers() {
        var (near, leaf) = Chain(["card", "secondary"], ["card", "primary"], ["secondary"], ["leaf"]);
        Assert.Equal("named", near.Read(leaf, "color"));

        var (far, farLeaf) = Chain(["card", "primary"], ["card", "secondary"], ["primary"], ["leaf"]);
        Assert.Null(far.Read(farLeaf, "color"));
    }

    [Fact]
    public void A_container_name_is_a_list_and_the_shorthand_names_too() {
        var (listed, leaf) = Chain(["panel", "primary"], ["leaf"]);
        Assert.Equal("listed", listed.Read(leaf, "background-color"));

        // `card-like` is not `card`: a name matches whole.
        Assert.Null(listed.Read(leaf, "color"));
    }

    [Fact]
    public void Or_holds_when_either_feature_does() {
        var (primary, primaryLeaf) = Chain(["primary"], ["leaf"]);
        Assert.Equal("either", primary.Read(primaryLeaf, "border-color"));

        var (flagged, flaggedLeaf) = Chain(["flag"], ["leaf"]);
        Assert.Equal("either", flagged.Read(flaggedLeaf, "border-color"));

        var (neither, neitherLeaf) = Chain(["secondary"], ["leaf"]);
        Assert.Null(neither.Read(neitherLeaf, "border-color"));
    }

    [Fact]
    public void Not_negates_the_feature_but_not_the_absence_of_a_container() {
        var (secondary, leaf) = Chain(["secondary"], ["leaf"]);
        Assert.Equal("not-primary", secondary.Read(leaf, "outline-color"));

        var (primary, primaryLeaf) = Chain(["primary"], ["leaf"]);
        Assert.Null(primary.Read(primaryLeaf, "outline-color"));

        // ⚠ A root has no container at all. That query is unknown, and unknown is not negated into a
        // match: `not` over no container matches nothing.
        var (root, rootLeaf) = Chain(["leaf"]);
        Assert.Null(root.Read(rootLeaf, "outline-color"));
    }

    /// <summary>
    ///     ⚠ The incremental half: a named ancestor's value changes, an element between it and the
    ///     asker overrides the property, and the asker still re-answers.
    /// </summary>
    /// <remarks>
    ///     The middle element's inherited portion does not move when the card's does, because it
    ///     declares <c>--variant</c> itself. <c>StyleUpdater</c> stops descending at exactly that kind
    ///     of element, and it is the reason the named form was refused. So the updater re-resolves a
    ///     named element's whole subtree when its style moves. Removing that edge leaves the leaf on
    ///     the answer it had before the class changed.
    /// </remarks>
    [Fact]
    public void A_named_ancestors_change_reaches_an_asker_below_an_element_that_overrides_it() {
        var fixture = new CascadeFixture();
        fixture.Load(Named);

        var card = fixture.Tree.CreateElement("div", classNames: ["card", "secondary"]);
        var middle = fixture.Tree.CreateElement("div", card, classNames: ["secondary"]);
        var leaf = fixture.Tree.CreateElement("div", middle, classNames: ["leaf"]);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Null(fixture.Read(updater.StyleOf(leaf), "color"));

        fixture.Tree.RemoveClass(card, "secondary");
        fixture.Tree.AddClass(card, "primary");
        updater.ClassChanged(card, "secondary", "primary");

        Assert.Equal("named", fixture.Read(updater.StyleOf(leaf), "color"));

        // And back, which the same edge has to carry the other way.
        fixture.Tree.RemoveClass(card, "primary");
        fixture.Tree.AddClass(card, "secondary");
        updater.ClassChanged(card, "secondary", "primary");

        Assert.Null(fixture.Read(updater.StyleOf(leaf), "color"));
    }

    /// <summary>An element that gains a name becomes a container its subtree's queries find.</summary>
    [Fact]
    public void An_ancestor_that_gains_a_name_is_found_on_the_next_pass() {
        var fixture = new CascadeFixture();
        fixture.Load(Named);

        var outer = fixture.Tree.CreateElement("div", classNames: ["primary"]);
        var middle = fixture.Tree.CreateElement("div", outer, classNames: ["secondary"]);
        var leaf = fixture.Tree.CreateElement("div", middle, classNames: ["leaf"]);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Null(fixture.Read(updater.StyleOf(leaf), "color"));

        fixture.Tree.AddClass(outer, "card");
        updater.ClassChanged(outer, "card");

        Assert.Equal("named", fixture.Read(updater.StyleOf(leaf), "color"));
    }

    /// <summary>
    ///     Resolving one element by hand, with no pass holding its ancestors, still finds the named
    ///     one: the resolver cascades the chain itself.
    /// </summary>
    [Fact]
    public void A_named_query_answers_when_the_leaf_is_resolved_alone() {
        var fixture = new CascadeFixture();
        fixture.Load(Named);

        var card = fixture.Tree.CreateElement("div", classNames: ["card", "primary"]);
        var middle = fixture.Tree.CreateElement("div", card, classNames: ["secondary"]);
        var leaf = fixture.Tree.CreateElement("div", middle, classNames: ["leaf"]);

        Assert.Equal("named", fixture.Value(leaf));
    }

    /// <summary>A chain of <paramref name="depth" /> elements each with a sibling, under a sheet with one named <c>style()</c> rule.</summary>
    /// <returns>The fixture, after one whole-document resolve, and the deepest element.</returns>
    static (CascadeFixture Fixture, StyleNodeId Deepest) DeepTree(string sheet, int depth, string[] deepestClasses) {
        var fixture = new CascadeFixture();
        fixture.Load(sheet);

        var at = fixture.Tree.CreateElement("div", classNames: ["card", "primary"]);

        for (var i = 1; i < depth; i++) {
            fixture.Tree.CreateElement("span", at, classNames: ["item"]);
            at = fixture.Tree.CreateElement("div", at, classNames: i == depth - 1 ? deepestClasses : ["item"]);
        }

        return (fixture, at);
    }

    /// <summary>
    ///     ⚠ A named <c>style()</c> query that no element's candidates include collects no ancestor
    ///     chain, however deep the tree (#1421).
    /// </summary>
    /// <remarks>
    ///     The resolver used to key the collection on the document-wide flag, so one named query in any
    ///     sheet made every element of every restyle allocate its whole ancestor chain. That was
    ///     <c>elements × depth</c> slots. The count is a deterministic counter, not a time. Every
    ///     element here is cascaded, as <c>Cascades</c> shows, so the old code collected once per
    ///     element and this one collects nothing, because no element is a <c>.never</c>.
    /// </remarks>
    [Fact]
    public void A_named_style_query_no_candidate_reaches_collects_no_ancestor_chain() {
        const string sheet = """
            .card { container-name: card; }
            .primary { --variant: primary; }
            .item { color: plain; }
            @container card style(--variant: primary) { .never { color: named; } }
            """;

        var (fixture, deepest) = DeepTree(sheet, 24, ["item"]);
        var resolver = fixture.Engine.Resolver;
        var before = resolver.Cascades;
        var styles = fixture.Engine.ResolveAll();

        Assert.True(resolver.Cascades - before >= 24, $"only {resolver.Cascades - before} cascades ran");
        Assert.Equal("plain", fixture.Read(styles[deepest.Index], "color"));
        Assert.Equal(0, resolver.AncestorCollections);
    }

    /// <summary>
    ///     And an element whose candidates do include such a rule still collects its chain, once, and
    ///     is answered from it: the laziness is per element, not a switch that turned the query off.
    /// </summary>
    [Fact]
    public void Only_the_element_a_named_style_rule_can_reach_collects_its_chain() {
        const string sheet = """
            .card { container-name: card; }
            .primary { --variant: primary; }
            .item { color: plain; }
            @container card style(--variant: primary) { .leaf { color: named; } }
            """;

        var (fixture, deepest) = DeepTree(sheet, 24, ["leaf"]);
        var styles = fixture.Engine.ResolveAll();

        Assert.Equal("named", fixture.Read(styles[deepest.Index], "color"));
        Assert.Equal(1, fixture.Engine.Resolver.AncestorCollections);
    }

    /// <summary>
    ///     ⚠ A size query nested in a named <c>style()</c> query still collects the chain its outer
    ///     group needs: whether a group asks for ancestors is inherited from the group it sits in
    ///     (#1421).
    /// </summary>
    /// <remarks>
    ///     The rule carries only the inner size group, so the per-element gate asks that group, and a
    ///     size group on its own asks nothing of the ancestor chain. Unless <c>Register</c> copies the
    ///     answer down from the enclosing group, the gate collects no chain, the named style half
    ///     searches an empty span, and the rule never applies. Each row is paired with one where
    ///     exactly one half refuses, so a rule that applied unconditionally fails too.
    /// </remarks>
    [Theory]
    [InlineData("primary", 500f, "nested")]
    [InlineData("secondary", 500f, null)]
    [InlineData("primary", 0f, null)]
    public void A_size_query_nested_in_a_named_style_query_collects_the_chain(
        string variant,
        float width,
        string? expected
    ) {
        const string sheet = """
            .card { container-name: card; }
            .sized { container-type: inline-size; }
            .primary { --variant: primary; }
            .secondary { --variant: secondary; }
            @container card style(--variant: primary) { @container (min-width: 1px) { .leaf { color: nested; } } }
            """;

        var fixture = new CascadeFixture();
        fixture.Load(sheet);
        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var card = fixture.Tree.CreateElement("div", classNames: ["card", variant]);
        var box = fixture.Tree.CreateElement("div", card, classNames: ["sized"]);
        fixture.Contain(box, width);

        var middle = fixture.Tree.CreateElement("div", box);
        var leaf = fixture.Tree.CreateElement("div", middle, classNames: ["leaf"]);
        var styles = fixture.Engine.ResolveAll();

        Assert.Equal(expected, fixture.Read(styles[leaf.Index], "color"));
    }

    /// <summary>
    ///     The mixed form: a size feature and a <c>style()</c> feature joined by <c>and</c>, both asked
    ///     of one box, the nearest <i>size</i> container (#273).
    /// </summary>
    /// <remarks>
    ///     CSS Conditional 5: a query's container is the nearest ancestor eligible for every feature in
    ///     it, and only a size container is eligible for <c>(min-width: …)</c>. So the style half is
    ///     asked of that container and never of the parent. A <c>.sized</c> element is both declared
    ///     a size container in the sheet, for the cascade, and entered into the scope chain with
    ///     <see cref="CascadeFixture.Contain" />, for the size half, as a layout pass would do.
    /// </remarks>
    const string Mixed = """
        .sized { container-type: inline-size; }
        .card { container-name: card; }
        .primary { --variant: primary; }
        .secondary { --variant: secondary; }
        @container (min-width: 400px) and style(--variant: primary) { .leaf { color: mixed; } }
        @container style(--variant: primary) and (min-width: 400px) { .leaf { border-color: reversed; } }
        @container card (min-width: 400px) and style(--variant: primary) { .leaf { background-color: named-mixed; } }
        """;

    /// <summary>A container of a given width and classes, one element between it and the leaf, and the leaf's style.</summary>
    static (CascadeFixture Fixture, ComputedStyle Leaf) MixedScene(float width, string[] container, string[] between) {
        var fixture = new CascadeFixture();
        fixture.Load(Mixed);

        var box = fixture.Tree.CreateElement("div", classNames: container);
        fixture.Contain(box, width, name: container.Contains("card") ? "card" : "");

        var middle = fixture.Tree.CreateElement("div", box, classNames: between);
        var leaf = fixture.Tree.CreateElement("div", middle, classNames: ["leaf"]);

        return (fixture, fixture.Engine.ResolveAll()[leaf.Index]);
    }

    [Fact]
    public void The_mixed_sheet_loads_without_a_diagnostic() {
        var fixture = new CascadeFixture();
        fixture.Load(Mixed);

        Assert.Empty(fixture.Engine.Loader.Diagnostics);
    }

    /// <summary>Each half alone is not enough, and the order the two are written in does not matter.</summary>
    [Theory]
    [InlineData(900f, "primary", "mixed")]
    [InlineData(300f, "primary", null)]
    [InlineData(900f, "secondary", null)]
    [InlineData(300f, "secondary", null)]
    public void A_mixed_query_holds_only_when_both_halves_do(float width, string variant, string? expected) {
        var (fixture, leaf) = MixedScene(width, ["sized", variant], []);

        Assert.Equal(expected, fixture.Read(leaf, "color"));
        Assert.Equal(expected is null ? null : "reversed", fixture.Read(leaf, "border-color"));
    }

    /// <summary>
    ///     ⚠ The style half asks the size container and not the parent: the element between them
    ///     declares the opposite value, so an evaluator reading the parent answers every row wrongly.
    /// </summary>
    [Fact]
    public void The_style_half_asks_the_size_container_and_not_the_parent() {
        var (container, leaf) = MixedScene(900f, ["sized", "primary"], ["secondary"]);
        Assert.Equal("mixed", container.Read(leaf, "color"));

        var (parent, parentLeaf) = MixedScene(900f, ["sized", "secondary"], ["primary"]);
        Assert.Null(parent.Read(parentLeaf, "color"));
    }

    /// <summary>
    ///     A named mixed query asks the nearest size container carrying the name, past a nearer
    ///     unnamed size container, and an element that only carries the name is not a size container.
    /// </summary>
    [Fact]
    public void A_named_mixed_query_asks_the_named_size_container() {
        var fixture = new CascadeFixture();
        fixture.Load(Mixed);

        var outer = fixture.Tree.CreateElement("div", classNames: ["sized", "card", "primary"]);
        fixture.Contain(outer, 900f, name: "card");

        var inner = fixture.Tree.CreateElement("div", outer, classNames: ["sized", "secondary"]);
        fixture.Contain(inner, 900f);

        var leaf = fixture.Tree.CreateElement("div", inner, classNames: ["leaf"]);
        var style = fixture.Engine.ResolveAll()[leaf.Index];

        // The named query asks `outer`, which is primary; the unnamed one asks `inner`, which is not.
        Assert.Equal("named-mixed", fixture.Read(style, "background-color"));
        Assert.Null(fixture.Read(style, "color"));

        // A `card` with no container-type is a style container only, so the named mixed query has
        // no eligible container and is false, even though a named style query alone would find it.
        // ⚠ `MixedScene` enters the box into the chain whatever its classes say, which a live
        // document would not, so the size half holds here and only the style half can refuse it:
        // this row is about the style half's eligibility rule on its own.
        var (plain, plainLeaf) = MixedScene(900f, ["card", "primary"], []);
        Assert.Null(plain.Read(plainLeaf, "background-color"));
    }

    /// <summary>
    ///     ⚠ <c>or</c> across the halves holds when either does, and both halves still ask the one
    ///     size container (#273).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This was refused as having "no single place to be answered". CSS Containment 3 § 5.1
    ///         gives it one: the nearest container eligible for every feature, the element the
    ///         <c>and</c> form asks. The size group is registered beside the style group, and the
    ///         cascade reads its verdict as a disjunct.
    ///     </para>
    ///     <para>
    ///         The fourth row puts the opposite value on the element between, so a style half that
    ///         read the parent would hold there. The last two rows have no size container at all,
    ///         only a <c>primary</c> parent: that query is unknown and does not apply, which a style
    ///         half that fell back to the parent would get wrong.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(900f, "secondary", "", true)]
    [InlineData(300f, "primary", "", true)]
    [InlineData(300f, "secondary", "", false)]
    [InlineData(300f, "secondary", "primary", false)]
    [InlineData(300f, "primary", "secondary", true)]
    public void Or_across_the_halves_holds_when_either_half_does(float width, string variant, string between, bool holds) {
        const string sheet = """
            .sized { container-type: inline-size; }
            .card { container-name: card; }
            .primary { --variant: primary; }
            .secondary { --variant: secondary; }
            @container (min-width: 400px) or style(--variant: primary) { .leaf { color: either; } }
            @container style(--variant: primary) or (min-width: 400px) { .leaf { border-color: reversed; } }
            @container card (min-width: 400px) or style(--variant: primary) { .leaf { background-color: named-either; } }
            """;

        var fixture = new CascadeFixture();
        fixture.Load(sheet);

        Assert.Empty(fixture.Engine.Loader.Diagnostics);

        var box = fixture.Tree.CreateElement("div", classNames: ["sized", "card", variant]);
        fixture.Contain(box, width, name: "card");

        var middle = fixture.Tree.CreateElement("div", box, classNames: between.Length == 0 ? [] : [between]);
        var leaf = fixture.Tree.CreateElement("div", middle, classNames: ["leaf"]);
        var style = fixture.Engine.ResolveAll()[leaf.Index];

        Assert.Equal(holds ? "either" : null, fixture.Read(style, "color"));
        Assert.Equal(holds ? "reversed" : null, fixture.Read(style, "border-color"));
        Assert.Equal(holds ? "named-either" : null, fixture.Read(style, "background-color"));
    }

    /// <summary>With no eligible container, <c>or</c> across the halves is unknown and does not apply.</summary>
    [Fact]
    public void Or_across_the_halves_with_no_size_container_does_not_apply() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            .primary { --variant: primary; }
            @container (min-width: 400px) or style(--variant: primary) { .leaf { color: either; } }
            """);

        var parent = fixture.Tree.CreateElement("div", classNames: ["primary"]);
        var leaf = fixture.Tree.CreateElement("div", parent, classNames: ["leaf"]);

        Assert.Null(fixture.Read(fixture.Engine.ResolveAll()[leaf.Index], "color"));
    }

    /// <summary>
    ///     ⚠ A mixed query whose size half reads the block axis asks the nearest <c>size</c> container
    ///     for both halves, past a nearer <c>inline-size</c> one (#1429).
    /// </summary>
    /// <remarks>
    ///     The inner box is an <c>inline-size</c> container declaring the opposite value. It cannot
    ///     answer <c>(min-height: …)</c>, so CSS Containment 3 § 5.1 skips it, and both halves have to
    ///     skip it: a size half that stopped there resolved false, and a style half that stopped there
    ///     read <c>secondary</c>. The second scene is the control, with the values swapped, so a style
    ///     half that read the inner box would hold there and the rule would wrongly apply.
    /// </remarks>
    [Fact]
    public void A_block_axis_mixed_query_asks_the_size_container_past_an_inline_size_one() {
        const string sheet = """
            .inline { container-type: inline-size; }
            .both { container-type: size; }
            .primary { --variant: primary; }
            .secondary { --variant: secondary; }
            @container (min-height: 200px) and style(--variant: primary) { .leaf { color: tall-primary; } }
            """;

        var asked = new CascadeFixture();
        asked.Load(sheet);
        Assert.Empty(asked.Engine.Loader.Diagnostics);
        Assert.Equal("tall-primary", asked.Read(Leaf(asked, "primary", "secondary"), "color"));

        var swapped = new CascadeFixture();
        swapped.Load(sheet);
        Assert.Null(swapped.Read(Leaf(swapped, "secondary", "primary"), "color"));

        static ComputedStyle Leaf(CascadeFixture fixture, string outerVariant, string innerVariant) {
            var outer = fixture.Tree.CreateElement("div", classNames: ["both", outerVariant]);
            fixture.Contain(outer, 900f, 900f, kind: ContainerKind.Size);

            var inner = fixture.Tree.CreateElement("div", outer, classNames: ["inline", innerVariant]);
            fixture.Contain(inner, 900f, 50f, kind: ContainerKind.InlineSize);

            var leaf = fixture.Tree.CreateElement("div", inner, classNames: ["leaf"]);
            return fixture.Engine.ResolveAll()[leaf.Index];
        }
    }

    /// <summary>
    ///     ⚠ The size container's own change reaches an asker below an element that overrides the
    ///     value, although the container carries no name.
    /// </summary>
    /// <remarks>
    ///     The edge a named container gets, re-resolving its whole subtree when its style moves, has
    ///     to cover every size container once a sheet declares a mixed query. The unnamed mixed query
    ///     asks the nearest size container, which need not have a name. The element between declares
    ///     <c>--variant: secondary</c>, so its inherited portion never moves and the ordinary walk stops
    ///     there.
    /// </remarks>
    [Fact]
    public void A_size_containers_change_reaches_an_asker_below_an_override() {
        var fixture = new CascadeFixture();
        fixture.Load(Mixed);

        var box = fixture.Tree.CreateElement("div", classNames: ["sized", "secondary"]);
        fixture.Contain(box, 900f);

        var middle = fixture.Tree.CreateElement("div", box, classNames: ["secondary"]);
        var leaf = fixture.Tree.CreateElement("div", middle, classNames: ["leaf"]);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Null(fixture.Read(updater.StyleOf(leaf), "color"));

        fixture.Tree.RemoveClass(box, "secondary");
        fixture.Tree.AddClass(box, "primary");
        updater.ClassChanged(box, "secondary", "primary");

        Assert.Equal("mixed", fixture.Read(updater.StyleOf(leaf), "color"));

        fixture.Tree.RemoveClass(box, "primary");
        fixture.Tree.AddClass(box, "secondary");
        updater.ClassChanged(box, "secondary", "primary");

        Assert.Null(fixture.Read(updater.StyleOf(leaf), "color"));
    }
}
