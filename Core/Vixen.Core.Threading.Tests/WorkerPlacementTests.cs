// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using Xunit;

namespace Vixen.Core.Threading.Tests;

/// <summary>
///     That a supplied <see cref="IWorkerPlacement" /> is asked once per worker, <em>on</em> that
///     worker, and that a scheduler which placed nothing says so.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assertion that carries this file is the thread name, not the call count.</b>
///         Every affinity primitive underneath pins whoever calls it, so a placement applied from the
///         constructing thread would pin the constructing thread once per worker and leave the pool
///         exactly where it was — and a test that only counted calls would be green against precisely
///         that defect, as would every throughput measurement anyone took afterwards. The fake below
///         therefore records <c>Thread.CurrentThread.Name</c>, which the scheduler sets to
///         <c>Vixen Job Worker {ordinal}</c> and nothing else in the process is called.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is bounded by a clock.</b> The one timeout is on a
///         <see cref="CountdownEvent" /> waiting for threads that are already started, so it is a
///         hang check and not a budget: if placement never happens the wait cannot succeed however
///         long it is given, and if it does happen the machine's load does not change the answer.
///     </para>
/// </remarks>
public class WorkerPlacementTests {
    const string WorkerNamePrefix = "Vixen Job Worker ";

    // A hang check. Workers are started by the constructor that returns before this is waited on, so
    // the event either counts down or the feature is not there at all; this bounds the second case.
    const int HangCheckMilliseconds = 30_000;

    [Fact]
    public void EachWorkerPlacesItselfOnceOnTheThreadItNames() {
        var placement = new RecordingPlacement(answer: true, workerCount: 4);

        using (var scheduler = new JobScheduler(4, placement)) {
            Assert.True(
                placement.Placed.Wait(HangCheckMilliseconds, TestContext.Current.CancellationToken),
                $"Only {placement.Calls.Count} of 4 workers reached the placement."
            );

            // Once each, and every ordinal exactly once — a policy that spreads workers across cores
            // has nothing to spread if two of them arrive as the same number.
            Assert.Equal([0, 1, 2, 3], placement.Calls.Select(call => call.Ordinal).Order());

            // ⚠ The half that makes the line above mean anything. The ordinal is only a claim about
            // which worker this is; the name is the worker itself saying so.
            foreach (var call in placement.Calls) {
                Assert.Equal($"Vixen Job Worker {call.Ordinal}", call.ThreadName);
            }

            // Every worker was told how many there are, or a policy cannot spread them.
            Assert.All(placement.Calls, call => Assert.Equal(4, call.WorkerCount));

            Assert.Equal(4, scheduler.WorkersPlaced);
        }

        // Released on the way out, and on the worker rather than on whoever disposed the scheduler —
        // the same reason placing has to happen there.
        Assert.Equal([0, 1, 2, 3], placement.Released.Select(call => call.Ordinal).Order());

        foreach (var call in placement.Released) {
            Assert.Equal($"Vixen Job Worker {call.Ordinal}", call.ThreadName);
        }
    }

    [Fact]
    public void APlacementThatPlacesNothingIsNotAPlacementThatWasNeverAsked() {
        // The instrument. macOS answers false to every TrySetAffinity, and so does a browser and a
        // container whose mask is not ours to set — so "supplied a placement" and "pinned a worker"
        // are different facts and something has to be able to tell them apart. Without this counter
        // nothing in the process can: the same jobs finish either way.
        var refusing = new RecordingPlacement(answer: false, workerCount: 2);

        using (var scheduler = new JobScheduler(2, refusing)) {
            Assert.True(refusing.Placed.Wait(HangCheckMilliseconds, TestContext.Current.CancellationToken), "The placement was never asked.");

            // Asked twice, placed nothing.
            Assert.Equal(2, refusing.Calls.Count);
            Assert.Equal(0, scheduler.WorkersPlaced);
        }

        // And nothing is released that was never placed, so an implementation never has to ask
        // whether it has anything to undo.
        Assert.Empty(refusing.Released);

        using var unplaced = new JobScheduler(2);
        Assert.Equal(0, unplaced.WorkersPlaced);
    }

    [Fact]
    public void APlacementThatThrowsCostsThePinningAndNotTheFrame() {
        var placement = new ThrowingPlacement(2);

        using var scheduler = new JobScheduler(2, placement);
        Assert.True(placement.Reached.Wait(HangCheckMilliseconds, TestContext.Current.CancellationToken), "The placement was never asked.");

        // The pool still works. An unhandled exception on a worker thread takes the whole process
        // down, and pinning is an optimisation — losing a frame to a machine whose affinity mask was
        // not ours to set is the worse of the two answers by a wide margin.
        var counter = new StrongCounter();
        scheduler.ParallelFor(new CountJob(counter), 64, 1);

        Assert.Equal(64, counter.Value);
        Assert.Equal(0, scheduler.WorkersPlaced);
    }

    [Fact]
    public void ASchedulerWithNoWorkersAsksNothing() {
        // The browser's shape. There is no thread to place, so a placement that was called anyway
        // would be pinning the caller — which is the defect this whole file is about, arriving by a
        // different door.
        var placement = new RecordingPlacement(answer: true, workerCount: 1);

        using var scheduler = new JobScheduler(0, placement);

        var counter = new StrongCounter();
        scheduler.ParallelFor(new CountJob(counter), 8, 1);

        Assert.Equal(8, counter.Value);
        Assert.Empty(placement.Calls);
        Assert.Equal(0, scheduler.WorkersPlaced);
    }

    sealed record PlacementCall(int Ordinal, int WorkerCount, string? ThreadName);

    sealed class RecordingPlacement(bool answer, int workerCount) : IWorkerPlacement {
        public ConcurrentBag<PlacementCall> Calls { get; } = [];
        public ConcurrentBag<PlacementCall> Released { get; } = [];
        public CountdownEvent Placed { get; } = new(workerCount);

        public bool TryPlace(int ordinal, int count) {
            Calls.Add(new(ordinal, count, Thread.CurrentThread.Name));
            Placed.Signal();
            return answer;
        }

        public void Release() {
            // Read back out of the thread name rather than remembered, because what is under test is
            // which thread this runs on. ⚠ Deliberately not a throw on a name that does not parse:
            // the scheduler swallows what this method throws, so a failure raised here would be
            // eaten and the suite would go green on the defect. A -1 reaches the assertion instead.
            var name = Thread.CurrentThread.Name;

            var ordinal = name is not null
                && name.StartsWith(WorkerNamePrefix, StringComparison.Ordinal)
                && int.TryParse(
                    name.AsSpan(WorkerNamePrefix.Length),
                    CultureInfo.InvariantCulture,
                    out var parsed
                )
                    ? parsed
                    : -1;

            Released.Add(new(ordinal, workerCount, name));
        }
    }

    sealed class ThrowingPlacement(int workerCount) : IWorkerPlacement {
        public CountdownEvent Reached { get; } = new(workerCount);

        public bool TryPlace(int ordinal, int count) {
            Reached.Signal();
            throw new InvalidOperationException("This platform's affinity call failed.");
        }

        public void Release() => throw new InvalidOperationException("Nothing was placed.");
    }

    sealed class StrongCounter {
        int value;
        public int Value => Volatile.Read(ref value);
        public void Increment() => Interlocked.Increment(ref value);
    }

    readonly struct CountJob(StrongCounter counter) : IJobParallelFor {
        public void Execute(int index) => counter.Increment();
    }
}
