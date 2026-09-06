// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>How much of `.searchable` markup already has, measured on a list rather than a widget.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#767 calls `.searchable` a feature rather than a spelling, and that is two thirds
///         right.</b> Its three named parts are where the field goes, what it filters, and the empty
///         state when nothing matches. The middle one — the part two audits called the sharpest
///         open question, because "a framework cannot know what matching means for an arbitrary
///         `@for` sequence" — needs nothing built: a `SearchBox` bound to a signal and an `@for`
///         whose sequence expression reads that signal is a list that narrows as it is typed into,
///         and the predicate stays the author's C#.
///     </para>
///     <para>
///         <b>So this file is a measurement and not a feature.</b> It exists to narrow the issue:
///         what `.searchable` would add is placement and an empty state, and the design question it
///         is blocked on is not "what shape does the predicate take" — that shape is
///         <c>Where(...)</c> in the sequence expression, which the language already has and which
///         nothing else could improve on.
///     </para>
///     <para>
///         ⚠ <b>Asserted about the rows, per the issue's own "done looks like".</b> The field is
///         driven the way a person drives it, and what is checked is which `search-row` elements the
///         document holds afterwards — not the field's `Value`, which would be a claim about
///         `SearchBox` and is already covered elsewhere.
///     </para>
/// </remarks>
public class SearchableReachTests {
    [Fact]
    public void A_search_field_bound_to_a_signal_narrows_a_loop_that_reads_it() {
        using var fixture = new ControlFixture();

        var sheet = new SearchableSheet();

        sheet.Rows.Value = ["Albedo", "Normal", "Roughness", "Metallic"];

        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        Assert.Equal(["Albedo", "Normal", "Roughness", "Metallic"], Rows(sheet));

        // Typed into the field rather than written to the model: `bind:` is half of what is under
        // test, and a test that set `Filter.Value` would never have exercised it.
        sheet.Field.Value = "al";
        fixture.Update();

        Assert.Equal(["Albedo", "Normal", "Metallic"], Rows(sheet));

        sheet.Field.Value = "rough";
        fixture.Update();

        Assert.Equal(["Roughness"], Rows(sheet));
    }

    /// <summary>
    ///     ⚠ <b>And the part that genuinely is absent: nothing says so when nothing matches.</b>
    /// </summary>
    /// <remarks>
    ///     The list simply empties. That is correct and it is not an empty state — there is no
    ///     "No results for 'zz'" and no way to write one over a loop, because an `@for` has no
    ///     fallback arm — filed as #908. This is asserted rather than left as prose so that the day
    ///     one exists, this test is what has to change.
    /// </remarks>
    [Fact]
    public void A_filter_that_matches_nothing_leaves_an_empty_list_and_says_nothing() {
        using var fixture = new ControlFixture();

        var sheet = new SearchableSheet();

        sheet.Rows.Value = ["Albedo", "Normal"];

        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        sheet.Field.Value = "zz";
        fixture.Update();

        Assert.Empty(Rows(sheet));

        // Nothing took the rows' place: the sheet holds the field and no other content.
        var host = sheet.Root.Children[0];

        Assert.Equal("search-box", Assert.Single(host.Children).Tag);
    }

    /// <summary>What each <c>search-row</c> is showing.</summary>
    /// <remarks>
    ///     ⚠ Through the row's child rather than off the row: content interpolation emits a text
    ///     element, so a <c>&lt;search-row&gt;@row&lt;/search-row&gt;</c> has an empty <c>Text</c>
    ///     of its own and every assertion here would have compared "" with "".
    /// </remarks>
    static string[] Rows(SearchableSheet sheet) => [
        .. sheet.Root.Children[0]
            .Children.Where(child => child.Tag == "search-row")
            .Select(row => row.Children.Count == 0 ? row.Text ?? "" : row.Children[0].Text ?? "")
    ];
}
