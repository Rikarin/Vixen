// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A panel that loads something when it appears, and stops when it leaves.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The substrate was built and nothing fed it.</b>
///         <see cref="AsyncComputed{TRequest,T}" /> has answered the three hard questions — a tracked
///         request, an untracked load, results posted back on the owning thread — since it was
///         written, and had no production caller anywhere in the tree: every reference to it was its
///         own file, its own tests, or a <c>&lt;see cref&gt;</c>. <c>BuildContext.Load</c> is what
///         gives it one, and <c>Component.Context</c> is what lets a <c>.vxml</c>'s
///         <c>OnComposed</c> reach it.
///     </para>
///     <para>
///         Written from committed markup rather than from <c>BuildContext</c> calls, because the
///         claim is that the arrival hook a <c>.vxml</c> actually has can reach this. A hand-written
///         <c>Build</c> body is the half that was never the problem: it is handed the context.
///     </para>
/// </remarks>
public class AsyncArrivalTests {
    [Fact]
    public void A_panel_that_appears_starts_its_load_and_reports_that_it_is_loading() {
        AsyncGuest.Reset();

        using var ui = Host(out var host);

        Assert.Equal(1, AsyncGuest.Starts);
        Assert.True(host.Guest.State.IsLoading);
        Assert.Equal(0, AsyncGuest.Cancellations);
    }

    /// <summary>The result arrives on the owning thread and the binding sees it.</summary>
    /// <remarks>
    ///     ⚠ Driven by frames and by the completion itself rather than by a wall-clock wait: the
    ///     gate is opened, the continuation is drained, and the assertion is about what the document
    ///     holds afterwards. Nothing here sleeps.
    /// </remarks>
    [Fact]
    public void A_load_that_finishes_reaches_the_panel_it_was_started_for() {
        AsyncGuest.Reset();

        using var ui = Host(out var host);

        AsyncGuest.Gate.SetResult("ready");
        Settle(ui);

        Assert.Equal(AsyncStatus.Success, host.Guest.State.Status);
        Assert.Equal("ready", host.Guest.State.Value);
        Assert.Equal("ready", Element(host).Text);
        Assert.Equal(0, AsyncGuest.Cancellations);
    }

    /// <summary>
    ///     ⚠ The assertion this issue is actually about: the work is cancelled when the panel leaves,
    ///     observed from the token rather than inferred from a value that never arrived.
    /// </summary>
    [Fact]
    public void A_panel_unmounted_mid_flight_has_its_load_cancelled() {
        AsyncGuest.Reset();

        using var ui = Host(out var host);

        Assert.Equal(0, AsyncGuest.Cancellations);

        host.Shown.Value = false;
        ui.Frame();

        Assert.Equal(1, AsyncGuest.Cancellations);

        // And the answer that was about to arrive lands nowhere: opening the gate afterwards is a
        // completion with no panel to reach, which is what the token registration is protecting.
        AsyncGuest.Gate.SetResult("late");
        Settle(ui);

        Assert.Equal(1, AsyncGuest.Cancellations);
    }

    /// <summary>A load that throws is a value on the signal, not an effect the scheduler suspended.</summary>
    /// <remarks>
    ///     ⚠ <c>Effect.Run</c> catches, suspends and logs — the arrangement that made a mistyped
    ///     <c>bind:</c> invisible for months — so a failure that only reached the log would be a
    ///     panel that silently stopped. It has to be renderable, and here it is rendered.
    /// </remarks>
    [Fact]
    public void A_load_that_faults_lands_on_the_signal_where_markup_can_draw_it() {
        AsyncGuest.Reset();

        using var ui = Host(out var host);

        AsyncGuest.Gate.SetException(new InvalidOperationException("no"));
        Settle(ui);

        Assert.Equal(AsyncStatus.Failure, host.Guest.State.Status);
        Assert.Equal("failed", Element(host).Text);
    }

    /// <summary>Drains the post the load made, then the frame that renders what it woke.</summary>
    static void Settle(UiTest ui) {
        // Two, and neither is a wait. Completing the gate runs `AsyncComputed`'s continuation
        // synchronously on this thread, which posts the result to the document's scheduler; the
        // first frame drains that post and the second runs the binding it woke.
        ui.Frame();
        ui.Frame();
    }

    /// <summary>The element the panel's own binding writes into.</summary>
    /// <remarks>
    ///     Two levels down: <c>Root</c> is the host element the component's tag made, and the
    ///     markup's own <c>&lt;async-guest&gt;</c> is what hangs off it.
    /// </remarks>
    static UiElement Element(AsyncHost host) =>
        host.Guest.Root.Children.SelectMany(child => child.Children).Single(child => child.Tag == "async-state");

    static UiTest Host(out AsyncHost host) {
        var ui = ControlHarness.Open(400f, 300f);

        host = new();

        BuildContext.BuildInto(host, ui.Document, ui.Document.Root);
        ui.Frame();

        return ui;
    }
}
