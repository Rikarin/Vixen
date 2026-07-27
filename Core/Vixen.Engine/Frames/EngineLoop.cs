// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Frames;

/// <summary>
///     A world, its systems, its behaviours and the clock — one frame at a time.
/// </summary>
/// <remarks>
///     <para>
///         Every step is public and every phase can be run on its own, so an editor's play mode, a
///         determinism test and the application host all drive the same loop rather than three
///         near-copies of it. That is [17](../../../docs/plan/17-app-heads-and-shipping.md)'s rule
///         that nothing in the boot path is inaccessible, applied one layer up.
///     </para>
///     <para>
///         The loop owns no clock of its own. <see cref="Frame" /> is told how much time passed,
///         which is what makes a fixed-step replay from an input log possible at all — the same
///         sequence of deltas produces the same sequence of frames, with no reference to a wall
///         clock anywhere.
///     </para>
/// </remarks>
public sealed class EngineLoop : IDisposable {
    readonly bool ownsWorld;

    /// <summary>The world everything operates on.</summary>
    public World World { get; }

    /// <summary>The systems, in their phases.</summary>
    public SystemRunner Systems { get; }

    /// <summary>The behaviours attached to this world's entities.</summary>
    public BehaviorStore Behaviors { get; }

    /// <summary>How the frame delta is turned into whole simulation steps.</summary>
    public FixedStepAccumulator FixedStep { get; }

    /// <summary>The clock, as of the last frame.</summary>
    public GameTime Time { get; private set; } = GameTime.Zero;

    /// <summary>How many fixed steps the last frame ran.</summary>
    public int LastFixedSteps { get; private set; }

    /// <summary>Creates a loop.</summary>
    /// <param name="world">The world, or <see langword="null" /> to make and own one.</param>
    /// <param name="jobs">The job scheduler systems hand work to, or <see langword="null" />.</param>
    /// <param name="fixedStep">The accumulator, or <see langword="null" /> for sixty steps a second.</param>
    /// <param name="registerDefaultSystems">
    ///     Whether to register the behaviour lifecycle, the behaviour update passes and the transform
    ///     pass. Off is for a test that wants an empty graph.
    /// </param>
    public EngineLoop(
        World? world = null,
        JobScheduler? jobs = null,
        FixedStepAccumulator? fixedStep = null,
        bool registerDefaultSystems = true
    ) {
        ownsWorld = world is null;
        World = world ?? new World("Game");
        FixedStep = fixedStep ?? new FixedStepAccumulator();
        Behaviors = new(World);
        Systems = new(World, jobs);

        if (registerDefaultSystems) {
            Systems.Add(new BehaviorLifecycleSystem(Behaviors))
                .Add(new BehaviorUpdateSystem(Behaviors))
                .Add(new BehaviorLateUpdateSystem(Behaviors))
                .Add(new TransformSystem());
        }
    }

    /// <summary>Adds a system. Its phase and ordering come from its attributes.</summary>
    /// <param name="system">The system.</param>
    /// <returns>This loop, for chaining.</returns>
    public EngineLoop Add(ISystem system) {
        Systems.Add(system);
        return this;
    }

    /// <summary>Runs one frame: every phase in order, with fixed update run as many times as it is owed.</summary>
    /// <param name="elapsed">How much unscaled time has passed since the previous frame.</param>
    /// <param name="timeScale">The time scale to apply. Zero pauses the simulation.</param>
    public void Frame(TimeSpan elapsed, float timeScale = 1f) {
        Time = Time.Advance(elapsed, timeScale);

        Systems.RunPhase(SystemPhase.EarlyUpdate, Time);
        Systems.RunPhase(SystemPhase.Input, Time);

        // Scaled elapsed, so a paused or slowed game runs fewer steps rather than the same number of
        // shorter ones — a fixed step whose length changed would not be a fixed step.
        LastFixedSteps = FixedStep.Advance(Time.Elapsed);
        var stepTime = FixedStep.StepTime(Time);

        for (var step = 0; step < LastFixedSteps; step++) {
            Systems.RunPhase(SystemPhase.FixedUpdate, stepTime);
        }

        Systems.RunPhase(SystemPhase.Update, Time);
        Systems.RunPhase(SystemPhase.Animation, Time);
        Systems.RunPhase(SystemPhase.LateUpdate, Time);
        Systems.RunPhase(SystemPhase.PreRender, Time);
        Systems.RunPhase(SystemPhase.Render, Time);
        Systems.RunPhase(SystemPhase.PostRender, Time);
    }

    /// <inheritdoc />
    public void Dispose() {
        Systems.Dispose();

        if (ownsWorld) {
            World.Dispose();
        }
    }
}
