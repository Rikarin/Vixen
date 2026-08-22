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
    public void Empty_means_no_children_and_no_text() {
        var fixture = new StyleFixture();

        var bare = fixture.Tree.CreateElement("span");
        var labelled = fixture.Tree.CreateElement("span");
        var parent = fixture.Tree.CreateElement("div");
        fixture.Tree.CreateElement("span", parent);

        fixture.Tree.SetHasText(labelled, true);

        Assert.True(fixture.Matches(":empty", bare));
        Assert.False(fixture.Matches(":empty", parent));

        // ⚠ The half that is not the child count, and the half the two rules in the tree that wanted
        // `:empty` are entirely about. Both are on leaves — a search row's port and a vector lane's
        // letter — whose content is `UiElement.Text` and never a child, so a `:empty` that counted
        // children alone would match them whatever they said and hide the ones with something to
        // say. Text is a node in the DOM and a property here, and this is where that difference is
        // paid back.
        Assert.False(fixture.Matches(":empty", labelled));

        fixture.Tree.SetHasText(labelled, false);
        Assert.True(fixture.Matches(":empty", labelled));
    }

    [Fact]
    public void Empty_composes_with_everything_else_a_compound_can_hold() {
        var fixture = new StyleFixture();

        var lane = fixture.Tree.CreateElement("node-port-lane", classNames: ["muted"]);
        var named = fixture.Tree.CreateElement("node-port-lane", classNames: ["muted"]);
        fixture.Tree.SetHasText(named, true);

        Assert.True(fixture.Matches("node-port-lane:empty", lane));
        Assert.False(fixture.Matches("node-port-lane:empty", named));

        Assert.False(fixture.Matches(".muted:not(:empty)", lane));
        Assert.True(fixture.Matches(".muted:not(:empty)", named));

        Assert.True(fixture.Matches(":is(:empty, .selected)", lane));
        Assert.False(fixture.Matches(":is(:empty, .selected)", named));

        // A pseudo-class, so it counts in the middle column exactly as `:hover` does.
        Assert.Equal(new Specificity(0, 1, 1), fixture.Compile("node-port-lane:empty").Specificity);
    }

    [Fact]
    public void A_rule_that_asks_what_an_element_holds_turns_style_sharing_off() {
        // The sharing key carries what an element *is* — parent, tag, id, classes, state, inline
        // block — and `:empty` asks what it *holds*. Two lanes of one vector field are the same tag
        // and the same classes under the same parent, and only one of them was given a letter; a
        // cache that shared between them would hand the named one the hidden one's style. This is
        // the same hole `:nth-child` and `[attr]` are already kept out of.
        var fixture = new StyleFixture();
        var rules = new StyleRuleSet(fixture.Table, fixture.Names, new NameTable(), new NameTable());

        rules.Add(fixture.Compile("node-port-lane"), [], StyleOrigin.Author, CascadeLayers.Unlayered);
        Assert.True(rules.SharingIsSound(default, default));

        rules.Add(fixture.Compile("node-port-lane:empty"), [], StyleOrigin.Author, CascadeLayers.Unlayered);
        Assert.False(rules.SharingIsSound(default, default));
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

    /// <summary>A pseudo-element is refused, because the alternative was matching the wrong thing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test is an inversion, and the thing it used to assert is the defect.</b> It
    ///         was called <c>A_pseudo_element_says_which_box_a_rule_targets_rather_than_filtering_the_element</c>
    ///         and it asserted that <c>.tooltip::before</c> matched a <c>.tooltip</c> and that the
    ///         name <c>before</c> had been interned onto the compiled selector. Both were true. The
    ///         part nobody asserted is that <i>nothing read the field</i> — not
    ///         <see cref="SelectorMatcher" />, not <c>StyleRuleSet</c>, not <c>StyleResolver</c> — so
    ///         the only observable behaviour of <c>p::before { color: red }</c> was a red paragraph.
    ///         Doc 43 records it as F6 and calls it the worst of the three possible states, because
    ///         the rule looked like it worked.
    ///     </para>
    ///     <para>
    ///         <b>Refused rather than matched-and-inert.</b> A selector that matches and contributes
    ///         nothing is this codebase's recurring defect, and it would leave an author staring at a
    ///         rule with no output and no message. The compiler's own contract — see its remarks —
    ///         already says an unsupported selector is dropped with a diagnostic; until this change
    ///         the pseudo-element was the one thing that broke it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_pseudo_element_selector_is_refused_rather_than_applied_to_the_element_behind_it() {
        var fixture = new StyleFixture();
        var element = fixture.Tree.CreateElement("p", classNames: ["tooltip"]);

        var compiled = fixture.Load("p::before { color: red } .tooltip::after { color: blue } p { color: green }");

        // Only the rule with no pseudo-element survived, and it is the one that matches.
        var survivor = Assert.Single(compiled);
        Assert.True(fixture.Matcher.Matches(fixture.Tree, element, survivor));

        Assert.Equal(2, fixture.Compiler.Diagnostics.Count);

        // Naming what the author wrote, not what ExCSS calls it — the `:has()` lesson, and the
        // reason two refusals in one sheet stay tellable apart.
        Assert.Equal("::before", fixture.Compiler.Diagnostics[0].Text);
        Assert.Equal("::after", fixture.Compiler.Diagnostics[1].Text);

        Assert.Contains("generates a box", fixture.Compiler.Diagnostics[0].Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Selector", fixture.Compiler.Diagnostics[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>Refusing a pseudo-element does not renumber the selectors around it.</summary>
    /// <remarks>
    ///     ⚠ <b>The hazard in dropping a selector late.</b> <c>Selector.Start</c> and
    ///     <c>CompoundSelector.Start</c> are absolute offsets into one shared
    ///     <see cref="SelectorTable" /> that grows for the whole sheet. A refusal that had already
    ///     written into that table — and <c>:is()</c> writes its nested parts on the way past, before
    ///     the compound holding it is known to be doomed — leaves entries nothing points at. Those
    ///     are waste, not corruption, precisely because every offset is captured at write time rather
    ///     than derived from a count. This asserts that: the rule before and the rule after both still
    ///     match, and their specificities are the ones they would have had on their own.
    /// </remarks>
    [Fact]
    public void A_refused_pseudo_element_leaves_the_rules_around_it_matching_and_weighed_the_same() {
        var fixture = new StyleFixture();
        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        var cell = fixture.Tree.CreateElement("span", classNames: ["cell"], parent: row);

        var alone = new StyleFixture();
        var expected = alone.Compile("div.row > span.cell").Specificity;

        var compiled = fixture.Load(
            "div.row > span.cell { color: red }"
                + " p:is(.x, .y)::before { color: blue }"
                + " .row .cell { color: green }"
        );

        Assert.Equal(2, compiled.Count);
        Assert.Single(fixture.Compiler.Diagnostics);

        Assert.Equal(expected, compiled[0].Specificity);
        Assert.Equal(new Specificity(0, 2, 0), compiled[1].Specificity);

        Assert.True(fixture.Matcher.Matches(fixture.Tree, cell, compiled[0]));
        Assert.True(fixture.Matcher.Matches(fixture.Tree, cell, compiled[1]));

        // And through the index, which is the path a document actually takes.
        Assert.Equal([0, 1], fixture.MatchIndexed(cell));
        Assert.Equal(fixture.MatchBruteForce(cell), fixture.MatchIndexed(cell));
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
