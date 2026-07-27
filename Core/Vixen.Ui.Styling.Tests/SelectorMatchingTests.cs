// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>What each kind of selector means, one at a time.</summary>
/// <remarks>
///     The oracle test says the fast path and the slow path agree. It cannot say either of them is
///     right about CSS. These are what say that.
/// </remarks>
public class SelectorMatchingTests {
    [Fact]
    public void The_simple_selectors_each_test_what_they_name() {
        var fixture = new StyleFixture();
        var root = fixture.Tree.CreateElement("div", id: "app", classNames: ["panel", "dark"]);
        fixture.Tree.SetAttribute(root, "data-role", "container");

        Assert.True(fixture.Matches("*", root));
        Assert.True(fixture.Matches("div", root));
        Assert.True(fixture.Matches("#app", root));
        Assert.True(fixture.Matches(".panel", root));
        Assert.True(fixture.Matches(".dark", root));
        Assert.True(fixture.Matches("div#app.panel.dark", root));
        Assert.True(fixture.Matches("[data-role]", root));
        Assert.True(fixture.Matches("[data-role=container]", root));

        Assert.False(fixture.Matches("span", root));
        Assert.False(fixture.Matches("#other", root));
        Assert.False(fixture.Matches(".light", root));
        Assert.False(fixture.Matches("div.panel.missing", root));
        Assert.False(fixture.Matches("[data-role=other]", root));
        Assert.False(fixture.Matches("[data-missing]", root));
    }

    [Fact]
    public void Tag_names_are_case_sensitive_because_VXML_makes_case_meaningful() {
        // docs/plan/09: a PascalCase tag is a component and a lowercase one is an intrinsic element.
        // Folding case would make `Button` and `button` the same selector, which they are not.
        var fixture = new StyleFixture();
        var component = fixture.Tree.CreateElement("Button");
        var intrinsic = fixture.Tree.CreateElement("button");

        Assert.True(fixture.Matches("Button", component));
        Assert.False(fixture.Matches("button", component));
        Assert.True(fixture.Matches("button", intrinsic));
        Assert.False(fixture.Matches("Button", intrinsic));
    }

    [Fact]
    public void The_attribute_operators_each_compare_the_way_CSS_says() {
        var fixture = new StyleFixture();
        var element = fixture.Tree.CreateElement("div");
        fixture.Tree.SetAttribute(element, "lang", "en-GB");
        fixture.Tree.SetAttribute(element, "rel", "next prev up");

        Assert.True(fixture.Matches("[lang^=en]", element));
        Assert.True(fixture.Matches("[lang$=GB]", element));
        Assert.True(fixture.Matches("[lang*=n-G]", element));
        Assert.True(fixture.Matches("[lang|=en]", element));
        Assert.True(fixture.Matches("[rel~=prev]", element));

        Assert.False(fixture.Matches("[lang|=e]", element));
        Assert.False(fixture.Matches("[rel~=nex]", element));
        Assert.False(fixture.Matches("[lang^=fr]", element));
    }

    [Fact]
    public void The_combinators_each_reach_the_element_they_name() {
        var fixture = new StyleFixture();
        var root = fixture.Tree.CreateElement("div", classNames: ["app"]);
        var sidebar = fixture.Tree.CreateElement("nav", root, classNames: ["sidebar"]);
        var list = fixture.Tree.CreateElement("ul", sidebar);
        var first = fixture.Tree.CreateElement("li", list, classNames: ["item"]);
        var second = fixture.Tree.CreateElement("li", list, classNames: ["item", "selected"]);
        var third = fixture.Tree.CreateElement("li", list, classNames: ["item"]);

        Assert.True(fixture.Matches(".app .item", third));
        Assert.True(fixture.Matches(".sidebar ul li", third));
        Assert.True(fixture.Matches("ul > li", third));
        Assert.False(fixture.Matches(".sidebar > li", third));

        Assert.True(fixture.Matches(".selected + .item", third));
        Assert.False(fixture.Matches(".selected + .item", second));

        Assert.True(fixture.Matches(".selected ~ .item", third));
        Assert.False(fixture.Matches(".selected ~ .item", first));
    }

    [Fact]
    public void A_descendant_combinator_keeps_climbing_past_an_ancestor_that_does_not_fit() {
        // The backtracking case. Matching `.a .b` at the leaf finds a `.b`, climbs, finds a `.b`
        // that is not under an `.a`, and has to keep going rather than give up.
        var fixture = new StyleFixture();
        var outer = fixture.Tree.CreateElement("div", classNames: ["a"]);
        var middle = fixture.Tree.CreateElement("div", outer, classNames: ["b"]);
        var inner = fixture.Tree.CreateElement("div", middle, classNames: ["b"]);
        var leaf = fixture.Tree.CreateElement("span", inner, classNames: ["c"]);

        Assert.True(fixture.Matches(".a .b .c", leaf));

        var detached = fixture.Tree.CreateElement("div", classNames: ["b"]);
        var detachedLeaf = fixture.Tree.CreateElement("span", detached, classNames: ["c"]);

        Assert.False(fixture.Matches(".a .b .c", detachedLeaf));
    }

    [Fact]
    public void The_position_pseudo_classes_count_from_one_the_way_CSS_does() {
        var fixture = new StyleFixture();
        var list = fixture.Tree.CreateElement("ul");
        var items = new StyleNodeId[5];
        for (var i = 0; i < items.Length; i++) {
            items[i] = fixture.Tree.CreateElement("li", list);
        }

        Assert.True(fixture.Matches(":first-child", items[0]));
        Assert.False(fixture.Matches(":first-child", items[1]));
        Assert.True(fixture.Matches(":last-child", items[4]));
        Assert.False(fixture.Matches(":last-child", items[3]));

        Assert.True(fixture.Matches(":nth-child(1)", items[0]));
        Assert.True(fixture.Matches(":nth-child(3)", items[2]));
        Assert.False(fixture.Matches(":nth-child(3)", items[3]));

        // 2n+1 is every odd position, one-based.
        Assert.True(fixture.Matches(":nth-child(2n+1)", items[0]));
        Assert.False(fixture.Matches(":nth-child(2n+1)", items[1]));
        Assert.True(fixture.Matches(":nth-child(2n+1)", items[2]));

        Assert.True(fixture.Matches(":nth-last-child(1)", items[4]));
        Assert.True(fixture.Matches(":nth-last-child(2)", items[3]));

        var only = fixture.Tree.CreateElement("p");
        var child = fixture.Tree.CreateElement("span", only);

        Assert.True(fixture.Matches(":only-child", child));
    }

    [Fact]
    public void The_state_pseudo_classes_read_the_element_state() {
        var fixture = new StyleFixture();
        var button = fixture.Tree.CreateElement("button");

        Assert.False(fixture.Matches(":hover", button));

        fixture.Tree.SetState(button, ElementState.Hover | ElementState.Focus);

        Assert.True(fixture.Matches(":hover", button));
        Assert.True(fixture.Matches(":focus", button));
        Assert.True(fixture.Matches(":hover:focus", button));
        Assert.False(fixture.Matches(":active", button));
        Assert.False(fixture.Matches(":disabled", button));
        Assert.True(fixture.Matches(":enabled", button));

        fixture.Tree.SetState(button, ElementState.Disabled);

        Assert.True(fixture.Matches(":disabled", button));
        Assert.False(fixture.Matches(":enabled", button));
    }

    [Fact]
    public void Not_and_is_take_whole_selector_lists() {
        var fixture = new StyleFixture();
        var plain = fixture.Tree.CreateElement("div", classNames: ["row"]);
        var selected = fixture.Tree.CreateElement("div", classNames: ["row", "selected"]);
        var header = fixture.Tree.CreateElement("h1");

        Assert.True(fixture.Matches(".row:not(.selected)", plain));
        Assert.False(fixture.Matches(".row:not(.selected)", selected));

        Assert.True(fixture.Matches(":is(h1, h2, h3)", header));
        Assert.False(fixture.Matches(":is(h1, h2, h3)", plain));

        Assert.True(fixture.Matches(":is(.row, .column):not(.selected)", plain));
        Assert.False(fixture.Matches(":is(.row, .column):not(.selected)", selected));
    }

    [Fact]
    public void A_pseudo_element_says_which_box_a_rule_targets_rather_than_filtering_the_element() {
        var fixture = new StyleFixture();
        var element = fixture.Tree.CreateElement("div", classNames: ["tooltip"]);

        var withPseudo = fixture.Compile(".tooltip::before");
        var withoutPseudo = fixture.Compile(".tooltip");

        Assert.True(fixture.Matcher.Matches(fixture.Tree, element, withPseudo));
        Assert.Equal(fixture.Names.Lookup("before"), withPseudo.PseudoElement);
        Assert.Equal(NameTable.None, withoutPseudo.PseudoElement);
    }

    [Fact]
    public void Specificity_counts_the_way_the_cascade_needs_it_to() {
        var fixture = new StyleFixture();

        Assert.Equal(new Specificity(0, 0, 1), fixture.Compile("div").Specificity);
        Assert.Equal(new Specificity(0, 1, 0), fixture.Compile(".row").Specificity);
        Assert.Equal(new Specificity(1, 0, 0), fixture.Compile("#app").Specificity);
        Assert.Equal(new Specificity(1, 1, 1), fixture.Compile("div#app.row").Specificity);
        Assert.Equal(new Specificity(0, 1, 0), fixture.Compile("[data-x]").Specificity);
        Assert.Equal(new Specificity(0, 1, 0), fixture.Compile(":hover").Specificity);
        Assert.Equal(new Specificity(0, 0, 2), fixture.Compile("div::before").Specificity);
        Assert.Equal(new Specificity(0, 0, 3), fixture.Compile("div span p").Specificity);

        Assert.True(fixture.Compile("#app").Specificity > fixture.Compile(".a.b.c.d").Specificity);
        Assert.True(fixture.Compile(".row").Specificity > fixture.Compile("div span").Specificity);
    }

    [Fact]
    public void A_selector_Vixen_does_not_support_is_dropped_with_a_diagnostic() {
        // Dropped rather than approximated. A rule that silently matches more than it says produces
        // a UI that is wrong everywhere nobody looked; a rule that does not load produces a message.
        var fixture = new StyleFixture();
        var compiled = fixture.Load(".ok { color: red } .bad:has(.x) { color: blue } .also-ok { color: green }");

        Assert.Equal(2, compiled.Count);
        Assert.Single(fixture.Compiler.Diagnostics);

        // Quoting what the author wrote, not what ExCSS calls it internally. `Contains("has")` was
        // the assertion here first and it could not tell the two apart — ":has(.x)" and the class
        // name "HasSelector" both contain it, and the message said the second one.
        var diagnostic = fixture.Compiler.Diagnostics[0];

        Assert.Contains(":has(.x)", diagnostic.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Selector", diagnostic.Reason, StringComparison.Ordinal);
    }
}
