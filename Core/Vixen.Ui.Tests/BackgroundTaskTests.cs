// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     The background-task model: what a pump applies, what cancellation means, and what an owner
///     that goes away owes the work it started.
/// </summary>
/// <remarks>
///     <para>
///         <b>These moved out of the editor with the model.</b> They used to live in
///         <c>Vixen.Editor.Ui.Tests.ServiceTests</c>, which is the shape of the problem this move
///         describes: an application-framework property pinned only by the one application that
///         happened to grow it.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is about the *queue*, not about the work.</b> The work runs on
///         the pool and this thread is the UI one; the only thing that makes a number visible is
///         <see cref="BackgroundTaskManager.Pump" />, so a test that asserted without pumping would
///         be asserting on a race and would pass on a fast machine.
///     </para>
/// </remarks>
public class BackgroundTaskTests {
    // ── What a pump applies ─────────────────────────────────────────────────

    [Fact]
    public async Task Work_that_finishes_leaves_the_list_after_a_pump() {
        using var tasks = new BackgroundTaskManager();
        var gate = new TaskCompletionSource();

        var task = tasks.Start("Importing", async _ => await gate.Task);
        Assert.True(tasks.IsBusy);

        gate.SetResult();
        await Drain(tasks, task);

        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Equal(1f, task.Progress);
        Assert.False(tasks.IsBusy);
    }

    [Fact]
    public async Task Work_that_throws_ends_as_failed_rather_than_taking_the_process_down() {
        using var tasks = new BackgroundTaskManager();
        var task = tasks.Start("Building", _ => throw new InvalidOperationException("no compiler"));

        await Drain(tasks, task);

        Assert.Equal(BackgroundTaskState.Failed, task.State);
        Assert.Equal("no compiler", task.Failure?.Message);
    }

    [Fact]
    public void Progress_reported_from_the_work_lands_on_the_pump_and_not_before() {
        using var tasks = new BackgroundTaskManager();
        var task = tasks.Begin("Importing");

        task.Report(0.5f, "textures");

        Assert.True(task.IsIndeterminate);
        Assert.Equal(0f, task.Progress);

        tasks.Pump();

        Assert.False(task.IsIndeterminate);
        Assert.Equal(0.5f, task.Progress);
        Assert.Equal("textures", task.Status);
    }

    [Fact]
    public void Overall_progress_ignores_the_tasks_that_have_not_said() {
        using var tasks = new BackgroundTaskManager();

        var known = tasks.Begin("Importing");
        tasks.Begin("Scanning");

        known.Report(0.8f);
        tasks.Pump();

        // Counting an indeterminate task as zero would leave three imports sitting at a third of
        // the way along and never moving.
        Assert.Equal(0.8f, tasks.Progress, 0.001f);
    }

    [Fact]
    public void A_pump_applies_at_most_its_budget() {
        using var tasks = new BackgroundTaskManager();
        var task = tasks.Begin("Importing");

        for (var i = 0; i < 10; i++) {
            task.Report("file " + i);
        }

        tasks.Pump(budget: 3);
        Assert.Equal("file 2", task.Status);

        tasks.Pump();
        Assert.Equal("file 9", task.Status);
    }

    // ── Progress reaches a subscriber ───────────────────────────────────────

    /// <summary>
    ///     The property the whole signal-backing exists for: a binding over a task re-runs when the
    ///     work reports, without anybody subscribing to an event or bumping a revision counter.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An <see cref="Effect" /> rather than a direct read, because a direct read passes
    ///     even if the properties are plain fields.</b> What is being pinned is that reading
    ///     <c>Progress</c> inside a reactive scope *subscribes* to it — which is what makes
    ///     `TaskCenter.vxml` a panel over the model rather than a view that is told to look.
    /// </remarks>
    [Fact]
    public void Progress_reaches_a_binding_that_read_it() {
        var scheduler = new EffectScheduler();

        using var tasks = new BackgroundTaskManager();
        var task = tasks.Begin("Importing");

        var seen = -1f;
        var runs = 0;

        using var effect = new Effect(
            () => {
                seen = task.Progress;
                runs++;
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(1, runs);
        Assert.Equal(0f, seen);

        task.Report(0.25f);
        tasks.Pump();
        scheduler.Flush();

        Assert.Equal(2, runs);
        Assert.Equal(0.25f, seen);
    }

    /// <summary>The list itself is reactive, so a panel rebuilds when a task starts or stops.</summary>
    [Fact]
    public void Starting_a_task_reaches_a_binding_that_counted_the_list() {
        var scheduler = new EffectScheduler();

        using var tasks = new BackgroundTaskManager();

        var seen = -1;

        using var effect = new Effect(() => seen = tasks.Tasks.Count, scheduler);
        scheduler.Flush();

        Assert.Equal(0, seen);

        tasks.Begin("Importing");
        scheduler.Flush();

        Assert.Equal(1, seen);
    }

    // ── Cancellation actually cancels ───────────────────────────────────────

    [Fact]
    public async Task Cancelling_asks_and_the_task_ends_when_the_work_notices() {
        using var tasks = new BackgroundTaskManager();
        var started = new TaskCompletionSource();

        var task = tasks.Start(
            "Baking",
            async running => {
                started.SetResult();

                while (!running.IsCancellationRequested) {
                    await Task.Delay(1, CancellationToken.None);
                }

                running.Cancellation.ThrowIfCancellationRequested();
            }
        );

        await started.Task;
        task.Cancel();

        // Asked, not done: a manager that took the task out of the list here would let the user
        // start the import again over the top of one that had not stopped.
        Assert.True(task.IsCancellationRequested);

        await Drain(tasks, task);
        Assert.Equal(BackgroundTaskState.Cancelled, task.State);
    }

    /// <summary>
    ///     The token the *work* watches fires too, which is the half `IsCancellationRequested` is a
    ///     mirror of.
    /// </summary>
    /// <remarks>
    ///     ⚠ Pinned separately because the two are different objects. The signal is written on the
    ///     UI thread for the interface to read; the token is what a `ThrowIfCancellationRequested`
    ///     in a tight loop on a pool thread asks. A `Cancel` that set only the signal would leave
    ///     every well-written piece of work running for ever.
    /// </remarks>
    [Fact]
    public async Task Cancelling_fires_the_token_the_work_is_watching() {
        using var tasks = new BackgroundTaskManager();
        var noticed = new TaskCompletionSource();

        var task = tasks.Begin("Baking");

        // A registration rather than a polling loop: it is the callback the token fires, so it
        // cannot pass by the signal happening to be set.
        using var registration = task.Cancellation.Register(() => noticed.TrySetResult());

        Assert.False(task.Cancellation.IsCancellationRequested);

        task.Cancel();

        await noticed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(task.Cancellation.IsCancellationRequested);
    }

    // ── A completed task leaves nothing behind ──────────────────────────────

    /// <summary>
    ///     After the pump that ends it, the manager holds no reference to a finished task and has
    ///     raised <c>Ended</c> exactly once.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>"Exactly once" is the assertion that matters.</b> `Stop` removes the task and
    ///     defers the event to `Pump`; a second pump finding the task still in the deferred list
    ///     would announce a finished import again, and the notification centre would show the same
    ///     toast twice.
    /// </remarks>
    [Fact]
    public void A_task_that_ended_is_out_of_the_list_and_announced_once() {
        using var tasks = new BackgroundTaskManager();

        var ended = 0;
        tasks.Ended += _ => ended++;

        var task = tasks.Begin("Importing");
        Assert.Single(tasks.Tasks);

        tasks.Complete(task);

        // Queued, not applied — the task is still listed until something pumps.
        Assert.Single(tasks.Tasks);
        Assert.Equal(0, ended);

        tasks.Pump();

        Assert.Empty(tasks.Tasks);
        Assert.False(tasks.IsBusy);
        Assert.Equal(BackgroundTaskState.Completed, task.State);
        Assert.Equal(1, ended);

        // The second pump is the one that catches a deferred list nobody cleared.
        tasks.Pump();
        tasks.Pump();

        Assert.Equal(1, ended);
        Assert.Empty(tasks.Tasks);
    }

    // ── A disposed owner does not strand a running task ─────────────────────

    /// <summary>
    ///     Disposing the manager cancels what is running rather than leaving it reporting into a
    ///     queue nothing will drain.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the plugin-unload leak in miniature.</b> The work's delegate belongs to
    ///     whoever started it; while a queued closure holds it, the assembly it came from cannot be
    ///     collected. An owner that dropped the manager without disposing would keep that closure
    ///     for the life of the process.
    /// </remarks>
    [Fact]
    public async Task Disposing_the_owner_cancels_what_is_still_running() {
        var tasks = new BackgroundTaskManager();
        var started = new TaskCompletionSource();
        var noticed = new TaskCompletionSource();

        var task = tasks.Start(
            "Baking",
            async running => {
                started.SetResult();

                while (!running.Cancellation.IsCancellationRequested) {
                    await Task.Delay(1, CancellationToken.None);
                }

                noticed.SetResult();
            }
        );

        await started.Task;

        tasks.Dispose();

        // The work is told, and it is told through the token rather than only through the mirror.
        await noticed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(task.IsCancellationRequested);
        Assert.Equal(BackgroundTaskState.Cancelled, task.State);
        Assert.False(task.IsRunning);
        Assert.Empty(tasks.Tasks);
        Assert.False(tasks.IsBusy);
    }

    /// <summary>
    ///     What the work reports after its owner has gone is dropped rather than accumulated.
    /// </summary>
    /// <remarks>
    ///     ⚠ The unbounded-growth half of the same leak: a task that ignores cancellation for
    ///     another minute must cost one thread and no memory. A `Post` that still enqueued would
    ///     grow a queue nothing drains, and every entry in it holds the task and the manager.
    /// </remarks>
    [Fact]
    public void Reports_after_disposal_are_dropped_rather_than_queued() {
        var tasks = new BackgroundTaskManager();
        var task = tasks.Begin("Importing");

        tasks.Dispose();

        for (var i = 0; i < 1000; i++) {
            task.Report(0.5f, "file " + i);
        }

        tasks.Pump();

        // Nothing was applied, because nothing was kept. The task ended as cancelled when the owner
        // went, and no report after that moved it.
        Assert.Equal(BackgroundTaskState.Cancelled, task.State);
        Assert.Null(task.Status);
        Assert.Equal(0f, task.Progress);
        Assert.Empty(tasks.Tasks);

        // Disposing twice is what a shell whose owner also disposes it does.
        tasks.Dispose();
    }

    static async Task Drain(BackgroundTaskManager tasks, BackgroundTask task) {
        for (var i = 0; i < 500 && task.IsRunning; i++) {
            tasks.Pump();

            if (task.IsRunning) {
                await Task.Delay(2, CancellationToken.None);
            }
        }

        tasks.Pump();
    }
}
