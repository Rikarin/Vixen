// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     The invalidation-minimality gate [doc 14](../../docs/plan/14-roadmap.md) names for 4b:
///     toggling a class restyles exactly N elements.
/// </summary>
/// <remarks>
///     Every test here asserts a <i>count</i>, which is unusual and is the point. Correctness is
///     covered by <see cref="IncrementalRestyleOracleTests" />; what these say is that the answer was
///     arrived at without restyling the grid, and a count is the only assertion that can say it. An
///     invalidator that gave up and restyled everything would pass every correctness test there is.
/// </remarks>
public class InvalidationTests {
    [Fact]
    public void Selecting_one_row_of_a_grid_restyles_that_row_and_nothing_else() {
        // The case doc 09 names. A hundred rows of a hundred cells, and `.selected` is a rule that
        // reaches nothing below it.
        //
        // `background`, not `color`, and the difference is the whole of what invalidation can and
        // cannot do. A highlight that changes `background` touches one element, because nothing
        // inherits it. The same highlight written with `color` touches a hundred and one, because
        // every cell's inherited colour genuinely did change — no dependency map can avoid that, and
        // `An_inherited_property_carries_past_…` below is that case stated on its own.
        var fixture = new CascadeFixture();
        fixture.Load(
            ".row { background: normal } .row.selected { background: highlighted } .cell { padding-left: 2px }"
        );

        var grid = fixture.Tree.CreateElement("div", classNames: ["grid"]);
        var rows = new StyleNodeId[100];

        for (var r = 0; r < rows.Length; r++) {
            rows[r] = fixture.Tree.CreateElement("div", grid, classNames: ["row"]);
            for (var c = 0; c < 100; c++) {
                fixture.Tree.CreateElement("div", rows[r], classNames: ["cell"]);
            }
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(rows[42], "selected");

        Assert.Equal(1, updater.ClassChanged(rows[42], "selected"));
        Assert.Equal("highlighted", fixture.Read(updater.StyleOf(rows[42]), "background"));
    }

    [Fact]
    public void A_class_that_reaches_descendants_restyles_only_the_descendants_it_names() {
        // `.selected .cell` has to reach the cells, and must not reach anything else in the row.
        // A scheme that only knew "this reaches downward" would restyle the whole subtree.
        var fixture = new CascadeFixture();
        fixture.Load(".selected .cell { background: highlighted }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        for (var i = 0; i < 20; i++) {
            fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
            fixture.Tree.CreateElement("div", row, classNames: ["gutter"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(row, "selected");

        // The row itself, and its twenty cells. Not its twenty gutters.
        Assert.Equal(21, updater.ClassChanged(row, "selected"));
    }

    [Fact]
    public void A_class_whose_rule_names_nothing_at_the_far_end_restyles_the_subtree() {
        // `.selected *` cannot be narrowed by any feature, and saying so is the honest answer. What
        // matters is that it is the *rule* that says so and not the invalidator giving up.
        var fixture = new CascadeFixture();
        fixture.Load(".selected * { background: highlighted }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        for (var i = 0; i < 10; i++) {
            fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(row, "selected");

        Assert.Equal(11, updater.ClassChanged(row, "selected"));
    }

    [Fact]
    public void An_inherited_property_carries_past_the_elements_the_rules_reached() {
        // The other half of invalidation, and the half a dependency map cannot see. `.themed`
        // changes the row's `color`; no rule mentions the cells at all, and they inherit it anyway.
        var fixture = new CascadeFixture();
        fixture.Load(".themed { color: themed }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        var cells = new StyleNodeId[10];
        for (var i = 0; i < cells.Length; i++) {
            cells[i] = fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(row, "themed");

        Assert.Equal(11, updater.ClassChanged(row, "themed"));
        Assert.Equal("themed", fixture.Read(updater.StyleOf(cells[3]), "color"));
    }

    [Fact]
    public void A_change_that_no_child_could_inherit_does_not_descend_at_all() {
        // `padding-left` does not inherit, so a change to it cannot reach a child and the walk stops
        // at the element it happened to. The cells are never even visited.
        var fixture = new CascadeFixture();
        fixture.Load(".padded { padding-left: 4px }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        for (var i = 0; i < 5; i++) {
            fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(row, "padded");

        Assert.Equal(1, updater.ClassChanged(row, "padded"));
    }

    [Fact]
    public void The_walk_stops_where_a_child_overrode_what_it_would_have_inherited() {
        // The stopping rule where it earns its keep. The root's `color` changes and `color` does
        // inherit, so the labels have to be reconsidered — but each one sets its own, so each comes
        // back as the same interned object and the walk goes no further. The twenty-five spans
        // beneath them are never reached, and *that* is what reference comparison buys.
        var fixture = new CascadeFixture();
        fixture.Load(".themed { color: themed } .label { color: own }");

        var root = fixture.Tree.CreateElement("div");
        for (var i = 0; i < 5; i++) {
            var label = fixture.Tree.CreateElement("div", root, classNames: ["label"]);
            for (var j = 0; j < 5; j++) {
                fixture.Tree.CreateElement("span", label);
            }
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(root, "themed");

        Assert.Equal(6, updater.ClassChanged(root, "themed"));
        Assert.Equal(5, updater.LastPassStopped);
    }

    [Fact]
    public void A_sibling_rule_reaches_later_siblings_and_not_earlier_ones() {
        var fixture = new CascadeFixture();
        fixture.Load(".selected ~ .row { background: after }");

        var list = fixture.Tree.CreateElement("div");
        var rows = new StyleNodeId[10];
        for (var i = 0; i < rows.Length; i++) {
            rows[i] = fixture.Tree.CreateElement("div", list, classNames: ["row"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(rows[3], "selected");

        // Row 3 itself, plus rows 4 to 9.
        Assert.Equal(7, updater.ClassChanged(rows[3], "selected"));
        Assert.Equal("after", fixture.Read(updater.StyleOf(rows[7]), "background"));
        Assert.Null(fixture.Read(updater.StyleOf(rows[1]), "background"));
    }

    [Fact]
    public void A_class_no_rule_mentions_restyles_only_the_element_it_was_put_on() {
        var fixture = new CascadeFixture();
        fixture.Load(".row { background: normal }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        for (var i = 0; i < 50; i++) {
            fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.AddClass(row, "no-rule-says-anything-about-this");

        Assert.Equal(1, updater.ClassChanged(row, "no-rule-says-anything-about-this"));
    }

    [Fact]
    public void Hovering_a_row_that_no_rule_reads_downward_from_restyles_only_the_row() {
        var fixture = new CascadeFixture();
        fixture.Load(".row:hover { background: hovered } .cell { padding-left: 2px }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        for (var i = 0; i < 50; i++) {
            fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.SetState(row, ElementState.Hover);

        Assert.Equal(1, updater.StateChanged(row));
        Assert.Equal("hovered", fixture.Read(updater.StyleOf(row), "background"));
    }

    [Fact]
    public void Hovering_a_row_that_a_rule_does_read_downward_from_reaches_what_that_rule_names() {
        // `.row:hover .cell` is why hovering a grid row is not free, and the invalidator has to know
        // it — a state change is not a name and cannot be looked up in the map, so the rule set
        // records separately whether any state test sits above a combinator.
        var fixture = new CascadeFixture();
        fixture.Load(".row:hover .cell { background: hovered }");

        var row = fixture.Tree.CreateElement("div", classNames: ["row"]);
        for (var i = 0; i < 20; i++) {
            fixture.Tree.CreateElement("div", row, classNames: ["cell"]);
            fixture.Tree.CreateElement("div", row, classNames: ["gutter"]);
        }

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        fixture.Tree.SetState(row, ElementState.Hover);

        Assert.Equal(21, updater.StateChanged(row));
    }

    [Fact]
    public void Adding_a_class_to_an_ancestor_updates_the_blooms_below_it() {
        // The bloom holds an element's *ancestors'* names, so a class arriving on an ancestor has to
        // arrive in every descendant's filter too. A false positive costs a tree walk; a false
        // negative is a rule that silently stops matching, and this is where one would come from.
        var fixture = new CascadeFixture();
        fixture.Load(".theme .label { color: themed }");

        var root = fixture.Tree.CreateElement("div");
        var middle = fixture.Tree.CreateElement("div", root);
        var label = fixture.Tree.CreateElement("span", middle, classNames: ["label"]);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Null(fixture.Read(updater.StyleOf(label), "color"));

        fixture.Tree.AddClass(root, "theme");
        updater.ClassChanged(root, "theme");

        Assert.Equal("themed", fixture.Read(updater.StyleOf(label), "color"));
    }

    [Fact]
    public void Removing_a_class_leaves_the_blooms_alone_and_still_gives_the_right_answer() {
        // A stale bit only ever makes the filter say "an ancestor might be called this" when none
        // is, which costs the walk that would have happened anyway. The filter is allowed to be
        // conservative; it is never allowed to be wrong.
        var fixture = new CascadeFixture();
        fixture.Load(".theme .label { color: themed }");

        var root = fixture.Tree.CreateElement("div", classNames: ["theme"]);
        var label = fixture.Tree.CreateElement("span", root, classNames: ["label"]);

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Equal("themed", fixture.Read(updater.StyleOf(label), "color"));

        fixture.Tree.RemoveClass(root, "theme");
        updater.ClassChanged(root, "theme");

        Assert.Null(fixture.Read(updater.StyleOf(label), "color"));
    }
}
