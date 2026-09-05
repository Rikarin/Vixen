// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Content that grows above the viewport no longer pushes what the reader is reading away.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracle is closed-form and there is no clock in it.</b> Rows are forty pixels, so
///         a row inserted above the viewport must move the offset by exactly forty and must leave the
///         row the reader was looking at at exactly the same distance from the top of the view. A
///         test that asserted "roughly still there" would pass against an implementation that
///         corrected by the wrong box, which is the mistake the horizontal axis is left out to avoid.
///     </para>
///     <para>
///         <b>Both halves are asserted every time</b>: the offset moved <i>and</i> the anchor did
///         not. The offset alone would be satisfied by anything that added forty for any reason; the
///         anchor alone would be satisfied by a view that never scrolled.
///     </para>
/// </remarks>
public class ScrollAnchoringTests {
    /// <summary>A 100×60 view over five 40-pixel rows, so the content is 200 and the travel is 140.</summary>
    static (ControlFixture Fixture, ScrollView View) Rows(string view = "") {
        var fixture = new ControlFixture(css: $$"""
            root  { width: 400px; height: 300px; }
            #view { width: 100px; height: 60px; {{view}} }
            .row  { width: 100px; height: 40px; flex-shrink: 0; }
        """);

        var scroller = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");

        for (var index = 0; index < 5; index++) {
            fixture.Document.Create("div", scroller.Content, $"row{index}", "row");
        }

        fixture.Update();

        return (fixture, scroller);
    }

    /// <summary>A row inserted above the viewport takes the offset with it.</summary>
    [Fact]
    public void Content_inserted_above_the_viewport_does_not_move_what_is_in_it() {
        var (fixture, view) = Rows();
        using var scope = fixture;

        view.ScrollTop = 80f;
        fixture.Update();

        var watched = view.Content.Children[3];
        var before = watched.Top - view.ScrollTop;

        // Prepended, which is the case the defect is named after: a log that appends at the top, a
        // chat that loads older messages, an image above the fold that finished decoding.
        fixture.Document.Move(fixture.Document.Create("div", view.Content, "inserted", "row"), 0);
        fixture.Update();

        Assert.Equal(120f, view.ScrollTop, 1);
        Assert.Equal(before, watched.Top - view.ScrollTop, 1);
    }

    /// <summary>And a row removed from above it does the same in the other direction.</summary>
    [Fact]
    public void Content_removed_from_above_the_viewport_does_not_move_what_is_in_it() {
        var (fixture, view) = Rows();
        using var scope = fixture;

        view.ScrollTop = 80f;
        fixture.Update();

        var watched = view.Content.Children[3];
        var before = watched.Top - view.ScrollTop;

        view.Content.Children[0].Remove();
        fixture.Update();

        Assert.Equal(40f, view.ScrollTop, 1);
        Assert.Equal(before, watched.Top - view.ScrollTop, 1);
    }

    /// <summary>A reader parked at the top stays at the top, which is the rule and not an edge case.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument check.</b> An implementation that anchored unconditionally passes both
    ///     cases above and fails here — and its failure is the one a user notices first, because a
    ///     live feed that pins the reader to the old first row looks frozen while it fills up out of
    ///     sight.
    /// </remarks>
    [Fact]
    public void Nothing_is_corrected_when_the_view_is_at_the_top() {
        var (fixture, view) = Rows();
        using var scope = fixture;

        Assert.Equal(0f, view.ScrollTop, 1);

        fixture.Document.Move(fixture.Document.Create("div", view.Content, "inserted", "row"), 0);
        fixture.Update();

        Assert.Equal(0f, view.ScrollTop, 1);
    }

    /// <summary>Content that grows below the offset moves nothing.</summary>
    /// <remarks>
    ///     The other instrument check: a correction that keyed off the content's total height rather
    ///     than off an element's position would fire here, and appending to a list would drag the
    ///     reader down it.
    /// </remarks>
    [Fact]
    public void Content_appended_below_the_viewport_moves_nothing() {
        var (fixture, view) = Rows();
        using var scope = fixture;

        view.ScrollTop = 80f;
        fixture.Update();

        fixture.Document.Create("div", view.Content, "appended", "row");
        fixture.Update();

        Assert.Equal(80f, view.ScrollTop, 1);
    }
}
