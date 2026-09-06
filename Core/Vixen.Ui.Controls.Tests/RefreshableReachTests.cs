// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>How much of `.refreshable` markup already has, measured on a list rather than a widget.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#767 calls `.refreshable` a row with nothing behind it, and says its open question is
///         what a desktop pull-to-refresh gesture is. That question is real and it is not this
///         row.</b> A gesture is a <i>trigger</i>. What a refresh <i>is</i>, is a re-request of work
///         that is already cancellable — and after <c>BuildContext.Load</c> landed under #768 that
///         is a thing this framework has. <c>Load</c>'s request expression runs with tracking on, so
///         a signal read inside it re-asks when it is bumped, and the superseded run's token is
///         cancelled by the same machinery that cancels it on unmount.
///     </para>
///     <para>
///         <b>So this file is a measurement and not a feature.</b> It narrows the row the way
///         <see cref="SearchableReachTests" /> narrowed its sibling: what `.refreshable` would add
///         is the gesture and the placement of an indicator, and the part two audits called its
///         substrate is written, in
///         <c>Core/Vixen.Ui.Controls.Tests/Markup/RefreshableSheet.vxml</c>, in a `@for`, an `@if`
///         and one button.
///     </para>
///     <para>
///         ⚠ <b>Asserted about the rows and about a deterministic counter, per the issue's own "done
///         looks like".</b> Nothing here sleeps and nothing waits on elapsed time: a load finishes
///         because the test opens its gate, and "a load started" is <c>Starts</c> rather than an
///         interval nobody can calibrate.
///     </para>
/// </remarks>
public class RefreshableReachTests {
    [Fact]
    public void A_list_that_has_loaded_shows_its_rows_and_says_it_is_no_longer_loading() {
        RefreshableSheet.Reset();

        using var ui = Sheet(out var sheet);

        // The instrument: in flight before anything is opened, and the list says so.
        Assert.Equal(1, RefreshableSheet.Starts);
        Assert.True(sheet.Rows.IsLoading);
        Assert.Single(Busy(sheet));

        RefreshableSheet.Gate.SetResult(["one", "two"]);
        Settle(ui);

        Assert.Equal(AsyncStatus.Success, sheet.Rows.Status);
        Assert.Equal(["one", "two"], Rows(sheet));
        Assert.Empty(Busy(sheet));
    }

    /// <summary>
    ///     ⚠ <b>The assertion the row is actually about: pressing the trigger re-runs the work and
    ///     the list becomes the new answer.</b>
    /// </summary>
    /// <remarks>
    ///     Pressed rather than written to — the button is what a person reaches, and a test that
    ///     bumped the signal would never have exercised the half that turns a gesture into a
    ///     re-request. What the framework would add here is which gesture; that it is <i>a</i>
    ///     gesture is already true.
    /// </remarks>
    [Fact]
    public void Pressing_the_trigger_asks_again_and_the_new_answer_replaces_the_old() {
        RefreshableSheet.Reset();

        using var ui = Sheet(out var sheet);

        RefreshableSheet.Gate.SetResult(["one"]);
        Settle(ui);

        Assert.Equal(["one"], Rows(sheet));
        Assert.Equal(1, RefreshableSheet.Starts);

        RefreshableSheet.Gate = new();
        sheet.Refresh.Raise(new ClickEvent());
        ui.Frame();

        // The re-request happened and is in flight: a second load started, and the list says so
        // while still showing what it had.
        Assert.Equal(2, RefreshableSheet.Starts);
        Assert.True(sheet.Rows.IsLoading);
        Assert.Single(Busy(sheet));

        RefreshableSheet.Gate.SetResult(["one", "three"]);
        Settle(ui);

        Assert.Equal(["one", "three"], Rows(sheet));
        Assert.Empty(Busy(sheet));
    }

    /// <summary>
    ///     ⚠ <b>The half a hand-written refresh gets wrong, and the reason this row's substrate had
    ///     to be <c>Load</c> rather than a task the panel starts: a second request cancels the
    ///     first.</b>
    /// </summary>
    /// <remarks>
    ///     Observed from the token rather than inferred from a value that never arrived, and counted
    ///     rather than timed. A panel that started its own task on each press would leave the first
    ///     one running and let whichever finished last win — which is the failure that looks like a
    ///     list flickering back to stale rows and cannot be reproduced on demand.
    /// </remarks>
    [Fact]
    public void A_refresh_while_one_is_in_flight_cancels_the_one_it_supersedes() {
        RefreshableSheet.Reset();

        using var ui = Sheet(out var sheet);

        Assert.Equal(0, RefreshableSheet.Cancellations);

        RefreshableSheet.Gate = new();
        sheet.Refresh.Raise(new ClickEvent());
        ui.Frame();

        Assert.Equal(2, RefreshableSheet.Starts);
        Assert.Equal(1, RefreshableSheet.Cancellations);

        // And the answer the cancelled one was about to give lands nowhere.
        RefreshableSheet.Gate.SetResult(["late"]);
        Settle(ui);

        Assert.Equal(["late"], Rows(sheet));
        Assert.Equal(1, RefreshableSheet.Cancellations);
    }

    /// <summary>
    ///     ⚠ <b>The instrument for the two above: with the trigger never pressed, nothing asks
    ///     again.</b> A `Starts` that counted builds rather than requests would make every assertion
    ///     above green for the wrong reason.
    /// </summary>
    [Fact]
    public void A_list_nobody_refreshes_loads_once() {
        RefreshableSheet.Reset();

        using var ui = Sheet(out var sheet);

        RefreshableSheet.Gate.SetResult(["one"]);
        Settle(ui);

        ui.Frame();
        ui.Frame();

        Assert.Equal(1, RefreshableSheet.Starts);
        Assert.Equal(["one"], Rows(sheet));
    }

    /// <summary>What each <c>refresh-row</c> is showing.</summary>
    /// <remarks>
    ///     Through the row's child rather than off the row, for
    ///     <see cref="SearchableReachTests" />'s reason: content interpolation emits a text element,
    ///     so the row's own <c>Text</c> is empty.
    /// </remarks>
    static string[] Rows(RefreshableSheet sheet) => [
        .. Sheet(sheet)
            .Children.Where(child => child.Tag == "refresh-row")
            .Select(row => row.Children.Count == 0 ? row.Text ?? "" : row.Children[0].Text ?? "")
    ];

    static UiElement[] Busy(RefreshableSheet sheet) =>
        [.. Sheet(sheet).Children.Where(child => child.Tag == "refresh-busy")];

    static UiElement Sheet(RefreshableSheet sheet) => sheet.Root.Children[0];

    /// <summary>Drains the post the load made, then the frame that renders what it woke.</summary>
    /// <remarks>
    ///     Two, and neither is a wait — <see cref="AsyncArrivalTests" />'s bargain: completing the
    ///     gate runs the continuation on this thread, which posts the result to the document's
    ///     scheduler; the first frame drains the post and the second runs the bindings it woke.
    /// </remarks>
    static void Settle(UiTest ui) {
        ui.Frame();
        ui.Frame();
    }

    static UiTest Sheet(out RefreshableSheet sheet) {
        var ui = ControlHarness.Open(400f, 300f);

        sheet = BuildContext.Build<RefreshableSheet>(ui.Document, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
