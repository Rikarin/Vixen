// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>Inheritance, custom properties, and the interning the rest of the engine rests on.</summary>
public class ComputedStyleTests {
    [Fact]
    public void An_inherited_property_reaches_a_child_and_a_non_inherited_one_does_not() {
        var fixture = new CascadeFixture();
        fixture.Load(".panel { color: parent-colour; padding-left: 8px }");

        var panel = fixture.Tree.CreateElement("div", classNames: ["panel"]);
        var label = fixture.Tree.CreateElement("span", panel);

        var parent = fixture.Engine.Resolver.Resolve(fixture.Tree, panel);
        var child = fixture.Engine.Resolver.Resolve(fixture.Tree, label, parent);

        Assert.Equal("parent-colour", fixture.Read(child, "color"));

        // A panel whose every descendant also got its 8px of padding would be unusable, which is
        // the whole reason the inherited list is as short as it is.
        Assert.Null(fixture.Read(child, "padding-left"));
    }

    [Fact]
    public void A_child_that_sets_an_inherited_property_keeps_its_own() {
        var fixture = new CascadeFixture();
        fixture.Load(".panel { color: parent-colour } .label { color: own-colour }");

        var panel = fixture.Tree.CreateElement("div", classNames: ["panel"]);
        var label = fixture.Tree.CreateElement("span", panel, classNames: ["label"]);

        var parent = fixture.Engine.Resolver.Resolve(fixture.Tree, panel);
        var child = fixture.Engine.Resolver.Resolve(fixture.Tree, label, parent);

        Assert.Equal("own-colour", fixture.Read(child, "color"));
    }

    [Fact]
    public void Inheritance_reaches_a_grandchild_in_one_step_each() {
        // Each level reads its parent's *resolved* table, so a value travels down without anybody
        // climbing. Ten levels deep would otherwise be ten probes per property.
        var fixture = new CascadeFixture();
        fixture.Load(".root { color: deep }");

        var root = fixture.Tree.CreateElement("div", classNames: ["root"]);
        var middle = fixture.Tree.CreateElement("div", root);
        var leaf = fixture.Tree.CreateElement("span", middle);

        var styles = fixture.Engine.ResolveAll();

        Assert.Equal("deep", fixture.Read(styles[leaf.Index], "color"));
        Assert.Equal("deep", fixture.Read(styles[middle.Index], "color"));
        Assert.NotNull(root.ToString());
    }

    [Fact]
    public void Custom_properties_inherit_without_being_on_any_list() {
        // There is no finite set of them, so they are recognised by their name rather than looked up.
        var fixture = new CascadeFixture();
        fixture.Load(":is(.theme) { --accent: teal }");

        var theme = fixture.Tree.CreateElement("div", classNames: ["theme"]);
        var child = fixture.Tree.CreateElement("span", theme);

        var styles = fixture.Engine.ResolveAll();

        Assert.Equal("teal", fixture.Read(styles[child.Index], "--accent"));
    }

    [Fact]
    public void Var_resolves_against_a_custom_property_the_element_inherited() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            .theme { --accent: teal }
            .label { color: var(--accent) }
            """);

        fixture.Tree.CreateElement("div", classNames: ["theme"]);
        fixture.Tree.CreateElement("span", new StyleNodeId(0), classNames: ["label"]);

        var styles = fixture.Engine.ResolveAll();

        Assert.Equal("teal", fixture.Read(styles[1], "color"));
    }

    [Fact]
    public void An_element_can_override_a_custom_property_and_use_it_in_the_same_rule() {
        // Which is why substitution runs against the element's own resolved set and not its
        // parent's — resolving against the parent would give `teal` here.
        var fixture = new CascadeFixture();
        fixture.Load("""
            .theme { --accent: teal }
            .label { --accent: coral; color: var(--accent) }
            """);

        fixture.Tree.CreateElement("div", classNames: ["theme"]);
        fixture.Tree.CreateElement("span", new StyleNodeId(0), classNames: ["label"]);

        var styles = fixture.Engine.ResolveAll();

        Assert.Equal("coral", fixture.Read(styles[1], "color"));
    }

    [Fact]
    public void A_var_fallback_is_used_only_when_there_is_nothing_to_substitute() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            .a { color: var(--missing, fallback) }
            .b { --present: actual; color: var(--present, fallback) }
            """);

        var a = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var b = fixture.Tree.CreateElement("div", classNames: ["b"]);

        Assert.Equal("fallback", fixture.Value(a));
        Assert.Equal("actual", fixture.Value(b));
    }

    [Fact]
    public void Custom_properties_can_refer_to_each_other() {
        var fixture = new CascadeFixture();
        fixture.Load(".a { --base: teal; --accent: var(--base); color: var(--accent) }");

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);

        Assert.Equal("teal", fixture.Value(element));
    }

    [Fact]
    public void A_var_with_nothing_to_resolve_to_makes_the_declaration_invalid_rather_than_dropping_to_the_next_one() {
        // The distinction that matters, and the one nearly every naive implementation gets wrong.
        // `var(--missing)` does not fall back to the previous declaration in the cascade — the
        // property behaves as though nothing had set it, which for an inherited property means the
        // parent's value.
        var fixture = new CascadeFixture();
        fixture.Load("""
            .panel { color: inherited-colour }
            .label { color: would-have-won }
            .label { color: var(--missing) }
            """);

        fixture.Tree.CreateElement("div", classNames: ["panel"]);
        fixture.Tree.CreateElement("span", new StyleNodeId(0), classNames: ["label"]);

        var styles = fixture.Engine.ResolveAll();

        Assert.Equal("inherited-colour", fixture.Read(styles[1], "color"));
    }

    [Fact]
    public void A_cycle_of_custom_properties_does_not_hang() {
        var fixture = new CascadeFixture();
        fixture.Load(".a { --x: var(--y); --y: var(--x); color: var(--x) }");

        var element = fixture.Tree.CreateElement("div", classNames: ["a"]);

        Assert.Null(fixture.Value(element));
    }

    [Fact]
    public void Two_elements_that_resolve_alike_hold_the_same_object() {
        // The property everything downstream rests on: `ReferenceEquals` is a complete answer to
        // "did this element's style change".
        var fixture = new CascadeFixture();
        fixture.Load(".a { color: red } .b { color: red }");

        var first = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var second = fixture.Tree.CreateElement("span", classNames: ["b"]);

        var one = fixture.Engine.Resolver.Resolve(fixture.Tree, first);
        var other = fixture.Engine.Resolver.Resolve(fixture.Tree, second);

        Assert.Same(one, other);
    }

    [Fact]
    public void Interning_survives_a_property_set_arriving_in_a_different_order() {
        var fixture = new CascadeFixture();
        fixture.Load("""
            .a { color: red; background: blue }
            .b { background: blue; color: red }
            """);

        var first = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var second = fixture.Tree.CreateElement("div", classNames: ["b"]);

        Assert.Same(
            fixture.Engine.Resolver.Resolve(fixture.Tree, first),
            fixture.Engine.Resolver.Resolve(fixture.Tree, second)
        );
    }

    [Fact]
    public void Ten_thousand_identical_cells_produce_one_computed_style() {
        // Doc 09's claim, measured. Interning is what makes it true; sharing is what makes it cheap,
        // and the two are counted separately because either can regress without the other.
        var fixture = new CascadeFixture();
        fixture.Load(".grid { color: grid } .cell { color: cell; padding-left: 4px }");

        var grid = fixture.Tree.CreateElement("div", classNames: ["grid"]);
        for (var row = 0; row < 100; row++) {
            var line = fixture.Tree.CreateElement("div", grid, classNames: ["row"]);
            for (var column = 0; column < 100; column++) {
                fixture.Tree.CreateElement("div", line, classNames: ["cell"]);
            }
        }

        var styles = fixture.Engine.ResolveAll();

        Assert.Equal(10_000 + 100 + 1, styles.Length);

        var distinct = new HashSet<ComputedStyle>(ReferenceEqualityComparer.Instance);
        foreach (var style in styles) {
            distinct.Add(style);
        }

        // Two, not three. No rule matches a row, so a row's style is nothing but the `color` it
        // inherited from the grid — which makes it byte-for-byte the grid's style and the same
        // object. Interning compares what a style *is*, never where it came from, and this is what
        // that buys: ten thousand and one elements, two objects.
        Assert.Equal(2, distinct.Count);
        Assert.Equal(2, fixture.Engine.Interning.Count);

        // And the cascade ran a hundred and two times for ten thousand and one elements: the grid,
        // one row (the other ninety-nine share with it), and one cell per row. Cells share only
        // within a row because the key holds the parent *element* — see StyleSharingKey for why it
        // cannot hold the parent's style instead.
        Assert.Equal(102, fixture.Engine.Resolver.Cascades);
        Assert.Equal(9_999, fixture.Engine.Resolver.SharingHits);
    }

    [Fact]
    public void An_element_no_rule_matched_has_an_empty_style_and_still_interns() {
        var fixture = new CascadeFixture();
        fixture.Load(".nothing-matches-this { color: red }");

        var first = fixture.Tree.CreateElement("div");
        var second = fixture.Tree.CreateElement("span");

        var one = fixture.Engine.Resolver.Resolve(fixture.Tree, first);

        Assert.Equal(0, one.Count);
        Assert.Same(one, fixture.Engine.Resolver.Resolve(fixture.Tree, second));
    }
}
