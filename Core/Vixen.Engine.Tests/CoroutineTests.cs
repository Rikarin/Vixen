// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Coroutines;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class CoroutineTests {
    static readonly TimeSpan Sixtieth = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    /// <summary>
    ///     The property everything else rests on: awaiting anything at all costs a frame, even a
    ///     wait of zero. Without it <c>while (true) await Seconds(0f);</c> — which users write — is a
    ///     hang rather than a loop.
    /// </summary>
    [Fact]
    public void NothingResumesInTheFrameItSuspendedIn() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var steps = 0;

        clock.Begin(scheduler);
        scheduler.Run(Body());

        Assert.Equal(1, steps);

        // Draining the very frame it suspended in must not move it on.
        scheduler.Drain(ResumePoint.Update);
        Assert.Equal(1, steps);

        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.Equal(2, steps);

        return;

        async Coroutine Body() {
            steps++;
            await scheduler.Seconds(0f);
            steps++;
        }
    }

    [Fact]
    public void NextFrameResumesOncePerFrame() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var resumes = 0;

        clock.Begin(scheduler);
        scheduler.Run(Body());

        for (var frame = 0; frame < 5; frame++) {
            clock.Begin(scheduler);
            scheduler.Drain(ResumePoint.Update);
        }

        Assert.Equal(5, resumes);
        return;

        async Coroutine Body() {
            while (true) {
                await scheduler.NextFrame();
                resumes++;
            }
        }
    }

    /// <summary>
    ///     Scaled time is the clock gameplay runs on, so a paused game must not advance a wait. The
    ///     unscaled one must, which is what a UI animation or a pause menu is written against.
    /// </summary>
    [Fact]
    public void APauseStopsScaledWaitsAndNotUnscaledOnes() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var scaled = false;
        var unscaled = false;

        clock.Begin(scheduler);
        scheduler.Run(Scaled());
        scheduler.Run(Unscaled());

        // A second of wall clock, entirely paused.
        for (var frame = 0; frame < 60; frame++) {
            clock.Begin(scheduler, Sixtieth, timeScale: 0f);
            scheduler.Drain(ResumePoint.Update);
        }

        Assert.False(scaled);
        Assert.True(unscaled);

        for (var frame = 0; frame < 60; frame++) {
            clock.Begin(scheduler, Sixtieth);
            scheduler.Drain(ResumePoint.Update);
        }

        Assert.True(scaled);
        return;

        async Coroutine Scaled() {
            await scheduler.Seconds(0.5f);
            scaled = true;
        }

        async Coroutine Unscaled() {
            await scheduler.UnscaledSeconds(0.5f);
            unscaled = true;
        }
    }

    [Fact]
    public void UntilAndWhileTestOncePerOccurrenceOfTheResumePoint() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var open = false;
        var tests = 0;
        var through = 0;

        clock.Begin(scheduler);
        scheduler.Run(Waits());

        for (var frame = 0; frame < 3; frame++) {
            clock.Begin(scheduler);
            scheduler.Drain(ResumePoint.Update);
        }

        Assert.Equal(0, through);
        Assert.Equal(3, tests);

        open = true;
        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.Equal(1, through);

        // Now the While: it holds while `open` stays true, and lets go when it goes false.
        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.Equal(1, through);

        open = false;
        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.Equal(2, through);
        return;

        async Coroutine Waits() {
            await scheduler.Until(() => {
                    tests++;
                    return open;
                }
            );

            through++;
            await scheduler.While(() => open);
            through++;
        }
    }

    /// <summary>
    ///     Resumption order is the order the waits were made, and it has to be: the determinism
    ///     criterion this phase measures says two runs of the same input produce the same state, and
    ///     coroutines that resumed in an order the scheduler chose for its own convenience would
    ///     break that the first time two of them touched the same component.
    /// </summary>
    [Fact]
    public void ResumptionOrderIsTheOrderTheWaitsWereMade() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var log = new List<int>();

        clock.Begin(scheduler);

        for (var index = 0; index < 8; index++) {
            scheduler.Run(Body(index));
        }

        // Every other one waits two frames, so the frame that resumes the rest has to remove them
        // from the middle of the list without disturbing what is left.
        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.Equal([0, 2, 4, 6], log);

        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.Equal([0, 2, 4, 6, 1, 3, 5, 7], log);
        return;

        async Coroutine Body(int index) {
            await scheduler.NextFrame();

            if (index % 2 == 1) {
                await scheduler.NextFrame();
            }

            log.Add(index);
        }
    }

    [Fact]
    public void AResumePointDecidesWhichPartOfTheFrameComesBack() {
        using var loop = new EngineLoop();
        var entity = loop.World.Create();
        var behavior = loop.Behaviors.Add(entity, new Sequencer());

        // One frame to Awake, one to Start — which is where it starts — and then one frame per wait,
        // because no wait ever resumes in the frame it was made in.
        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);
        Assert.Empty(behavior.Log);

        loop.Frame(Sixtieth);
        Assert.Equal(["update"], behavior.Log);

        loop.Frame(Sixtieth);
        Assert.Equal(["update", "late"], behavior.Log);

        loop.Frame(Sixtieth);
        Assert.Equal(["update", "late", "end"], behavior.Log);
    }

    /// <summary>
    ///     A fixed-step wait ticks with the steps, not the frames — so a frame that owes three steps
    ///     resumes a step-waiting coroutine three times, and one that owes none leaves it alone.
    /// </summary>
    [Fact]
    public void AFixedStepWaitTicksWithTheStepsAndNotTheFrames() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var resumes = 0;

        clock.Begin(scheduler);
        scheduler.Run(Body());

        // One frame, three steps.
        clock.Begin(scheduler);

        for (var step = 0; step < 3; step++) {
            scheduler.BeginStep();
            scheduler.Drain(ResumePoint.FixedStep);
        }

        Assert.Equal(3, resumes);

        // One frame, no steps.
        clock.Begin(scheduler);
        Assert.Equal(3, resumes);
        return;

        async Coroutine Body() {
            while (true) {
                await scheduler.NextFrame(ResumePoint.FixedStep);
                resumes++;
            }
        }
    }

    /// <summary>
    ///     A coroutine started from <c>Start</c> — which runs in the lifecycle drain in
    ///     <c>EarlyUpdate</c>, before any coroutine drain — must still see its first
    ///     <c>await NextFrame()</c> land on the next frame rather than a millisecond later in this
    ///     one. That is what the loop's separate <c>BeginFrame</c> is for.
    /// </summary>
    [Fact]
    public void AWaitMadeBeforeAnyDrainStillCostsAWholeFrame() {
        using var loop = new EngineLoop();
        var entity = loop.World.Create();
        var behavior = loop.Behaviors.Add(entity, new StartsACoroutine());

        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);

        // Frame two ran Start, which started it; nothing may have resumed yet.
        Assert.Equal(0, behavior.Resumes);

        loop.Frame(Sixtieth);
        Assert.Equal(1, behavior.Resumes);
    }

    /// <summary>
    ///     Destroying a behaviour cancels its coroutines by throwing into them rather than by
    ///     abandoning them, so a <c>finally</c> gets to run. Abandoning is the cheaper implementation
    ///     and it silently skips every piece of cleanup anybody wrote.
    /// </summary>
    [Fact]
    public void DestroyingABehaviourCancelsItsCoroutinesAndRunsTheirFinallyBlocks() {
        using var loop = new EngineLoop();
        var entity = loop.World.Create();
        var behavior = loop.Behaviors.Add(entity, new HoldsAResource());

        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);
        Assert.True(behavior.Held);

        behavior.Destroy();
        loop.Frame(Sixtieth);

        Assert.False(behavior.Held);
        Assert.Equal(0, loop.Coroutines.RunningCount);
    }

    /// <summary>
    ///     A ten second wait must not keep a destroyed behaviour's coroutine alive for ten seconds.
    ///     Cancellation is checked before whatever the coroutine was actually waiting for.
    /// </summary>
    [Fact]
    public void ALongWaitDoesNotDelayCancellation() {
        using var loop = new EngineLoop();
        var entity = loop.World.Create();
        var behavior = loop.Behaviors.Add(entity, new WaitsTenSeconds());

        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);
        Assert.Equal(1, loop.Coroutines.RunningCount);

        behavior.Destroy();
        loop.Frame(Sixtieth);

        Assert.True(behavior.Cancelled);
        Assert.Equal(0, loop.Coroutines.RunningCount);
    }

    /// <summary>
    ///     <c>StopCoroutines</c> has to reach a coroutine several <c>await</c>s down, which is why
    ///     cancellation is a generation on the owner rather than a flag on a handle: the nested
    ///     coroutine's continuation is held by its caller's state machine, where no handle can see
    ///     it.
    /// </summary>
    [Fact]
    public void StoppingCoroutinesReachesOneNestedInsideAnother() {
        using var loop = new EngineLoop();
        var entity = loop.World.Create();
        var behavior = loop.Behaviors.Add(entity, new NestsACoroutine());

        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);
        Assert.True(behavior.Inner > 0);
        Assert.False(behavior.OuterFinished);

        behavior.StopCoroutines();
        loop.Frame(Sixtieth);

        Assert.True(behavior.InnerCleanedUp);
        Assert.True(behavior.OuterCleanedUp);
        Assert.False(behavior.OuterFinished);
        Assert.Equal(0, loop.Coroutines.RunningCount);
    }

    /// <summary>A coroutine started after the stop is not cancelled by it — hence a counter, not a flag.</summary>
    [Fact]
    public void StoppingCoroutinesDoesNotStopOnesStartedAfterwards() {
        using var loop = new EngineLoop();
        var entity = loop.World.Create();
        var behavior = loop.Behaviors.Add(entity, new StartsACoroutine());

        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);

        behavior.StopCoroutines();
        behavior.Restart();

        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);

        Assert.True(behavior.Resumes > 0);
    }

    /// <summary>
    ///     An exception in a coroutine is not allowed to vanish. Fire-and-forget is what makes
    ///     coroutines pleasant to use and it is also what makes <c>async void</c> notorious; the
    ///     default here is the loud one.
    /// </summary>
    [Fact]
    public void AnUnhandledExceptionSurfacesFromTheDrainThatObservedIt() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();

        clock.Begin(scheduler);
        scheduler.Run(Body());

        clock.Begin(scheduler);
        var failure = Assert.Throws<InvalidOperationException>(() => scheduler.Drain(ResumePoint.Update));

        Assert.Equal("from a coroutine", failure.Message);
        Assert.Equal(0, scheduler.RunningCount);
        return;

        async Coroutine Body() {
            await scheduler.NextFrame();
            throw new InvalidOperationException("from a coroutine");
        }
    }

    /// <summary>
    ///     And the drain still finishes what it started. A failure that stranded the continuations
    ///     already taken off the waiting list would lose them for ever.
    /// </summary>
    [Fact]
    public void AFailureDoesNotStrandTheOtherCoroutinesInTheSameDrain() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var survivors = 0;

        clock.Begin(scheduler);
        scheduler.Run(Fails());
        scheduler.Run(Survives());
        scheduler.Run(Survives());

        clock.Begin(scheduler);
        Assert.Throws<InvalidOperationException>(() => scheduler.Drain(ResumePoint.Update));
        Assert.Equal(2, survivors);
        return;

        async Coroutine Fails() {
            await scheduler.NextFrame();
            throw new InvalidOperationException("first");
        }

        async Coroutine Survives() {
            await scheduler.NextFrame();
            survivors++;
        }
    }

    [Fact]
    public void AHandlerTakesTheFailureInsteadOfTheDrainThrowing() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var caught = new List<Exception>();
        scheduler.UnhandledException = caught.Add;

        clock.Begin(scheduler);
        scheduler.Run(Body());

        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);

        Assert.Single(caught);
        Assert.Equal("handled", caught[0].Message);
        return;

        async Coroutine Body() {
            await scheduler.NextFrame();
            throw new InvalidOperationException("handled");
        }
    }

    [Fact]
    public void AHandleIsRunningUntilItsCoroutineIsNot() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();

        Assert.False(default(CoroutineHandle).IsRunning);

        clock.Begin(scheduler);
        var handle = scheduler.Run(Body());
        Assert.True(handle.IsRunning);

        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.False(handle.IsRunning);

        // And the slot it was in has been reused rather than leaked.
        clock.Begin(scheduler);
        scheduler.Run(Body());
        Assert.False(handle.IsRunning);
        Assert.Equal(1, scheduler.RunningCount);
        return;

        async Coroutine Body() => await scheduler.NextFrame();
    }

    /// <summary>A coroutine that finishes before it ever suspends is finished by the time <c>Run</c> returns.</summary>
    [Fact]
    public void ACoroutineThatNeverSuspendsIsAlreadyDone() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var ran = false;

        clock.Begin(scheduler);
        var handle = scheduler.Run(Body());

        Assert.True(ran);
        Assert.False(handle.IsRunning);
        Assert.Equal(0, scheduler.RunningCount);
        return;

#pragma warning disable CS1998
        async Coroutine Body() => ran = true;
#pragma warning restore CS1998
    }

    [Fact]
    public void WhenAllFinishesWhenTheLastOfThemDoes() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var finished = false;

        clock.Begin(scheduler);
        scheduler.Run(Both());

        for (var frame = 0; frame < 3; frame++) {
            clock.Begin(scheduler);
            scheduler.Drain(ResumePoint.Update);
            Assert.False(finished);
        }

        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);
        Assert.True(finished);
        return;

        async Coroutine Both() {
            // The slow one first, so that finishing "when the last one does" cannot be got right by
            // accident by returning after the argument that happens to be listed last.
            await Coroutine.WhenAll(Frames(4), Frames(1));
            finished = true;
        }

        async Coroutine Frames(int count) {
            for (var frame = 0; frame < count; frame++) {
                await scheduler.NextFrame();
            }
        }
    }

    /// <summary>
    ///     Reading the clock and the frame counter from another thread would be a race, so a wait
    ///     made from one is refused rather than quietly answered with whatever was there.
    /// </summary>
    [Fact]
    public void AWaitMadeOffTheLoopThreadIsRefused() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        clock.Begin(scheduler);

        Exception? caught = null;

        // A thread of its own rather than the thread pool: waiting on a pool task can run it inline
        // on the waiting thread, which here is the loop thread, and the test would pass by not
        // testing anything.
        var thread = new Thread(() => {
                try {
                    scheduler.NextFrame().GetAwaiter().UnsafeOnCompleted(() => { });
                } catch (Exception failure) {
                    caught = failure;
                }
            }
        );

        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("not the loop thread", caught.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The way back from real asynchrony: a coroutine that awaited a file read resumes on a
    ///     thread pool thread, where it must not touch the world, and <c>ResumeOnLoop</c> is what
    ///     puts it back where it may.
    /// </summary>
    [Fact]
    public void ResumeOnLoopBringsACoroutineBackFromTheThreadPool() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedOn = 0;

        clock.Begin(scheduler);
        scheduler.Run(Body());
        gate.SetResult();

        var waited = Stopwatch.StartNew();

        // ⚠ Thirty seconds, because what is being waited for is a thread-pool continuation and the
        // pool is shared with everything else the runner is doing. Ten was enough on a developer's
        // machine and not on the Windows leg, which spent all of it and reported `resumedOn` as zero
        // — a coroutine that never came back, from a scheduler that was working perfectly.
        while (resumedOn == 0 && waited.Elapsed < TimeSpan.FromSeconds(30)) {
            clock.Begin(scheduler);
            scheduler.Drain(ResumePoint.Update);
            Thread.Sleep(1);
        }

        Assert.Equal(Environment.CurrentManagedThreadId, resumedOn);
        return;

        async Coroutine Body() {
            await gate.Task.ConfigureAwait(false);
            await scheduler.ResumeOnLoop();
            resumedOn = Environment.CurrentManagedThreadId;
        }
    }

    [Fact]
    public void ADrainCannotBeReEntered() {
        var clock = new Clock();
        var scheduler = new CoroutineScheduler();
        Exception? caught = null;

        clock.Begin(scheduler);
        scheduler.Run(Body());

        clock.Begin(scheduler);
        scheduler.Drain(ResumePoint.Update);

        Assert.IsType<InvalidOperationException>(caught);
        return;

        async Coroutine Body() {
            await scheduler.NextFrame();

            try {
                scheduler.Drain(ResumePoint.Update);
            } catch (Exception failure) {
                caught = failure;
            }
        }
    }

    /// <summary>Winds a clock forward the way <c>EngineLoop</c> does, so scheduler-level tests read the same.</summary>
    sealed class Clock {
        GameTime time = GameTime.Zero;

        internal void Begin(CoroutineScheduler scheduler, TimeSpan? elapsed = null, float timeScale = 1f) {
            time = time.Advance(elapsed ?? Sixtieth, timeScale);
            scheduler.BeginFrame(time);
        }
    }

    sealed class Sequencer : Behavior {
        internal List<string> Log { get; } = [];

        protected override void Start() => Run(Body());

        async Coroutine Body() {
            await NextFrame(ResumePoint.Update);
            Log.Add("update");
            await NextFrame(ResumePoint.LateUpdate);
            Log.Add("late");
            await NextFrame(ResumePoint.EndOfFrame);
            Log.Add("end");
        }
    }

    sealed class StartsACoroutine : Behavior {
        internal int Resumes { get; private set; }

        internal void Restart() => Run(Body());

        protected override void Start() => Run(Body());

        async Coroutine Body() {
            while (true) {
                await NextFrame();
                Resumes++;
            }
        }
    }

    sealed class HoldsAResource : Behavior {
        internal bool Held { get; private set; }

        protected override void Start() => Run(Body());

        async Coroutine Body() {
            Held = true;

            try {
                while (true) {
                    await NextFrame();
                }
            } finally {
                Held = false;
            }
        }
    }

    sealed class WaitsTenSeconds : Behavior {
        internal bool Cancelled { get; private set; }

        protected override void Start() => Run(Body());

        async Coroutine Body() {
            try {
                await Seconds(10f);
            } catch (OperationCanceledException) {
                Cancelled = true;
            }
        }
    }

    sealed class NestsACoroutine : Behavior {
        internal int Inner { get; private set; }

        internal bool InnerCleanedUp { get; private set; }

        internal bool OuterCleanedUp { get; private set; }

        internal bool OuterFinished { get; private set; }

        protected override void Start() => Run(Outer());

        async Coroutine Outer() {
            try {
                await Middle();
                OuterFinished = true;
            } finally {
                OuterCleanedUp = true;
            }
        }

        async Coroutine Middle() {
            try {
                for (var frame = 0; frame < 1000; frame++) {
                    await NextFrame();
                    Inner++;
                }
            } finally {
                InnerCleanedUp = true;
            }
        }
    }
}
