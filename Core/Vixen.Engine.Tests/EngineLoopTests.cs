// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class EngineLoopTests {
    // Exact. TimeSpan.FromSeconds(1d / 60d) rounds to seventeen milliseconds.
    static readonly TimeSpan Sixtieth = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    [Fact]
    public void ExactlyOneStepIsOwedForExactlyOneStepOfTime() {
        var accumulator = new FixedStepAccumulator(Sixtieth);

        Assert.Equal(1, accumulator.Advance(Sixtieth));
        Assert.Equal(0, accumulator.Advance(TimeSpan.Zero));
    }

    [Fact]
    public void TimeShorterThanAStepIsBankedRatherThanLost() {
        var accumulator = new FixedStepAccumulator(TimeSpan.FromMilliseconds(30));

        Assert.Equal(0, accumulator.Advance(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(0, accumulator.Advance(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(1, accumulator.Advance(TimeSpan.FromMilliseconds(10)));
    }

    /// <summary>
    ///     Ten thousand frames of exactly one step must be exactly ten thousand steps. Counting in
    ///     `double` seconds instead of ticks loses one every so often, and the loss is silent.
    /// </summary>
    [Fact]
    public void AnHourOfIdenticalFramesDoesNotDrift() {
        var accumulator = new FixedStepAccumulator(Sixtieth);
        var steps = 0;

        for (var frame = 0; frame < 10_000; frame++) {
            steps += accumulator.Advance(Sixtieth);
        }

        Assert.Equal(10_000, steps);
        Assert.Equal(0, accumulator.DroppedSteps);
    }

    [Fact]
    public void AlphaSaysHowFarThroughTheNextStepTheFrameIs() {
        var accumulator = new FixedStepAccumulator(Sixtieth);

        accumulator.Advance(Sixtieth / 2);

        Assert.True(Math.Abs(accumulator.Alpha - 0.5f) < 1e-3f, $"{accumulator.Alpha}");
    }

    /// <summary>
    ///     The spiral of death: a frame that owes sixty steps makes the next frame owe sixty more.
    ///     The clamp discards the debt so the simulation runs slow for one frame and then recovers.
    /// </summary>
    [Fact]
    public void AStallIsClampedAndTheDebtIsDroppedVisibly() {
        var accumulator = new FixedStepAccumulator(Sixtieth, maxStepsPerFrame: 5);

        Assert.Equal(5, accumulator.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(55, accumulator.DroppedSteps);

        // And the next frame is a normal frame, not sixty more steps of catch-up.
        Assert.Equal(1, accumulator.Advance(Sixtieth));
    }

    [Fact]
    public void NegativeElapsedTimeIsRefused() {
        var accumulator = new FixedStepAccumulator(Sixtieth);

        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AFixedStepSeesTheStepAsItsDeltaAndNotTheFramesDelta() {
        var log = new List<(SystemPhase Phase, float Delta)>();
        // Ten, not the default five: a hundred milliseconds owes six steps, and the point here is
        // the arithmetic rather than the clamp, which has its own test.
        using var loop = new EngineLoop(fixedStep: new(Sixtieth, 10), registerDefaultSystems: false);
        loop.Add(new Simulate(log));

        loop.Frame(TimeSpan.FromMilliseconds(100));

        Assert.Equal(6, loop.LastFixedSteps);
        Assert.Equal(6, log.Count);
        Assert.All(log, entry => Assert.True(Math.Abs(entry.Delta - (1f / 60f)) < 1e-5f, $"{entry.Delta}"));
    }

    [Fact]
    public void APausedGameRunsNoFixedStepsButStillRunsTheRestOfTheFrame() {
        var log = new List<(SystemPhase Phase, float Delta)>();
        using var loop = new EngineLoop(fixedStep: new(Sixtieth), registerDefaultSystems: false);
        loop.Add(new Simulate(log)).Add(new Think(log));

        loop.Frame(TimeSpan.FromMilliseconds(100), timeScale: 0f);

        Assert.Equal(0, loop.LastFixedSteps);
        Assert.Equal([SystemPhase.Update], log.Select(entry => entry.Phase));
    }

    [Fact]
    public void ThePhasesRunInTheirDeclaredOrder() {
        var log = new List<(SystemPhase Phase, float Delta)>();
        using var loop = new EngineLoop(registerDefaultSystems: false);

        // Registered last-phase-first, so passing means the phase order won and not the registration
        // order.
        loop.Add(new Present(log)).Add(new Think(log)).Add(new Poll(log)).Add(new Simulate(log));

        loop.Frame(Sixtieth);

        Assert.Equal(
            [SystemPhase.Input, SystemPhase.FixedUpdate, SystemPhase.Update, SystemPhase.PostRender],
            log.Select(entry => entry.Phase)
        );
    }

    [Fact]
    public void ASystemIsToldWhichPhaseItIsRunningIn() {
        var log = new List<(SystemPhase Phase, float Delta)>();
        using var loop = new EngineLoop(registerDefaultSystems: false);
        loop.Add(new Poll(log));

        loop.Frame(Sixtieth);

        Assert.Equal(SystemPhase.Input, Assert.Single(log).Phase);
    }

    [Fact]
    public void TheClockAdvancesByWhatTheCallerSaidAndNotByAWallClock() {
        using var loop = new EngineLoop(registerDefaultSystems: false);

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(2, loop.Time.FrameCount);
        Assert.Equal(TimeSpan.FromMilliseconds(32), loop.Time.Total);
    }

    [Fact]
    public void TheDefaultSystemsAreTheBehaviourPassesAndTheTransformPass() {
        using var loop = new EngineLoop();
        var names = loop.Systems.Graph.All.Select(node => node.Name).ToArray();

        Assert.Equal(
            ["BehaviorLifecycleSystem", "BehaviorUpdateSystem", "BehaviorLateUpdateSystem", "TransformSystem"],
            names
        );
    }

    /// <summary>Records the phase it ran in and the delta it was handed. One type per phase.</summary>
    abstract class Recorder(List<(SystemPhase Phase, float Delta)> log) : SystemBase, IDeclaredAccess {
        public SystemAccess Access => SystemAccess.None;

        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            log.Add((context.Phase, context.Time.DeltaSeconds));
            return dependency;
        }
    }

    [UpdateInGroup(SystemPhase.Input)]
    sealed class Poll(List<(SystemPhase Phase, float Delta)> log) : Recorder(log);

    [UpdateInGroup(SystemPhase.FixedUpdate)]
    sealed class Simulate(List<(SystemPhase Phase, float Delta)> log) : Recorder(log);

    [UpdateInGroup(SystemPhase.Update)]
    sealed class Think(List<(SystemPhase Phase, float Delta)> log) : Recorder(log);

    [UpdateInGroup(SystemPhase.PostRender)]
    sealed class Present(List<(SystemPhase Phase, float Delta)> log) : Recorder(log);
}
