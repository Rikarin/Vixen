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
    // ⚠ Named: the nearest ancestor CALLED `card` can be several levels above the parent, and the
    // incremental updater stops descending where a parent's style did not move — so a grandparent's
    // change would never reach the element asking. Refused rather than answered stale.
    [InlineData("@container card style(--variant: primary) { .leaf { color: x } }", "named")]
    // Mixed with a size feature, which makes the container the nearest SIZE container rather than
    // the parent: the same staleness.
    [InlineData("@container (min-width: 400px) and style(--variant: primary) { .leaf { color: x } }", "size container")]
    // A standard property, which no engine answers either and which this cascade has no computed
    // value to compare for without re-deriving one.
    [InlineData("@container style(color: red) { .leaf { color: x } }", "custom properties")]
    // `or` and `not`, which neither query grammar here has.
    [InlineData("@container style(--a: 1) or style(--b: 1) { .leaf { color: x } }", "'or'")]
    [InlineData("@container not style(--a: 1) { .leaf { color: x } }", "'not'")]
    public void A_style_query_this_cascade_cannot_answer_is_a_diagnostic(string css, string because) {
        var fixture = new CascadeFixture();
        fixture.Load(css);

        Assert.Contains(fixture.Engine.Loader.Diagnostics, diagnostic => diagnostic.Reason.Contains(because, StringComparison.Ordinal));

        var parent = fixture.Tree.CreateElement("div");
        var leaf = fixture.Tree.CreateElement("div", parent, classNames: ["leaf"]);
        Assert.Null(fixture.Value(leaf, parent: fixture.Engine.Resolver.Resolve(fixture.Tree, parent)));
    }
}
