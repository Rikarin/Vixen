// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     Subsystem teardown. The two behaviours that make a bag worth having over a hand-written
///     sequence of <c>Dispose</c> calls are reverse ordering and not stopping at the first failure;
///     both are here.
/// </summary>
public class DisposeBagTests {
    sealed class Recorder(List<string> log, string name) : IDisposable {
        public void Dispose() => log.Add(name);
    }

    sealed class AsyncRecorder(List<string> log, string name) : IAsyncDisposable {
        public ValueTask DisposeAsync() {
            log.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    sealed class Thrower(string message) : IDisposable {
        public void Dispose() => throw new InvalidOperationException(message);
    }

    [Fact]
    public void Resources_are_disposed_in_reverse_order_of_registration() {
        // Construction order is dependency order, so teardown has to run backwards.
        var log = new List<string>();
        var bag = new DisposeBag();

        bag.Add(new Recorder(log, "device"));
        bag.Add(new Recorder(log, "swapchain"));
        bag.Add(new Recorder(log, "pipeline"));

        bag.Dispose();

        Assert.Equal(new[] { "pipeline", "swapchain", "device" }, log);
    }

    [Fact]
    public void Add_hands_the_resource_straight_back() {
        var bag = new DisposeBag();
        var recorder = new Recorder([], "x");

        Assert.Same(recorder, bag.Add(recorder));
        Assert.Equal(1, bag.Count);
    }

    [Fact]
    public void One_failure_does_not_strand_the_rest() {
        var log = new List<string>();
        var bag = new DisposeBag();

        bag.Add(new Recorder(log, "first"));
        bag.Add(new Thrower("boom"));
        bag.Add(new Recorder(log, "last"));

        var failure = Assert.Throws<AggregateException>(bag.Dispose);

        Assert.Single(failure.InnerExceptions);
        Assert.Equal("boom", failure.InnerExceptions[0].Message);
        Assert.Equal(new[] { "last", "first" }, log);
    }

    [Fact]
    public void Every_failure_is_reported_not_just_the_first() {
        var bag = new DisposeBag();
        bag.Add(new Thrower("one"));
        bag.Add(new Thrower("two"));

        var failure = Assert.Throws<AggregateException>(bag.Dispose);

        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Fact]
    public void Disposing_twice_does_not_dispose_anything_twice() {
        var log = new List<string>();
        var bag = new DisposeBag();
        bag.Add(new Recorder(log, "once"));

        bag.Dispose();
        bag.Dispose();

        Assert.Equal(new[] { "once" }, log);
        Assert.True(bag.IsDisposed);
        Assert.Equal(0, bag.Count);
    }

    [Fact]
    public void Adding_to_a_disposed_bag_disposes_immediately_rather_than_leaking() {
        // Teardown races are real; the alternative to this is a leak plus an exception on a path
        // that is already going wrong.
        var log = new List<string>();
        var bag = new DisposeBag();
        bag.Dispose();

        bag.Add(new Recorder(log, "late"));

        Assert.Equal(new[] { "late" }, log);
        Assert.Equal(0, bag.Count);
    }

    [Fact]
    public void A_callback_can_stand_in_for_a_disposable() {
        var ran = false;
        var bag = new DisposeBag();
        bag.Add(() => ran = true);

        bag.Dispose();

        Assert.True(ran);
    }

    [Fact]
    public async Task DisposeAsync_awaits_async_resources_and_still_runs_backwards() {
        var log = new List<string>();
        var bag = new DisposeBag();

        bag.Add(new Recorder(log, "sync"));
        bag.AddAsync(new AsyncRecorder(log, "async"));

        await bag.DisposeAsync();

        Assert.Equal(new[] { "async", "sync" }, log);
    }

    [Fact]
    public void The_synchronous_path_still_disposes_async_only_resources() {
        var log = new List<string>();
        var bag = new DisposeBag();
        bag.AddAsync(new AsyncRecorder(log, "async"));

        bag.Dispose();

        Assert.Equal(new[] { "async" }, log);
    }

    [Fact]
    public void A_null_resource_is_rejected_at_the_door() {
        var bag = new DisposeBag();
        Assert.Throws<ArgumentNullException>(() => bag.Add((IDisposable)null!));
        Assert.Throws<ArgumentNullException>(() => bag.Add((Action)null!));
    }
}
