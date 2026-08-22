// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a parent owning a window can still have its children moved around.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A surface root is in the element tree and the style tree and not in the layout
///         tree.</b> <c>UiDocument.CreateSurface</c> takes it out of the layout tree's child list on
///         purpose — a second window is not a flex item of the first — and leaves it in the other
///         two, because the style tree needs the parent link for inheritance and the element tree is
///         what a routed event climbs. So a parent that owns <i>n</i> windows has <i>n</i> more
///         element children than layout children, and the two lists stop agreeing about what
///         position 2 means.
///     </para>
///     <para>
///         ⚠ <b>Both writers that convert one into the other had it wrong, and the second is the
///         one worth the file.</b> <c>Reparent</c> threw outright, which at least said something;
///         <c>Move</c> threw only when the index ran past the shorter list and otherwise put the
///         element in the wrong place quietly. <c>Move</c> is reached by <c>HotReloadHost</c>
///         putting a rebuilt component back where the old one was, by <c>MenuPresenter</c> and
///         <c>ToolbarPresenter</c> pulling their strip back above the workspace, and by every
///         markup region that inserts at a position — so the quiet half had four callers.
///     </para>
///     <para>
///         ⚠ <b>Written against geometry rather than against child counts.</b> An off-by-one that
///         still fits inside the shorter list does not throw and does not change any count; the only
///         thing that notices is where the boxes end up. The heights below are all different for
///         that reason — with equal heights every ordering of the column looks the same.
///     </para>
/// </remarks>
public class SurfaceIndexTests {
    /// <summary>A column of three differently sized boxes, so that order is readable off the tops.</summary>
    const string Column = """
        root { flex-direction: column; }
        ui-surface { flex-direction: column; }
        .a { height: 20px; }
        .b { height: 30px; }
        .c { height: 40px; }
        """;

    static UiDocument Document() {
        var document = new UiDocument(1000f, 600f);
        document.Load(Column);

        return document;
    }

    /// <summary>
    ///     ⚠ <b>Docking a floating window's panels back into their old home.</b>
    /// </summary>
    /// <remarks>
    ///     The operation surfaces exist for. <c>UiSurface</c> argues at length that a torn-off window
    ///     is a surface rather than a second document precisely so that moving a panel between
    ///     windows is a <c>Reparent</c>, which preserves the scroll offset, the selection and the
    ///     focus — and until this test the headline case threw, because the home the panel came from
    ///     is the element that owns the floating window's surface root.
    /// </remarks>
    [Fact]
    public void A_panel_docks_back_into_the_parent_that_owns_the_window() {
        using var document = Document();

        var panel = document.Root.Add("div", null, "a");
        var floating = document.CreateSurface(400f, 300f);

        document.Update();

        document.Reparent(panel, floating.Root);
        document.Update();

        // The floating window's own rectangle, which is the point of the tear-off.
        Assert.Equal(400f, panel.Width, 0.001f);

        // ⚠ And back. `Root`'s element child list holds the surface root and its layout child list
        // does not, so the default index — one past the last element child — is one past the end of
        // the layout list. This is the call that threw.
        document.Reparent(panel, document.Root);
        document.Update();

        Assert.Same(document.Root, panel.Parent);
        Assert.Equal(1000f, panel.Width, 0.001f);
        Assert.Equal(0f, panel.Top, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>An insertion after a window lands before the sibling that follows it.</b>
    /// </summary>
    /// <remarks>
    ///     The quiet half of the same conversion. The element index here is 2 and the layout index is
    ///     1, and a layout list of two accepts an insertion at 2 without complaint — so the failure
    ///     is not an exception but a box that comes last when it was asked to come second.
    /// </remarks>
    [Fact]
    public void A_reparent_past_a_window_lands_where_the_element_tree_says() {
        using var document = Document();

        var first = document.Root.Add("div", null, "a");
        document.CreateSurface(400f, 300f);
        var last = document.Root.Add("div", null, "b");

        // Somewhere to be moved out of. Under `first`, whose height is declared, so its presence
        // there does not move anything.
        var moved = first.Add("div", null, "c");

        document.Update();

        // Element children are [first, ui-surface, moved, last]; layout children [first, moved, last].
        document.Reparent(moved, document.Root, 2);
        document.Update();

        Assert.Equal(3, document.Root.Children.Count(child => child.SurfaceRoot is null));
        Assert.Equal(0f, first.Top, 0.001f);
        Assert.Equal(20f, moved.Top, 0.001f);
        Assert.Equal(60f, last.Top, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b><c>Move</c> makes the same conversion and got it wrong the same way.</b>
    /// </summary>
    /// <remarks>
    ///     Fixing <c>Reparent</c> alone would have left this, and this is the one with callers:
    ///     <c>HotReloadHost.Reload</c> moves a rebuilt component's root back to the index the old one
    ///     held, and a shell whose chrome owns a floating window is exactly a parent with two child
    ///     counts. The last valid element index is one past the last valid layout index, so the
    ///     move furthest to the right is the one that throws.
    /// </remarks>
    [Fact]
    public void A_move_to_the_last_position_counts_the_layout_list_and_not_the_element_one() {
        using var document = Document();

        var first = document.Root.Add("div", null, "a");
        var second = document.Root.Add("div", null, "b");
        document.CreateSurface(400f, 300f);

        document.Update();
        Assert.Equal(0f, first.Top, 0.001f);

        // Three element children, two layout ones. Index 2 is the last element position and one past
        // the last layout position.
        document.Move(first, 2);
        document.Update();

        Assert.Equal(0f, second.Top, 0.001f);
        Assert.Equal(30f, first.Top, 0.001f);
    }

    /// <summary>
    ///     ⚠ <b>A surface root that is itself moved goes back into no layout child list.</b>
    /// </summary>
    /// <remarks>
    ///     The other half of the invariant, and the one a fix that only adjusted the index would have
    ///     broken: <c>Reparent</c> removes from the old layout parent and inserts into the new one,
    ///     and a surface root was never in the first list — so the removal did nothing and the
    ///     insertion would have put a whole window into its new owner's flex line. A docking host
    ///     reparents the mount a window hangs off whenever the control that opened it moves.
    /// </remarks>
    [Fact]
    public void A_window_that_is_reparented_stays_out_of_its_new_parents_layout() {
        using var document = Document();

        var host = document.Root.Add("div", null, "a");
        var sibling = document.Root.Add("div", null, "b");
        var floating = document.CreateSurface(400f, 300f);
        var content = floating.Root.Add("div", null, "c");

        document.Update();

        document.Reparent(floating.Root, host);
        document.Update();

        Assert.Same(host, floating.Root.Parent);
        Assert.Equal(0, document.Layout.GetChildCount(host.LayoutNode));

        // The declared heights, undisturbed: a window laid out inside `host` would have grown it.
        Assert.Equal(0f, host.Top, 0.001f);
        Assert.Equal(20f, sibling.Top, 0.001f);

        // And the window is still a window — its own size, and still the surface its contents answer.
        Assert.Equal(400f, floating.Root.Width, 0.001f);
        Assert.Same(floating, document.SurfaceOf(content));
    }

    /// <summary>
    ///     ⚠ <b>And a surface root reordered among its siblings leaves the layout alone.</b>
    /// </summary>
    /// <remarks>
    ///     <c>Move</c>'s version of the case above. It matters for the style tree — <c>:nth-child</c>
    ///     counts the surface root, because the style tree holds it — so the move is not a no-op; it
    ///     is a no-op in exactly one of the three stores.
    /// </remarks>
    [Fact]
    public void A_window_reordered_among_its_siblings_leaves_the_layout_alone() {
        using var document = Document();

        var first = document.Root.Add("div", null, "a");
        var second = document.Root.Add("div", null, "b");
        var floating = document.CreateSurface(400f, 300f);

        document.Update();

        document.Move(floating.Root, 0);
        document.Update();

        Assert.Equal(0, floating.Root.IndexInParent);
        Assert.Equal(2, document.Layout.GetChildCount(document.Root.LayoutNode));
        Assert.Equal(0f, first.Top, 0.001f);
        Assert.Equal(20f, second.Top, 0.001f);
        Assert.Equal(400f, floating.Root.Width, 0.001f);
    }
}
