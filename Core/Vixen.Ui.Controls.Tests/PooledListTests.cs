// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A virtualised list whose row template is a build body rather than hand-written C#.</summary>
/// <remarks>
///     <para>
///         <b><c>BuildContext.Pool</c> is the runtime half of
///         <a href="https://github.com/Rikarin/Vixen/issues/758">#758</a></b>, and the half a markup
///         spelling would compile to. <c>VirtualListReachTests</c> established what was actually
///         true — the control is reachable from a <c>.vxml</c> through <c>use=</c>, and the gap is
///         that a row <i>template</i> had no construct — so this closes the part of that gap which
///         is not syntax: a slot's subtree is built by the same context that builds everything
///         else, with <c>@if</c>, <c>refs</c>, a nested loop and ordinary bindings available in it.
///     </para>
///     <para>
///         ⚠ <b>The body runs once per SLOT and not once per item, and that is the assertion that
///         matters.</b> A pooled list and a correct list that allocated ten thousand boxes look
///         identical on screen and differ by four orders of magnitude in what they built, so
///         <c>Built</c> — counted, never timed — is what tells them apart.
///     </para>
///     <para>
///         Sabotages, each reddening its own fact and no other: handing the body the item as an
///         <c>int</c> rather than a signal (every row shows what it was built for, for ever, while
///         scrolling perfectly); binding a slot only when its item changed; opening no region per
///         slot at all.
///     </para>
/// </remarks>
public class PooledListTests {
    const int Items = 10_000;

    /// <summary>The viewport and the row height, so the pool's size is arithmetic rather than luck.</summary>
    const string Css = "virtualizing-panel { width: 300px; height: 200px; --row-height: 20px; }";

    [Fact]
    public void A_pooled_list_builds_one_body_per_slot_and_not_one_per_item() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        Assert.Equal(Items, sheet.List.Count);
        Assert.InRange(sheet.List.Rows.Count, 10, 20);

        // The whole claim: ten thousand items, a dozen bodies — and every element in the scroller
        // is one of them, so nothing was built beside the pool either.
        Assert.Equal(sheet.List.Rows.Count, sheet.Built);
        Assert.Equal(sheet.List.Rows.Count, sheet.List.Scroller.Content.Children.Count);

        // And the bodies that ran are showing the right items, which is what the signal buys.
        Assert.Equal("row 0", sheet.List.Rows[0].Text);
        Assert.Equal("row 1", sheet.List.Rows[1].Text);
    }

    /// <summary>Scrolling re-reads the slot's signal without re-running its body.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure a plain <c>int</c> would produce is invisible to a count.</b> The pool
    ///     stays the same size either way and scrolls perfectly; what changes is that every row goes
    ///     on showing the item it was built for. So this asserts the text after the scroll as well
    ///     as the size of the pool.
    /// </remarks>
    [Fact]
    public void Scrolling_rebinds_a_slot_without_rebuilding_it() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        var pool = sheet.List.Rows.Count;
        var built = sheet.Built;

        sheet.Bound.Clear();
        sheet.List.Scroller.ScrollTop = 2_000f;

        // ⚠ Two passes, and the second is not slack. The control grows and binds its pool from
        // `LayoutFinished` — after the pass that measured the viewport — so the signals a scroll
        // writes are written at the END of one update and the bindings that read them run in the
        // next one. One pass leaves the rows showing the items they had, which is what a test that
        // asserted after a single update would have called a bug in `Pool`.
        fixture.Update();
        fixture.Update();

        // 2 000 pixels at 20 a row is item 100, and the pool starts `Overscan` rows above it.
        var top = 100 - VirtualizingPanel.Overscan;

        Assert.Equal(pool, sheet.List.Rows.Count);
        Assert.Equal(built, sheet.Built);
        Assert.Equal(top, sheet.List.FirstItem);
        Assert.Contains(100, sheet.Bound);
        Assert.Equal("row " + top, sheet.List.Rows[0].Text);
    }

    /// <summary>A count that is a signal's is re-read, because the body of `Pool` is an effect.</summary>
    [Fact]
    public void The_list_follows_a_count_that_changes() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var sheet = Sheet(fixture);

        var built = sheet.Built;

        Assert.Equal(Items, sheet.List.Count);

        sheet.Items.Value = [.. Enumerable.Range(0, 5).Select(i => "short " + i)];
        fixture.Update();
        fixture.Update();

        Assert.Equal(5, sheet.List.Count);
        Assert.Equal("short 0", sheet.List.Rows[0].Text);

        // ⚠ And the pool was re-used rather than re-made, which is the half that says the count is
        // its own effect. Reading it inside the enclosing `use=` effect instead would re-run the
        // whole of `Fill` on every change — a second registration over the same control, a fresh
        // slot table that knows none of the rows already on screen, and a list that stops updating
        // as soon as anything the body reads changes.
        Assert.Equal(built, sheet.Built);
        Assert.Equal(1, sheet.Fills);
    }

    /// <summary>
    ///     ⚠ <b>The instrument.</b> Nothing above distinguishes "the body filled the control" from
    ///     "the control does this by itself": a panel nobody filled has a count of zero and no rows.
    /// </summary>
    [Fact]
    public void The_same_tag_without_the_pool_lists_nothing() {
        using var fixture = new ControlFixture(400f, 300f, Css);
        var panel = fixture.Document.Root.Add<VirtualizingPanel>();

        fixture.Update();

        Assert.Equal(0, panel.Count);
        Assert.Empty(panel.Rows);
    }

    static PooledListSheet Sheet(ControlFixture fixture) {
        var sheet = new PooledListSheet();

        sheet.Items.Value = [.. Enumerable.Range(0, Items).Select(i => "row " + i)];
        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        return sheet;
    }
}
