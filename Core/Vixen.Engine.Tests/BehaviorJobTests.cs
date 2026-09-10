// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     Item 3 of [04](../../../docs/plan/04-ecs-and-scripting.md) § Making it fast: a behaviour type
///     that opts into <see cref="BehaviorJobAttribute" /> gets its batch dispatched across the job
///     system instead of walked on the calling thread.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What is asserted is the dispatch and the result, and never a duration.</b> A batch
///         that ran on eight threads and a batch that ran on one produce the same world in the same
///         order — that is the whole design — so a test that only checked the result would pass just
///         as happily against a build where the feature was silently skipped, which is exactly what a
///         missing scheduler or a dropped attribute looks like.
///         <c>BehaviorStore.DispatchedBatches</c> is the instrument that separates the two, and it
///         reads zero on the day the feature does not run.
///     </para>
///     <para>
///         ⚠ <b>And no test here asserts that two indices overlapped in time.</b> That would be a
///         wall-clock property on a machine several other things are using, which is this
///         repository's largest flake source; what a batch is dispatched to is the job system's own
///         business and <c>Vixen.Core.Threading</c>'s tests are where it is held to it.
///     </para>
/// </remarks>
public sealed class BehaviorJobTests {
    /// <summary>A type that asked for its batch to be dispatched.</summary>
    /// <remarks>
    ///     Every field it writes is its own, which is the contract <c>IJobParallelFor</c> states and
    ///     <c>VXS0417</c> is what keeps a real one to.
    /// </remarks>
    [BehaviorJob]
    sealed class Dispatched : Behavior {
        public int Updates;
        public int LateUpdates;

        protected override void Update() => Updates++;

        protected override void LateUpdate() => LateUpdates++;
    }

    /// <summary>The identical body without the attribute, which is the differential.</summary>
    sealed class Serial : Behavior {
        public int Updates;

        protected override void Update() => Updates++;
    }

    static Dispatched[] Fill(EngineLoop loop, int count) {
        var behaviors = new Dispatched[count];

        for (var index = 0; index < count; index++) {
            behaviors[index] = loop.Behaviors.Add(loop.World.Create(), new Dispatched());
        }

        return behaviors;
    }

    /// <summary>Two frames: the first wakes and enables, the second is the first that updates.</summary>
    static void Run(EngineLoop loop, int frames = 2) {
        for (var frame = 0; frame < frames; frame++) {
            loop.Frame(TimeSpan.FromMilliseconds(16));
        }
    }

    [Fact]
    public void AMarkedBatchGoesToTheJobSystemAndEveryInstanceUpdatesExactlyOnce() {
        using var jobs = new JobScheduler(2);
        using var loop = new EngineLoop(jobs: jobs);
        var behaviors = Fill(loop, 64);

        Run(loop);

        Assert.All(behaviors, behavior => Assert.Equal(1, behavior.Updates));
        Assert.All(behaviors, behavior => Assert.Equal(1, behavior.LateUpdates));

        // Two frames, and two passes a frame: the batch is enabled from the first drain onwards, so
        // the first frame dispatches a pass that finds nothing started and does nothing. That is the
        // same shape the serial loop has — it walks the prefix and skips on IsStarted — and counting
        // it is what keeps this number a fact about dispatch rather than about the lifecycle.
        Assert.Equal(4, loop.Behaviors.DispatchedBatches);
    }

    /// <summary>
    ///     ⚠ The negative half of the instrument: the same store, the same scheduler, a type that did
    ///     not ask. A build where the attribute was read from the wrong place would pass the test
    ///     above and fail this one.
    /// </summary>
    /// <summary>
    ///     The same marked batch, dispatched across a scheduler with no workers at all and across one
    ///     with two, has to reach the same answer.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="JobScheduler" /> with nought workers is what the browser head builds by
    ///     construction</b>, and it is the configuration in which "dispatch and never wait" looks
    ///     exactly like "dispatch and complete" from every counter — the batch is handed over, the
    ///     frame ends, and the instances are simply never updated. Every other fixture in this file
    ///     runs two workers, so none of them can tell those apart (#328).
    ///     <para>
    ///         The oracle is per-instance work rather than a count of batches: <c>Updates</c> is
    ///         written by the behaviour itself, so <c>Assert.Equal(1, …)</c> on all sixty-four is
    ///         false both when the work is dropped and when it is done twice.
    ///     </para>
    /// </remarks>
    [Trait("Workers", "0")]
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AMarkedBatchReachesEveryInstanceWhateverTheWorkerCount(int workers) {
        using var jobs = new JobScheduler(workers);
        using var loop = new EngineLoop(jobs: jobs);
        var behaviors = Fill(loop, 64);

        Run(loop);

        Assert.All(behaviors, behavior => Assert.Equal(1, behavior.Updates));
        Assert.All(behaviors, behavior => Assert.Equal(1, behavior.LateUpdates));
        Assert.Equal(4, loop.Behaviors.DispatchedBatches);
    }

    [Fact]
    public void AnUnmarkedBatchIsWalkedOnTheCallingThreadEvenWithASchedulerPresent() {
        using var jobs = new JobScheduler(2);
        using var loop = new EngineLoop(jobs: jobs);
        var behaviors = new Serial[64];

        for (var index = 0; index < behaviors.Length; index++) {
            behaviors[index] = loop.Behaviors.Add(loop.World.Create(), new Serial());
        }

        Run(loop);

        Assert.All(behaviors, behavior => Assert.Equal(1, behavior.Updates));
        Assert.Equal(0, loop.Behaviors.DispatchedBatches);
    }

    /// <summary>
    ///     A loop with no scheduler — the editor's, a test's — runs a marked type serially and gets
    ///     the same answer. The attribute is permission, not a requirement.
    /// </summary>
    [Fact]
    public void AMarkedBatchWithNoSchedulerRunsHereAndGetsTheSameAnswer() {
        using var loop = new EngineLoop();
        var behaviors = Fill(loop, 8);

        Run(loop);

        Assert.All(behaviors, behavior => Assert.Equal(1, behavior.Updates));
        Assert.Equal(0, loop.Behaviors.DispatchedBatches);
    }

    /// <summary>One index cannot be split, so dispatching it would buy a round trip and nothing else.</summary>
    [Fact]
    public void ASingleEnabledInstanceIsNotDispatched() {
        using var jobs = new JobScheduler(2);
        using var loop = new EngineLoop(jobs: jobs);
        var behaviors = Fill(loop, 1);

        Run(loop);

        Assert.Equal(1, behaviors[0].Updates);
        Assert.Equal(0, loop.Behaviors.DispatchedBatches);
    }

    /// <summary>
    ///     ⚠ The partition is what the dispatch is given, not the array. A disabled behaviour lives
    ///     past the enabled prefix, and a job handed <c>items.Length</c> instead of the prefix would
    ///     run it — silently, because the only visible difference is a counter on an instance nobody
    ///     is looking at.
    /// </summary>
    [Fact]
    public void ADispatchedBatchStopsAtTheEnabledPrefix() {
        using var jobs = new JobScheduler(2);
        using var loop = new EngineLoop(jobs: jobs);
        var behaviors = Fill(loop, 16);

        Run(loop);

        foreach (var behavior in behaviors[8..]) {
            behavior.Enabled = false;
        }

        Run(loop, 1);

        Assert.All(behaviors[..8], behavior => Assert.Equal(2, behavior.Updates));
        Assert.All(behaviors[8..], behavior => Assert.Equal(1, behavior.Updates));
    }

    /// <summary>
    ///     A behaviour that has woken but not started is inside the enabled prefix and must not
    ///     update — the same test the serial loop makes, made per index rather than once.
    /// </summary>
    [Fact]
    public void ADispatchedBatchDoesNotUpdateSomethingThatHasNotStarted() {
        using var jobs = new JobScheduler(2);
        using var loop = new EngineLoop(jobs: jobs);
        var behaviors = Fill(loop, 16);

        Run(loop, 1);

        Assert.All(behaviors, behavior => Assert.Equal(0, behavior.Updates));

        // Dispatched and empty-handed, which is the point: the IsStarted test is inside the job.
        Assert.Equal(2, loop.Behaviors.DispatchedBatches);
    }
}
