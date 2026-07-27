// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Arrow navigation over a laid-out tree.</summary>
/// <remarks>
///     Every one of these lays the document out first, because the whole question is about where
///     things ended up. A test that skipped <see cref="UiDocument.Update" /> would be asking about a
///     stack of zero boxes at the origin and would pass whatever the beam model did.
/// </remarks>
public class NavigationTests {
    /// <summary>A three-by-three grid of forty-by-twenty cells, wrapped by flexbox.</summary>
    /// <remarks>
    ///     Cells abut exactly, with no gap, because that is the case a non-strict overlap test gets
    ///     wrong: the cell diagonally below shares an edge with this one, so "overlaps" has to mean
    ///     more than "touches" or Down moves sideways.
    /// </remarks>
    static (UiDocument Document, UiElement[] Cells) Grid() {
        var document = new UiDocument(120f, 60f);

        document.Load("""
            root { width: 120px; height: 60px; flex-direction: row; flex-wrap: wrap; }
            cell { width: 40px; height: 20px; }
        """);

        var cells = new UiElement[9];
        for (var i = 0; i < cells.Length; i++) {
            cells[i] = document.Root.Add("cell");
            cells[i].Focusable = true;
        }

        document.Update();
        return (document, cells);
    }

    [Fact]
    public void An_arrow_moves_to_the_neighbour_it_points_at() {
        var (document, cells) = Grid();
        using var owner = document;

        // 0 1 2
        // 3 4 5
        // 6 7 8
        Assert.Same(cells[5], document.FindInDirection(cells[4], NavigationDirection.Right));
        Assert.Same(cells[3], document.FindInDirection(cells[4], NavigationDirection.Left));
        Assert.Same(cells[1], document.FindInDirection(cells[4], NavigationDirection.Up));
        Assert.Same(cells[7], document.FindInDirection(cells[4], NavigationDirection.Down));
    }

    [Fact]
    public void The_diagonal_neighbour_is_not_below() {
        var (document, cells) = Grid();
        using var owner = document;

        // ⚠ Cell 4 shares its bottom-left corner with cell 6 and its bottom-right with cell 8, so
        // every one of 6, 7 and 8 starts past cell 4's bottom edge and all three are candidates.
        // Only 7 shares any width with it, and a weighted score would have to be tuned until the
        // other two lost. The beam does not rank them at all: out of the beam is out.
        Assert.Same(cells[7], document.FindInDirection(cells[4], NavigationDirection.Down));

        // The same edge-sharing, one row up, where the wrong answer would be cell 0 or cell 2.
        Assert.Same(cells[1], document.FindInDirection(cells[4], NavigationDirection.Up));
    }

    [Fact]
    public void Anything_in_the_beam_beats_anything_outside_it() {
        using var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; }
            box { position: absolute; width: 20px; height: 20px; }
            #origin { top: 0px; left: 0px; }

            /* Directly below, and a long way off. */
            #far { top: 150px; left: 0px; }

            /* Just below, and just to the side — close enough that any weighting small enough to
               be called a tie-break would pick it. */
            #near { top: 21px; left: 21px; }
        """);

        var origin = document.Root.Add("box", "origin");
        var far = document.Root.Add("box", "far");
        var near = document.Root.Add("box", "near");

        foreach (var element in (UiElement[]) [origin, far, near]) {
            element.Focusable = true;
        }

        document.Update();

        Assert.Same(far, document.FindInDirection(origin, NavigationDirection.Down));
    }

    [Fact]
    public void With_nothing_in_the_beam_the_nearest_wins() {
        using var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; }
            box { position: absolute; width: 20px; height: 20px; }
            #origin { top: 0px; left: 0px; }

            /* Just below and just past the origin's right edge, and very wide — so its nearest
               corner is close and its centre is a long way off. */
            #wide { top: 30px; left: 30px; width: 170px; }

            /* Much further down, and narrow enough that its centre is nearer than the wide one's. */
            #narrow { top: 100px; left: 25px; }
        """);

        var origin = document.Root.Add("box", "origin");
        var wide = document.Root.Add("box", "wide");
        var narrow = document.Root.Add("box", "narrow");

        foreach (var element in (UiElement[]) [origin, wide, narrow]) {
            element.Focusable = true;
        }

        document.Update();

        // Neither shares any width with the origin, so the beam is empty and the fallback decides.
        // ⚠ A straight line between the two <i>rectangles</i>, not between their centres: these two
        // are placed so the metrics disagree, and the centre one says the thing eighty pixels away
        // is closer than the thing ten pixels away because it is narrower. Distance to a shape is
        // distance to the shape.
        Assert.Same(wide, document.FindInDirection(origin, NavigationDirection.Down));
    }

    [Fact]
    public void An_arrow_stops_at_the_edge_rather_than_wrapping() {
        var (document, cells) = Grid();
        using var owner = document;

        Assert.Null(document.FindInDirection(cells[2], NavigationDirection.Right));
        Assert.Null(document.FindInDirection(cells[0], NavigationDirection.Up));

        // Which is the point: Tab is a cycle because an order has no far end, and an arrow points
        // at somewhere. Holding Down in a list that wrapped would never settle.
        document.Focus(cells[8]);
        Assert.False(document.MoveFocus(NavigationDirection.Down));
        Assert.Same(cells[8], document.Focused);
    }

    [Fact]
    public void An_elements_own_children_are_not_in_any_direction_from_it() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            card { width: 100px; height: 40px; }
            button { width: 30px; height: 20px; }
        """);

        var card = document.Root.Add("card");
        card.Focusable = true;

        var inside = card.Add("button");
        inside.Focusable = true;

        document.Update();

        // A child is inside its parent, so it is past none of its parent's edges and the direction
        // test excludes it without anything having to say so. Entering a group is a separate idea
        // from moving between things, and conflating them makes Right mean two things.
        Assert.Null(document.FindInDirection(card, NavigationDirection.Right));
        Assert.Null(document.FindInDirection(card, NavigationDirection.Down));
    }

    [Fact]
    public void A_zero_sized_element_is_not_a_destination() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; flex-direction: row; }
            box { width: 20px; height: 20px; }
            hidden { display: none; }

            /* ⚠ Zero on one axis and not the other, which is the case the guard is actually for.
               A box that is 0×0 shares no width with anything, so the beam already excludes it and
               a test using only `display: none` passes with the guard deleted — which the first
               version of this test did. This one is full height, so it is squarely in the beam,
               exactly as near as the real destination, and earlier in document order. */
            collapsed { width: 0px; height: 20px; }
        """);

        var origin = document.Root.Add("box");
        origin.Focusable = true;

        var invisible = document.Root.Add("hidden");
        invisible.Focusable = true;

        var collapsed = document.Root.Add("collapsed");
        collapsed.Focusable = true;

        var real = document.Root.Add("box");
        real.Focusable = true;

        document.Update();

        // Both of the ways an element ends up with nothing to show: `display: none`, which reaches
        // here as a zero box rather than as a keyword the same way it does in the draw list, and a
        // box flexed down to no width at all. An arrow that landed on either would move the focus
        // somewhere invisible, and the user's next press would start from a place they cannot see.
        Assert.Same(real, document.FindInDirection(origin, NavigationDirection.Right));
    }

    [Fact]
    public void An_arrow_stays_inside_a_focus_scope() {
        using var document = new UiDocument(200f, 100f);

        document.Load("""
            root { width: 200px; height: 100px; flex-direction: row; }
            dialog { width: 100px; height: 100px; flex-direction: row; }
            box { width: 40px; height: 40px; }
        """);

        var dialog = document.Root.Add("dialog");
        dialog.IsFocusScope = true;

        var inside = dialog.Add("box");
        inside.Focusable = true;

        var outside = document.Root.Add("box");
        outside.Focusable = true;

        document.Update();
        document.Focus(inside);

        // The element outside is directly to the right and in the beam, and it is still not where
        // the arrow goes — a dialog is modal to the arrow keys for the same reason it is modal to
        // Tab.
        Assert.False(document.MoveFocus(NavigationDirection.Right));
        Assert.Same(inside, document.Focused);
    }

    [Fact]
    public void An_element_skipped_by_tab_is_still_reachable_by_an_arrow() {
        var (document, cells) = Grid();
        using var owner = document;

        cells[5].TabIndex = -1;

        // Negative means "focusable but not a Tab stop", and an arrow is not Tab. A pane that can
        // hold the focus without being on the way round is the whole reason the value exists.
        Assert.DoesNotContain(cells[5], UiDocument.TabOrder(document.Root));
        Assert.Same(cells[5], document.FindInDirection(cells[4], NavigationDirection.Right));
    }

    [Fact]
    public void A_non_focusable_element_is_navigated_past_rather_than_into() {
        using var document = new UiDocument(120f, 20f);

        document.Load("""
            root { width: 120px; height: 20px; flex-direction: row; }
            box { width: 40px; height: 20px; }
        """);

        var origin = document.Root.Add("box");
        var decoration = document.Root.Add("box");
        var destination = document.Root.Add("box");

        origin.Focusable = true;
        destination.Focusable = true;

        document.Update();

        // Most of a real interface is not focusable — the labels, the separators, the panel the
        // buttons sit in. An arrow crosses them rather than stopping on them, so the distance that
        // matters is to the next thing that can hold the focus and not to the next thing there is.
        Assert.Same(destination, document.FindInDirection(origin, NavigationDirection.Right));
        Assert.NotSame(decoration, document.Focused);
    }

    [Fact]
    public void The_first_arrow_press_gets_into_the_interface() {
        var (document, cells) = Grid();
        using var owner = document;

        // Nothing focused means there is no origin, so there is no direction either. Refusing to
        // move would leave a keyboard-only user with no way in.
        Assert.True(document.MoveFocus(NavigationDirection.Up));
        Assert.Same(cells[0], document.Focused);
    }

    [Fact]
    public void Moving_the_focus_by_arrow_is_moving_the_focus() {
        var (document, cells) = Grid();
        using var owner = document;

        var gained = 0;
        cells[5].AddHandler<FocusEvent>((_, args) => gained += args.Gained ? 1 : 0);

        document.Focus(cells[4]);
        Assert.True(document.MoveFocus(NavigationDirection.Right));

        Assert.Same(cells[5], document.Focused);
        Assert.Equal(1, gained);
    }
}
