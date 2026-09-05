// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>CSS Scroll Anchoring: what is above the reader may grow without moving the reader.</summary>
/// <remarks>
///     <para>
///         <b>Every assertion here is closed form.</b> The rows are forty pixels and the growth is a
///         whole number of them, so the answer to "where should the offset be" is arithmetic rather
///         than a tolerance — which is what lets the sabotage checks below be exact and what stops a
///         correction that is merely in the right direction passing for one that is right.
///     </para>
///     <para>
///         ⚠ <b>Two of these are instrument tests and are supposed to stay green under the
///         sabotage.</b> A view at the start edge and a view whose offset moved on the same frame must
///         <i>not</i> be corrected, so a test suite that only ever asserted a correction would be
///         satisfied by anchoring that fired unconditionally — which is the version that cancels a
///         virtualiser's scroll, and the reason this feature sat unlanded.
///     </para>
/// </remarks>
public class ScrollAnchoringTests {
    /// <summary>A 200-pixel port over ten 40-pixel rows, the first of which can be made taller.</summary>
    /// <remarks>
    ///     The growth is a class on the first row rather than an inserted element, because the two are
    ///     the same event to the rule under test — an element's content-space position moving — and a
    ///     class change cannot be confused with the anchor itself being replaced.
    /// </remarks>
    static (ControlFixture Fixture, ScrollView View, UiElement[] Row) Rows(string css = "") {
        var fixture = new ControlFixture(css: $$"""
            root   { width: 400px; height: 400px; }
            #view  { width: 300px; height: 200px; }
            .row   { width: 260px; height: 40px; }
            .grown { height: 120px; }
            {{css}}
            """);

        var view = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        var rows = new UiElement[10];

        for (var index = 0; index < rows.Length; index++) {
            rows[index] = fixture.Document.Create("div", view.Content, $"row-{index}", "row");
        }

        fixture.Update();

        return (fixture, view, rows);
    }

    /// <summary>Content growing above the reader moves the offset by exactly what it grew.</summary>
    [Fact]
    public void Growth_above_the_port_moves_the_offset_by_what_it_grew() {
        var (fixture, view, row) = Rows();

        view.ScrollTop = 160f;
        fixture.Update();

        // The baseline is taken on a frame the offset moved, so a second settled frame is what
        // arms the anchor — exactly as a real frame loop would.
        fixture.Update();

        row[0].AddClass("grown");
        fixture.Update();

        // 40 → 120 is eighty pixels of growth entirely above the port, so the row the reader was
        // looking at is eighty pixels further down the content and the offset has to follow it.
        Assert.Equal(240f, view.ScrollTop, 3);
    }

    /// <summary>And content shrinking above it moves the offset back the same way.</summary>
    /// <remarks>
    ///     ⚠ <b>The direction is worth a test of its own, not because the subtraction has two
    ///     directions but because the clamp does.</b> A shrink reduces <c>MaximumTop</c>, so a
    ///     correction that arrived after <see cref="ScrollView.Refresh" />'s own clamp would be
    ///     coerced against a range it had already left.
    /// </remarks>
    [Fact]
    public void Shrinking_above_the_port_moves_the_offset_back() {
        var (fixture, view, row) = Rows();

        row[0].AddClass("grown");
        fixture.Update();

        view.ScrollTop = 240f;
        fixture.Update();
        fixture.Update();

        row[0].RemoveClass("grown");
        fixture.Update();

        Assert.Equal(160f, view.ScrollTop, 3);
    }

    /// <summary>Growth below the reader moves nothing, which is the half that makes it a rule.</summary>
    [Fact]
    public void Growth_below_the_port_moves_nothing() {
        var (fixture, view, row) = Rows();

        view.ScrollTop = 40f;
        fixture.Update();
        fixture.Update();

        // The last row is below a port that ends at content 240; growing it changes nothing above
        // the anchor, so the anchor does not move and neither does the offset.
        row[9].AddClass("grown");
        fixture.Update();

        Assert.Equal(40f, view.ScrollTop, 3);
    }

    /// <summary>A view at the start edge is not anchored — the new content is what the reader wants.</summary>
    /// <remarks>
    ///     ⚠ <b>An instrument test: it stays green when the correction is sabotaged.</b> It is here to
    ///     make the suppression falsifiable in the other direction, since a correction at offset zero
    ///     would scroll a reader away from content arriving at the top of a log.
    /// </remarks>
    [Fact]
    public void A_view_at_the_start_edge_is_not_anchored() {
        var (fixture, view, row) = Rows();

        Assert.Equal(0f, view.ScrollTop, 3);

        row[0].AddClass("grown");
        fixture.Update();

        Assert.Equal(0f, view.ScrollTop, 3);
    }

    /// <summary>`overflow-anchor: none` on the view refuses the correction outright.</summary>
    [Fact]
    public void Overflow_anchor_none_refuses_the_correction() {
        var (fixture, view, row) = Rows("#view { overflow-anchor: none; }");

        view.ScrollTop = 160f;
        fixture.Update();
        fixture.Update();

        row[0].AddClass("grown");
        fixture.Update();

        Assert.Equal(160f, view.ScrollTop, 3);
    }

    /// <summary>`overflow-anchor: none` on a row makes the walk pick a different one.</summary>
    /// <remarks>
    ///     The refused row is the one the port straddles, so refusing it does not disable anchoring —
    ///     it moves the anchor to the next row down, whose content position moves by the same eighty
    ///     pixels. The correction is therefore identical, which is the point: the property excludes a
    ///     candidate rather than the feature.
    /// </remarks>
    [Fact]
    public void Overflow_anchor_none_on_a_row_excludes_only_that_row() {
        var (fixture, view, row) = Rows("#row-4 { overflow-anchor: none; }");

        view.ScrollTop = 160f;
        fixture.Update();
        fixture.Update();

        row[0].AddClass("grown");
        fixture.Update();

        Assert.Equal(240f, view.ScrollTop, 3);
    }

    /// <summary>A frame whose offset moved is re-baselined and never corrected.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The other instrument test, and the one that stands in for a row recycler.</b> A
    ///         virtualising list re-lays its rows <i>because</i> the offset moved, so its rows'
    ///         content positions change on exactly the frames a scroll happened — and a correction on
    ///         such a frame subtracts the scroll the reader just asked for. This asserts the
    ///         suppression directly: growth and a scroll on one frame leave the scroll intact.
    ///     </para>
    ///     <para>
    ///         It stays green under the sabotage of the correction and goes red under a version that
    ///         corrects unconditionally, which is the pair no single-direction test gives.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_frame_whose_offset_moved_is_not_corrected() {
        var (fixture, view, row) = Rows();

        view.ScrollTop = 160f;
        fixture.Update();
        fixture.Update();

        // Both on one frame: the reader scrolled a row and the content above them grew.
        row[0].AddClass("grown");
        view.ScrollTop = 200f;
        fixture.Update();

        Assert.Equal(200f, view.ScrollTop, 3);
    }
}
