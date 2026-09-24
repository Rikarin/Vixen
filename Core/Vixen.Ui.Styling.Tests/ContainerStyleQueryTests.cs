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
    // Mixed with a size feature, which makes the container the nearest SIZE container rather than
    // the parent. `ContainerScopes` holds that element as a box and the cascade holds it as a style,
    // and neither holds both. Named or not.
    [InlineData("@container (min-width: 400px) and style(--variant: primary) { .leaf { color: x } }", "size container")]
    [InlineData("@container card (min-width: 400px) and style(--variant: primary) { .leaf { color: x } }", "size container")]
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
    [Fact]
    public void The_nearest_named_ancestor_answers() {
        var (near, leaf) = Chain(["card", "secondary"], ["card", "primary"], ["plain"], ["leaf"]);
        Assert.Equal("named", near.Read(leaf, "color"));

        var (far, farLeaf) = Chain(["card", "primary"], ["card", "secondary"], ["plain"], ["leaf"]);
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
}
