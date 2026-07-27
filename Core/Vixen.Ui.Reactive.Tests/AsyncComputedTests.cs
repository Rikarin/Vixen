// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     Asynchronous derivation. Every test here drives the completion by hand rather than waiting on
///     a timer: a test that sleeps is a test that is flaky on a loaded CI runner, and the whole point
///     of the design is that results arrive at a moment the frame chooses.
/// </summary>
public class AsyncComputedTests {
    [Fact]
    public void It_starts_loading_and_publishes_when_the_work_completes() {
        var scheduler = new EffectScheduler();
        var query = new Signal<string>("cube");
        var pending = new TaskCompletionSource<int>();

        using var results = new AsyncComputed<string, int>(() => query.Value, (_, _) => pending.Task, scheduler);
        scheduler.Flush();

        Assert.Equal(AsyncStatus.Loading, results.Peek().Status);

        pending.SetResult(7);
        scheduler.Flush();

        Assert.Equal(AsyncStatus.Success, results.Peek().Status);
        Assert.Equal(7, results.Peek().Value);
    }

    [Fact]
    public void A_result_is_not_visible_until_a_flush_applies_it() {
        // The guarantee that makes the rest of the assembly lock-free: nothing a worker thread
        // produced is in the graph until the owning thread puts it there.
        var scheduler = new EffectScheduler();
        var pending = new TaskCompletionSource<int>();

        using var results = new AsyncComputed<int, int>(static () => 0, (_, _) => pending.Task, scheduler);
        scheduler.Flush();
        pending.SetResult(7);

        Assert.Equal(AsyncStatus.Loading, results.Peek().Status);

        scheduler.Flush();

        Assert.Equal(AsyncStatus.Success, results.Peek().Status);
    }

    [Fact]
    public void A_new_request_supersedes_the_one_in_flight_even_if_it_finishes_second() {
        var scheduler = new EffectScheduler();
        var query = new Signal<string>("first");
        var first = new TaskCompletionSource<string>();
        var second = new TaskCompletionSource<string>();

        using var results = new AsyncComputed<string, string>(
            () => query.Value,
            (request, _) => request == "first" ? first.Task : second.Task,
            scheduler
        );

        scheduler.Flush();

        query.Value = "second";
        scheduler.Flush();

        second.SetResult("from second");
        scheduler.Flush();

        Assert.Equal("from second", results.Peek().Value);

        // The overtaken request answers late. It must not be believed.
        first.SetResult("from first");
        scheduler.Flush();

        Assert.Equal("from second", results.Peek().Value);
    }

    [Fact]
    public void The_previous_value_survives_a_reload_so_a_panel_does_not_blank() {
        var scheduler = new EffectScheduler();
        var query = new Signal<int>(1);
        var pending = new TaskCompletionSource<string>();

        using var results = new AsyncComputed<int, string>(() => query.Value, (_, _) => pending.Task, scheduler);
        scheduler.Flush();
        pending.SetResult("first");
        scheduler.Flush();

        Assert.Equal("first", results.Peek().Value);

        var reload = new TaskCompletionSource<string>();
        pending = reload;
        query.Value = 2;
        scheduler.Flush();

        var during = results.Peek();

        Assert.Equal(AsyncStatus.Loading, during.Status);
        Assert.Equal("first", during.Value);
        Assert.True(during.HasValue);
    }

    [Fact]
    public void A_failure_is_a_state_rather_than_an_exception_nobody_can_catch() {
        var scheduler = new EffectScheduler();
        var pending = new TaskCompletionSource<int>();

        using var results = new AsyncComputed<int, int>(static () => 0, (_, _) => pending.Task, scheduler);
        scheduler.Flush();

        pending.SetException(new InvalidOperationException("network"));
        scheduler.Flush();

        Assert.Equal(AsyncStatus.Failure, results.Peek().Status);
        Assert.Equal("network", results.Peek().Error?.Message);
    }

    [Fact]
    public void A_request_that_throws_before_it_starts_lands_in_the_same_failure_state() {
        var scheduler = new EffectScheduler();

        using var results = new AsyncComputed<int, int>(
            static () => 0,
            static (_, _) => throw new InvalidOperationException("bad request"),
            scheduler
        );

        scheduler.Flush();
        scheduler.Flush();

        Assert.Equal(AsyncStatus.Failure, results.Peek().Status);
    }

    [Fact]
    public void The_in_flight_request_is_cancelled_when_a_new_one_starts() {
        var scheduler = new EffectScheduler();
        var query = new Signal<int>(1);
        var cancelled = false;

        using var results = new AsyncComputed<int, int>(
            () => query.Value,
            (_, token) => {
                token.Register(() => cancelled = true);
                return new TaskCompletionSource<int>().Task;
            },
            scheduler
        );

        scheduler.Flush();
        query.Value = 2;
        scheduler.Flush();

        Assert.True(cancelled);
    }

    [Fact]
    public void Whatever_reads_it_is_woken_when_the_result_lands() {
        var scheduler = new EffectScheduler();
        var pending = new TaskCompletionSource<int>();

        using var results = new AsyncComputed<int, int>(static () => 0, (_, _) => pending.Task, scheduler);
        var seen = new List<AsyncStatus>();

        using var effect = new Effect(() => seen.Add(results.Value.Status), scheduler);
        scheduler.Flush();

        pending.SetResult(1);
        scheduler.Flush();

        Assert.Equal(new[] { AsyncStatus.Loading, AsyncStatus.Success }, seen);
    }

    [Fact]
    public void Disposing_it_stops_a_late_result_reaching_the_graph() {
        var scheduler = new EffectScheduler();
        var pending = new TaskCompletionSource<int>();
        var results = new AsyncComputed<int, int>(static () => 0, (_, _) => pending.Task, scheduler);

        scheduler.Flush();
        results.Dispose();
        pending.SetResult(7);
        scheduler.Flush();

        Assert.Equal(AsyncStatus.Loading, results.Peek().Status);
    }
}
