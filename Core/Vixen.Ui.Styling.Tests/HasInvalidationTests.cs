// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>What <c>:has()</c> does to a restyle, in elements resolved.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 09 deferred <c>:has()</c> to P2 on incremental-match cost, so the cost is the
///         deliverable and not a footnote.</b> Measured as <i>work</i> — <c>LastPassResolved</c>, the
///         number of elements the cascade actually recomputed — and never as elapsed time, which on
///         this repository's own record is its largest source of flakes. A count is also the only
///         form of the measurement that can be asserted: it is exact, it is the same on every
///         machine, and a change that made invalidation coarser moves it.
///     </para>
///     <para>
///         The other half is <c>IncrementalRestyleOracleTests</c>, whose generated stylesheets now
///         contain <c>:has()</c>. That one says the upward walk is <i>right</i>; this one says it is
///         not free, and says by how much. Neither is enough alone — an invalidator that restyled
///         the document on every change would pass the oracle, and one that restyled nothing would
///         pass a cost test.
///     </para>
/// </remarks>
public class HasInvalidationTests {
    const string Sheet = """
        .cell { color: normal }
        .card:has(.error) .cell { color: alarming }
        """;

    const string Comparable = """
        .cell { color: normal }
        .card.flagged .cell { color: alarming }
        """;

    /// <summary>Builds a page of cards, each with a body and some cells, and returns one deep cell.</summary>
    static (CascadeFixture Fixture, StyleNodeId Field, int Elements) Page(string css, int cards, int cells) {
        var fixture = new CascadeFixture();
        fixture.Load(css);

        var page = fixture.Tree.CreateElement("div", classNames: ["page"]);
        var field = StyleNodeId.Invalid;

        for (var c = 0; c < cards; c++) {
            var card = fixture.Tree.CreateElement("div", page, classNames: ["card"]);
            var body = fixture.Tree.CreateElement("div", card, classNames: ["body"]);

            for (var i = 0; i < cells; i++) {
                var cell = fixture.Tree.CreateElement("div", body, classNames: ["cell"]);

                if (c == 0 && i == 0) {
                    field = cell;
                }
            }
        }

        return (fixture, field, fixture.Tree.Count);
    }

    [Fact]
    public void A_class_inside_a_has_restyles_the_ancestors_and_every_cell_in_the_document() {
        var (fixture, field, elements) = Page(Sheet, cards: 10, cells: 10);
        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Equal(elements, updater.LastPassResolved);

        fixture.Tree.AddClass(field, "error");
        var resolved = updater.ClassChanged(field, "error");

        // ⚠ The number is the point, so it is written out rather than bounded. The page is 121
        // elements: one page, ten cards, ten bodies, a hundred cells. A change to `.error` on one
        // cell restyles that cell's ancestors — body, card, page, three elements — and then every
        // `.cell` in the document, because `.card:has(.error) .cell` is a rule whose far end is
        // `.cell` and nothing about an upward walk knows which cards contain the change.
        //
        // 103 = the 100 cells, one of which is the changed one, plus its body, its card and the
        // page. The nine untouched cards and their bodies are not resolved, which is the narrowing
        // the far end's names buy; the ninety cells under them are, which is what they do not.
        Assert.Equal(103, resolved);

        // And it was the right answer, not merely a big one.
        Assert.Equal("alarming", fixture.Read(updater.StyleOf(field), "color"));
    }

    [Fact]
    public void The_same_page_driven_by_a_class_on_the_card_costs_a_tenth_of_that() {
        // ⚠ The differential, measured on the same shape in the same run, which is what makes the
        // number above mean something. `.card.flagged .cell` says what `.card:has(.error) .cell`
        // says, one direction round: the same hundred-cell page, the same rule shape, the same
        // property — and the only difference is which end of the relationship changed.
        var (fixture, _, _) = Page(Comparable, cards: 10, cells: 10);
        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        var card = fixture.Tree.GetChild(new StyleNodeId(0), 0);
        fixture.Tree.AddClass(card, "flagged");

        // Ten: the card itself and its ten cells — the cells of the other nine cards cannot have
        // been reached, and the invalidator knows it because the change is above them.
        Assert.Equal(11, updater.ClassChanged(card, "flagged"));
    }

    [Fact]
    public void A_state_inside_a_has_walks_up_as_well_and_a_state_outside_one_does_not() {
        // ⚠ A state is not a name, so it cannot be looked up in the invalidator's map — which is
        // exactly how `:hover` above a combinator once came to restyle whole subtrees. The upward
        // direction has the same hole and the same fix, and this is the pair that proves the fix is
        // not simply "always walk up".
        var withHas = new CascadeFixture();
        withHas.Load(".cell { color: normal } .card:has(:checked) { color: alarming }");

        var page = withHas.Tree.CreateElement("div", classNames: ["page"]);
        var card = withHas.Tree.CreateElement("div", page, classNames: ["card"]);
        var cell = withHas.Tree.CreateElement("div", card, classNames: ["cell"]);

        var updater = new StyleUpdater(withHas.Engine);
        updater.ResolveAll();

        withHas.Tree.SetState(cell, ElementState.Checked);
        updater.StateChanged(cell);

        Assert.Equal("alarming", withHas.Read(updater.StyleOf(card), "color"));

        // The same document under a sheet with no `:has()` in it: a state change on the cell cannot
        // reach the card, and the invalidator must not pretend it can.
        var without = new CascadeFixture();
        without.Load(".cell { color: normal } .card:checked { color: alarming }");

        var page2 = without.Tree.CreateElement("div", classNames: ["page"]);
        var card2 = without.Tree.CreateElement("div", page2, classNames: ["card"]);
        var cell2 = without.Tree.CreateElement("div", card2, classNames: ["cell"]);

        var second = new StyleUpdater(without.Engine);
        second.ResolveAll();

        without.Tree.SetState(cell2, ElementState.Checked);

        Assert.Equal(1, second.StateChanged(cell2));
        Assert.Null(without.Read(second.StyleOf(card2), "color"));
    }

    [Fact]
    public void A_has_rule_turns_the_sharing_cache_off_because_two_identical_cards_can_differ_below() {
        // ⚠ The soundness hole `:has()` opens in the *other* optimisation, and the one that would
        // have been invisible: a sharing key is parent, tag, classes and state, so two sibling cards
        // that are identical by it share a style — and one of them containing an error is a
        // difference the key cannot see. `StyleRuleSet.BlocksSharing` names `Has` for the reason it
        // names `Empty`.
        var fixture = new CascadeFixture();
        fixture.Load(".card { color: normal } .card:has(.error) { color: alarming }");

        var page = fixture.Tree.CreateElement("div", classNames: ["page"]);
        var first = fixture.Tree.CreateElement("div", page, classNames: ["card"]);
        fixture.Tree.CreateElement("div", first, classNames: ["error"]);
        var second = fixture.Tree.CreateElement("div", page, classNames: ["card"]);
        fixture.Tree.CreateElement("div", second, classNames: ["plain"]);

        Assert.False(
            fixture.Engine.Rules.SharingIsSound(
                fixture.Engine.Scopes.VerdictsOf(MediaScopes.Document),
                fixture.Engine.ContainerScopes.VerdictsOf(ContainerScopes.Root)
            )
        );

        var updater = new StyleUpdater(fixture.Engine);
        updater.ResolveAll();

        Assert.Equal("alarming", fixture.Read(updater.StyleOf(first), "color"));
        Assert.Equal("normal", fixture.Read(updater.StyleOf(second), "color"));
    }
}
