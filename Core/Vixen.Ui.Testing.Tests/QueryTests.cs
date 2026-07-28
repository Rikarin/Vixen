// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>Selectors and traversal.</summary>
/// <remarks>
///     The point of most of these is agreement rather than behaviour: a selector in a test has to
///     mean what the same selector means in a stylesheet, and the way to check that is to write one
///     of each and assert that the same elements answer to both.
/// </remarks>
public class QueryTests {
    static UiTest Fixture() {
        var ui = UiTest.Create(400f, 300f);

        ui.Load("""
            root { width: 400px; height: 300px; }

            /* ⚠ Stated, not assumed. CSS's initial flex-direction is `row`, so a list that did not
               say this would lay its rows out side by side — which is correct and is not what the
               word "list" leads anybody to expect. */
            .list { width: 300px; height: 200px; flex-direction: column; }
            .row { width: 300px; height: 20px; }
            .row:nth-child(odd) { background-color: #222; }
        """);

        var list = ui.Create("div", ui.Document.Root, "items", "list");

        for (var i = 0; i < 4; i++) {
            var row = ui.Create("div", list, null, "row");
            row.Text = $"Row {i}";
        }

        ui.Frame();
        return ui;
    }

    [Fact]
    public void A_class_selector_finds_every_match_in_document_order() {
        using var ui = Fixture();

        var rows = ui.Get(".row").Elements;

        Assert.Equal(4, rows.Count);
        Assert.Equal(["Row 0", "Row 1", "Row 2", "Row 3"], rows.Select(row => row.Text));
    }

    [Fact]
    public void A_structural_pseudo_class_agrees_with_the_stylesheet() {
        using var ui = Fixture();

        // ⚠ The whole argument for compiling through the cascade's own machinery. `:nth-child(odd)`
        // is one-based and counts every sibling, and a second implementation would have got that
        // right or wrong independently of the stylesheet that draws the stripes.
        var odd = ui.Get(".row:nth-child(odd)").Elements;

        Assert.Equal(["Row 0", "Row 2"], odd.Select(row => row.Text));
    }

    [Fact]
    public void A_descendant_combinator_asks_the_real_ancestors() {
        using var ui = Fixture();

        ui.Get("#items .row").ShouldHaveCount(4);
        ui.Get("root > .row").ShouldNotExist();
    }

    [Fact]
    public void Find_looks_below_the_subject_and_not_at_it() {
        using var ui = Fixture();

        // ⚠ Without this, Get(".list").Find(".list") returns the list — the scope is a candidate of
        // its own subtree walk, and `find` means strictly below.
        ui.Get(".list").Find(".list").ShouldNotExist();
        ui.Get(".list").Find(".row").ShouldHaveCount(4);
    }

    [Fact]
    public void Filter_keeps_the_subject_and_narrows_it() {
        using var ui = Fixture();

        ui.Get(".row").Filter(":nth-child(1)").ShouldHaveCount(1);
    }

    [Fact]
    public void Contains_addresses_what_a_player_can_read() {
        using var ui = Fixture();

        ui.Contains("Row 2").ShouldHaveCount(1).ShouldHaveText("Row 2");
        ui.Get(".row").Contains("Row").ShouldHaveCount(4);
    }

    [Fact]
    public void Traversal_walks_up_and_down() {
        using var ui = Fixture();

        ui.Get(".row").First().ShouldHaveText("Row 0");
        ui.Get(".row").Last().ShouldHaveText("Row 3");
        ui.Get(".row").Nth(2).ShouldHaveText("Row 2");

        // Four rows share one parent, and it is named once rather than four times.
        ui.Get(".row").Parent().ShouldHaveCount(1);
        ui.Get(".row").Closest(".list").ShouldHaveCount(1);
        ui.Get(".list").Children().ShouldHaveCount(4);
    }

    [Fact]
    public void At_answers_with_whatever_the_document_would_hit() {
        using var ui = Fixture();

        // The second row: the list starts at the origin and each row is twenty tall.
        ui.At(10f, 30f).ShouldHaveText("Row 1");

        // Inside the viewport but past the list, so the root is what is under it — the root is a
        // real element and covers the viewport, which is easy to forget when reading a hit test.
        ui.At(390f, 290f).ShouldHaveCount(1);
        ui.At(500f, 500f).ShouldHaveCount(0);
    }

    [Fact]
    public void A_selector_that_cannot_compile_says_so_rather_than_matching_nothing() {
        using var ui = Fixture();

        // ⚠ The compiler drops what it does not support with a diagnostic rather than throwing,
        // which is right for a stylesheet and wrong for a test — a selector that silently matched
        // nothing would report "expected 1, found 0" and send somebody looking at the interface.
        var failure = Assert.Throws<UiTestException>(() => ui.Get("!!not a selector"));
        Assert.Contains("not a selector", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Where_is_the_escape_hatch_for_what_no_selector_reaches() {
        using var ui = Fixture();

        ui.Get(".row")
            .Where(row => row.Top >= 40f, "below the second row")
            .ShouldHaveCount(2);
    }
}
