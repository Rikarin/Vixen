// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Physics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Ecs;
using Xunit;

namespace Vixen.Ai.Perception.Tests;

/// <summary>
///     P3's first exit criterion: five hundred listeners and five hundred sources hold a frame budget
///     with the broad phase and miss it without one, with both numbers recorded.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The budget is measured and reported; what is <i>asserted</i> is the work.</b> A
///         millisecond threshold is a different number on every machine — this repository builds
///         Debug locally and Release in CI, so it would not even be one number here — and P1's cost
///         test settled the same question the same way. What is asserted instead is the claim doc 37
///         § D15 actually makes: a scan examines <c>listeners × sources</c> by construction, a grid
///         examines a number set by the query radius and the local density, and the second is a small
///         fraction of the first at a population a game would ship.
///     </para>
///     <para>
///         Both times go into the message whether or not it fails, so the number a reader wants —
///         what this cost on the machine that ran it — comes out of the run rather than out of a
///         document that will go stale. <see cref="Budget" /> is the figure doc 37 records, and it is
///         reported against rather than enforced.
///     </para>
/// </remarks>
public class PerceptionCostTests {
    const int Listeners = 500;
    const int Sources = 500;

    /// <summary>The figure doc 37 § P3 records for this frame, in milliseconds.</summary>
    /// <remarks>
    ///     A quarter of a 60 Hz frame, for the worst case this test constructs: every one of five
    ///     hundred listeners sensing on one tick, which is the thing the interval and its deviation
    ///     exist to prevent. At the shipped 10 Hz that frame is a tenth of this.
    /// </remarks>
    const double Budget = 4.0;

    [Fact]
    public void FiveHundredListenersAndFiveHundredSourcesCostFarLessWithTheBroadPhase() {
        var (fast, quick) = Measure(broadPhase: true);
        var (slow, scanned) = Measure(broadPhase: false);

        var report = $"broad phase: {fast.Examined} examined in {quick:0.000} ms; "
            + $"scan: {slow.Examined} examined in {scanned:0.000} ms; recorded budget {Budget:0.000} ms.";

        // The scan is the product, exactly, and it is what a naive implementation costs.
        Assert.Equal(Listeners * Sources, slow.Examined);
        Assert.Equal(Listeners, fast.Passes);
        Assert.Equal(slow.Candidates, fast.Candidates);

        Assert.True(fast.Examined * 20 < slow.Examined, report);
        Assert.True(quick < scanned, report);
    }

    static (PerceptionStats Stats, double Milliseconds) Measure(bool broadPhase) {
        // Every listener senses every frame, so a frame is the whole population's worth of work —
        // which is what "a frame budget" has to mean for a number to be comparable. Sight and hearing
        // and not all five, because those two are PerceptionConfig's own default and are what a game
        // turns on.
        var fleet = new Fleet(
            Fleet.Everything(SenseMask.Sight | SenseMask.Hearing) with { Interval = 0f, RandomDeviation = 0f }
        );

        fleet.System.BroadPhase = broadPhase;

        // Five hundred of each over a 400-metre square: a village, at the density a village has.
        for (var index = 0; index < Listeners; index++) {
            fleet.Listener(Spread(index, 1), team: 0);
        }

        for (var index = 0; index < Sources; index++) {
            fleet.Source(Spread(index, 2), team: 1);
        }

        fleet.Step(3);

        var clock = Stopwatch.StartNew();

        fleet.Step();
        clock.Stop();

        return (fleet.System.LastStats, clock.Elapsed.TotalMilliseconds);
    }

    static Vector3 Spread(int index, uint salt) {
        var value = ((uint)index * 2_654_435_761u) ^ (salt * 0x9E3779B9u);

        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;

        return new(
            ((value & 0xFFFF) / 65535f * 400f) - 200f,
            0f,
            ((value >> 16) / 65535f * 400f) - 200f
        );
    }
}

/// <summary>
///     P3's second exit criterion: a sight test asserting occlusion against real
///     <c>Vixen.Physics</c> geometry rather than against a mock.
/// </summary>
/// <remarks>
///     ⚠ <b>Against a real solver on purpose.</b> A mock occlusion tester asserts that the perception
///     pass calls something; it cannot catch the two things that actually go wrong — a ray that starts
///     inside the listener's own collider and reports itself as the blocker, and a ray that reaches
///     the target and reports the <i>target's</i> collider as the blocker. Both produce an agent that
///     can never see anything, and both look exactly like a correct implementation from a mock's point
///     of view.
/// </remarks>
public class SightOcclusionTests {
    [Fact]
    public void AWallBlocksSightAndRemovingItRestoresIt() {
        using var physics = new PhysicsWorld();
        var fleet = Watching(physics);
        var listener = fleet.Listener(Vector3.Zero);

        fleet.Source(new(0f, 0f, -10f));

        // A slab across the line of sight, five metres out, three metres high.
        var wall = physics.CreateBody(BodyDescription.Static(physics.Shapes.Box(new Vector3(6f, 3f, 0.25f)), new(0f, 1.5f, -5f)));

        fleet.Step();

        Assert.False(fleet.Perceived(listener).IsPerceiving(SenseMask.Sight));
        Assert.Equal(1, fleet.System.LastStats.Traces);

        physics.DestroyBody(wall);
        fleet.Step();

        Assert.True(fleet.Perceived(listener).IsPerceiving(SenseMask.Sight));
    }

    /// <summary>
    ///     ⚠ The target's own collider is the thing the ray is supposed to hit. Excluding it as well as
    ///     the listener's would report a clear line to a target standing behind a wall whose collider
    ///     it happens to be inside; not handling it at all makes every target with a body invisible.
    /// </summary>
    [Fact]
    public void TheTargetsOwnColliderIsNotWhatBlocksTheViewOfIt() {
        using var physics = new PhysicsWorld();
        var fleet = Watching(physics);
        var listener = fleet.Listener(Vector3.Zero);
        var target = fleet.Source(new(0f, 0f, -10f));
        var body = physics.CreateBody(BodyDescription.Static(physics.Shapes.Box(new Vector3(0.5f, 1f, 0.5f)), new(0f, 1f, -10f)));

        fleet.World.Add(target, new PhysicsBody { Handle = body });
        fleet.Step();

        Assert.True(fleet.Perceived(listener).IsPerceiving(SenseMask.Sight));
    }

    /// <summary>
    ///     ⚠ And the listener's own collider, which the eye is usually inside. A ray that starts inside
    ///     a convex shape is not reliably outside it, so the query excludes the asking body rather than
    ///     hoping.
    /// </summary>
    [Fact]
    public void TheListenersOwnColliderDoesNotBlindIt() {
        using var physics = new PhysicsWorld();
        var fleet = Watching(physics);
        var listener = fleet.Listener(Vector3.Zero);

        fleet.Source(new(0f, 0f, -10f));

        var body = physics.CreateBody(BodyDescription.Static(physics.Shapes.Box(new Vector3(0.5f, 2f, 0.5f)), new(0f, 1.7f, 0f)));

        fleet.World.Add(listener, new PhysicsBody { Handle = body });
        fleet.Step();

        Assert.True(fleet.Perceived(listener).IsPerceiving(SenseMask.Sight));
    }

    /// <summary>The trace is last and only runs for what survived the radius and the cone.</summary>
    [Fact]
    public void NothingOutsideTheConeIsEverTraced() {
        using var physics = new PhysicsWorld();
        var fleet = Watching(physics, cone: 90f);

        fleet.Listener(Vector3.Zero);
        fleet.Source(new(0f, 0f, -10f));
        fleet.Source(new(0f, 0f, 10f));
        fleet.Source(new(14f, 0f, 14f));
        fleet.Step();

        // Four sources are inside the radius; one is inside the cone.
        Assert.Equal(3, fleet.System.LastStats.Candidates);
        Assert.Equal(1, fleet.System.LastStats.Traces);
    }

    static Fleet Watching(PhysicsWorld physics, float cone = 360f) {
        var fleet = new Fleet(
            Fleet.Everything() with {
                Senses = SenseMask.Sight,
                Sight = new() { Radius = 20f, LoseSightRadius = 20f, ConeDegrees = cone, Occlusion = true }
            }
        );

        fleet.System.Occlusion = new PhysicsOcclusion(physics);

        return fleet;
    }
}
