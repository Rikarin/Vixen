// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The queue every platform answer comes back to the frame thread through.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Deferred.When</c> used to make a call that had <em>already answered</em> wait on
///         the thread pool, and that is #1179.</b> <c>Task.ContinueWith</c> against
///         <see cref="TaskScheduler.Default" /> schedules a pool work item even for a task that is
///         already complete — it does not run inline — so the hand-off back to the frame thread cost
///         a pool schedule whether or not anything had been waited for. On an idle machine that is
///         microseconds; under fifteen concurrent test assemblies it is however long the queue is.
///     </para>
///     <para>
///         ⚠ <b>Which is the answer to the question #1179 left open, and it is the second of the two
///         candidates rather than the first.</b> That issue asked whether the delay was
///         <c>SweepAsync</c>'s <c>Task.Run</c> or the deferred pump.
///         <c>EditorApplication.UseSourceControl</c> sets <c>sought</c>, so the harness's sweep never
///         reaches the <c>Task.Run</c> at all, and the <c>Fake</c> provider answers with an
///         already-completed <c>ValueTask</c> — so on the failing path there was <b>no</b>
///         asynchrony left except the one this queue imposed on itself.
///     </para>
///     <para>
///         <b>Order rather than elapsed time, which is what makes these assertions mean anything.</b>
///         Every one of them is "the continuation is available at the next pump", counted in pumps.
///         The one ceiling below is on iterations of a spin and its comment says plainly that it is a
///         hang check.
///     </para>
/// </remarks>
public class DeferredTests {
    /// <summary>
    ///     A call that answered synchronously is on the frame thread at the very next pump.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A hundred rounds rather than one, and that is the instrument.</b> One round is a race
    ///     the old code could win: a pool schedule is microseconds and the <c>Pump</c> below is
    ///     nanoseconds away, so it would <em>usually</em> lose but not always, and a test that is
    ///     usually red is worse than no test. A hundred consecutive rounds it cannot win, because
    ///     winning one costs it a full pool round trip and it has to win every one of them.
    /// </remarks>
    [Fact]
    public void An_answer_that_was_already_there_is_pumped_on_the_next_frame() {
        var deferred = new Deferred();
        var seen = 0;

        for (var round = 0; round < 100; round++) {
            deferred.When(ValueTask.FromResult(round), answer => seen += answer, _ => seen = -1_000_000);
            deferred.Pump();

            Assert.Equal(round * (round + 1) / 2, seen);
        }
    }

    /// <summary>A synchronous throw reaches the failure handler, and only that one.</summary>
    /// <remarks>
    ///     ⚠ <b>The exception is unwrapped.</b> The asynchronous path reports
    ///     <c>GetBaseException()</c> rather than the <c>AggregateException</c> a faulted task carries,
    ///     and a fast path that handed back the wrapper instead would give every caller's message a
    ///     different shape depending on how quickly the platform answered — the kind of difference
    ///     that only shows up in a log nobody can reproduce.
    /// </remarks>
    [Fact]
    public void A_call_that_had_already_thrown_is_reported_and_unwrapped() {
        var deferred = new Deferred();
        var answered = 0;

        Exception? failure = null;

        deferred.When(
            ValueTask.FromException<int>(new InvalidOperationException("the picker is gone")),
            _ => answered++,
            thrown => failure = thrown
        );

        deferred.Pump();

        Assert.Equal(0, answered);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("the picker is gone", failure.Message);
    }

    /// <summary>A cancelled call runs neither handler.</summary>
    /// <remarks>
    ///     The behaviour the asynchronous path already had — it tests <c>IsFaulted</c> and
    ///     <c>IsCompletedSuccessfully</c> and does nothing when neither holds — asserted so that the
    ///     fast path cannot quietly start reporting cancellation as a failure and toasting about a
    ///     dialog somebody dismissed.
    /// </remarks>
    [Fact]
    public void A_cancelled_call_runs_neither_handler() {
        var deferred = new Deferred();
        var answered = 0;
        var failed = 0;

        deferred.When(ValueTask.FromCanceled<int>(new(canceled: true)), _ => answered++, _ => failed++);

        deferred.Pump();
        deferred.Pump();

        Assert.Equal(0, answered);
        Assert.Equal(0, failed);
    }

    /// <summary>
    ///     A call that had not answered yet still arrives, which is what stops the fast path being
    ///     the whole implementation.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Without this, deleting the asynchronous branch entirely would leave the other three
    ///     green.</b> The spin below is bounded because an unbounded one hangs the suite rather than
    ///     failing it; the bound is a hang check and not a budget, and the assertion is that the
    ///     answer arrived at all rather than that it arrived quickly.
    ///     <para>
    ///         ⚠ <b>It was a budget, and it read as a count.</b> A million <see cref="Deferred.Pump" />
    ///         calls over an empty queue is a few milliseconds of one core that never yields it, and
    ///         <see cref="Deferred.When" /> hands its continuation to
    ///         <see cref="TaskScheduler.Default" /> — so on a runner whose thread pool is saturated,
    ///         which CI's is by construction, the loop can run out before the pool ever schedules
    ///         the continuation. <c>Expected: 7, Actual: 0</c> on <c>test-windows-latest</c>,
    ///         2026-09-22, on a commit that touched none of this. The ceiling is wall clock now
    ///         <em>because it is a hang check</em> — thirty seconds is absurd for a thread-pool
    ///         hand-off and would be reached only by a queue that never receives the answer — and
    ///         each turn yields, so the thread that has the answer can run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_call_that_answers_later_still_reaches_the_frame_thread() {
        var deferred = new Deferred();
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;

        deferred.When(new ValueTask<int>(source.Task), answer => seen = answer, _ => seen = -1);

        deferred.Pump();
        Assert.Equal(0, seen);

        source.SetResult(7);

        var hung = Stopwatch.StartNew();

        while (seen == 0 && hung.Elapsed < TimeSpan.FromSeconds(30)) {
            deferred.Pump();

            // ⚠ The yield is the fix, not the ceiling. A spin that keeps its core cannot be
            // overtaken by the pool thread carrying the answer on a machine that has no spare one.
            Thread.Yield();
        }

        Assert.True(
            seen == 7,
            $"the answer never reached the frame thread: seen is {seen} after {hung.Elapsed.TotalSeconds:F1}s "
            + "of pumping. `Deferred.When` posts from a thread-pool continuation, so this is a queue "
            + "that never received it rather than a hand-off that was slow."
        );
    }
}
