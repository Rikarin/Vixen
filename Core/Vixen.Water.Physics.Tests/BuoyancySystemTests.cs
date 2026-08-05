// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Physics;
using Vixen.Physics.Ecs;
using Vixen.Water;
using Vixen.Water.Physics;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Tests;

/// <summary>
///     A crate dropped into a lake settles where Archimedes says — [docs/plan/35 § W7].
/// </summary>
/// <remarks>
///     <para>
///         <b>The kernel's arithmetic is tested in <c>Vixen.Water.Tests</c> and is not retested
///         here.</b> What is here is the join: a component, a rigid body, a fixed step and the phase
///         order between them — and every one of those is silent when it is wrong. A force applied
///         after the step is thrown away by Jolt and the boat simply sinks; a pose read from a
///         component rather than from the simulation is a boat floated where it was last frame; a
///         clock read from the wrong phase is a boat one frame of swell behind the water drawn under
///         it. None of them looks like a bug in the thing that caused it.
///     </para>
///     <para>
///         ⚠ <b>The measurements are made against <c>Buoyancy.RestDisplacement</c> rather than
///         against a number somebody watched happen.</b> A convergence test whose expected value came
///         from running the code is a test that pins the bug as firmly as the behaviour.
///     </para>
/// </remarks>
public sealed class BuoyancySystemTests {
    const float Step = 1f / 60f;

    /// <summary>Still water at a height, everywhere, with no field to bound it.</summary>
    /// <remarks>
    ///     ⚠ <b>A null field is open water at height zero in the evaluator</b> — see
    ///     <c>WaterEvaluator.Sample</c> — so a surface at another height is a query over a field with
    ///     one body in it. That is more setup than a fake would be and it is the point: a fake
    ///     surface would let the join pass while the thing a game actually holds did not.
    /// </remarks>
    sealed class Lake(float height, float extent = 200f) : IWaterSurface {
        readonly WaterQuery query = Build(height, extent);

        public float WaterTime { get; set; }

        public int Asks { get; private set; }

        public WaterQuery? QueryAt(Vector2 position) {
            Asks++;

            return MathF.Abs(position.X) <= extent * 0.5f && MathF.Abs(position.Y) <= extent * 0.5f
                ? query
                : null;
        }

        static WaterQuery Build(float height, float extent) {
            var field = new WaterField(
                new() { Origin = new(-extent * 0.5f, -extent * 0.5f), Extent = extent, Resolution = 65 }
            );

            var lake = new WaterBody(
                WaterBodyKind.Lake,
                new Spline(
                    Spline.SmoothTangents(
                        [
                            new(-80f, height, -80f), new(80f, height, -80f),
                            new(80f, height, 80f), new(-80f, height, 80f)
                        ],
                        closed: true,
                        tension: 1f
                    ),
                    closed: true
                ),
                defaults: new() { Depth = 20f }
            ) {
                SurfaceHeight = height,
                ShoreFalloff = 2f
            };

            field.Rasterize([lake], new FlatWaterGround(height - 20f));

            // ⚠ A dead calm, so the rest height is a number and not a number plus a wave. Every other
            // test here would otherwise be measuring where in its cycle the swell happened to be.
            return new(field, WaterWaveSpectrum.Calm with { WindSpeed = 0f, AmplitudeScale = 0f });
        }
    }

    /// <summary>
    ///     A crate released above still water settles at the analytic displacement of its pontoons.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>§ W7's own exit criterion.</b> A crate that floats is not evidence: one with twice
    ///         the lift also floats, higher, and looks entirely convincing until somebody stands on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The waterline is what is asserted, and the displacement on its own would not have
    ///         been enough.</b> At equilibrium the lift equals the weight by definition, so
    ///         "displaced volume equals <c>RestDisplacement</c>" is true for <em>any</em> coefficient
    ///         — both sides scale by it — and a test asserting only that would pass with the
    ///         coefficient doubled. The height is where the coefficient actually shows, so the cap
    ///         depth is solved for and compared against where the crate ended up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the coefficient here is deliberately not one.</b> At a coefficient of one, a
    ///         solver that dropped the term entirely produces exactly the right answer — so the test
    ///         that is supposed to pin the term would pass with the term deleted. 1.6 is the smallest
    ///         change that makes the assertion mean what it says.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ACrateSettlesAtTheWaterlineItsPontoonsPredict() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var lake = new Lake(0f);
        var buoyancy = new BuoyancySystem(scene, lake);

        var crate = Crate(scene, new(0f, 4f, 0f), mass: 300f, radius: 0.6f, coefficient: 1.6f);

        Advance(scene, buoyancy, entities, 600, lake);

        var authored = entities.Read<BuoyancyBody>(crate);
        var state = entities.Read<BuoyancyState>(crate);
        var settled = entities.Read<LocalTransform>(crate).Position.Y;

        // The volume it has to displace to hold its own weight up, as a fraction of the sphere.
        var displacement = Buoyancy.RestDisplacement(authored.Pontoons, 300f, authored.Settings);
        var fraction = displacement / authored.Pontoons[0].Volume;

        Assert.True(state.IsFloating, "the crate is not in the water at all");
        Assert.Equal(fraction, state.Submerged, 0.01f);

        // And the height that fraction implies, which is the assertion that pins the coefficient:
        // the surface is at zero, so the centre sits a radius below it plus however deep the cap is.
        Assert.Equal(WaterlineOf(0.6f, fraction), settled, 0.02f);
    }

    /// <summary>The centre height of a sphere floating with a given fraction submerged.</summary>
    /// <remarks>
    ///     ⚠ <b>Bisected against <see cref="Buoyancy.SubmergedFraction" /> rather than solved in
    ///     closed form.</b> Inverting the spherical cap is a cubic, and a second implementation of the
    ///     cap here would be a test that agrees with itself; bisecting the shipped function makes the
    ///     assertion about the <em>solver</em> reaching the height the cap predicts.
    /// </remarks>
    static float WaterlineOf(float radius, float fraction) {
        var low = -radius;
        var high = radius;

        for (var step = 0; step < 60; step++) {
            var middle = (low + high) * 0.5f;

            // Deeper centre, more submerged: bisect on the height, surface pinned at zero.
            if (Buoyancy.SubmergedFraction(radius, middle, 0f) < fraction) {
                high = middle;
            } else {
                low = middle;
            }
        }

        return (low + high) * 0.5f;
    }

    /// <summary>
    ///     ⚠ A body outside every zone falls, and its readout says dry rather than going stale.
    /// </summary>
    /// <remarks>
    ///     The negative control for the whole file. Without it, a surface that answered "water
    ///     everywhere" would pass every other test here — and the symptom in a game is a crate
    ///     hovering in the desert.
    /// </remarks>
    [Fact]
    public void ACrateOutsideEveryZoneFalls() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var lake = new Lake(0f, extent: 20f);
        var buoyancy = new BuoyancySystem(scene, lake);

        var crate = Crate(scene, new(500f, 4f, 0f), mass: 300f, radius: 0.6f);

        Advance(scene, buoyancy, entities, 120, lake);

        var state = entities.Read<BuoyancyState>(crate);

        Assert.False(state.IsFloating);
        Assert.Equal(0, state.Wet);
        Assert.Equal(1, state.Total);
        Assert.True(entities.Read<LocalTransform>(crate).Position.Y < 0f, "it did not fall");
        Assert.Equal(0, buoyancy.WetPontoons);
        Assert.Equal(1, buoyancy.Pontoons);
    }

    /// <summary>
    ///     ⚠ A raft with a corner held down pitches, because the force is applied where the pontoon is.
    /// </summary>
    /// <remarks>
    ///     <b>The one behaviour that separates a pontoon list from a single displacement volume.</b> A
    ///     force at the centre of mass floats a hull perfectly level whatever its attitude, which reads
    ///     as a boat on rails — and it is invisible in every test that only measures a height.
    /// </remarks>
    [Fact]
    public void ARaftTippedOnItsSideRightsItself() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var lake = new Lake(0f);
        var buoyancy = new BuoyancySystem(scene, lake);

        var raft = entities.Create(
            LocalTransform.At(new(0f, 0.2f, 0f)) with {
                Rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, 0.6f)
            }
        );

        entities.Add(raft, Collider.Of(scene.Shapes.Box(new Vector3(2f, 0.25f, 3f))));
        entities.Add(raft, RigidBody.Dynamic() with { Mass = 800f });
        entities.Add(raft, BuoyancyBody.Raft(halfLength: 2.5f, halfWidth: 1.5f, radius: 0.7f));
        entities.Add<BuoyancyState>(raft);

        Advance(scene, buoyancy, entities, 900, lake);

        // Upright to within a few degrees: the up axis of the hull, back near the world's.
        var rotation = entities.Read<LocalTransform>(raft).Rotation;
        var up = Quaternion.Transform(Vector3.UnitY, rotation);

        Assert.True(up.Y > 0.98f, $"the raft is still leaning: up.Y = {up.Y}");
    }

    /// <summary>
    ///     ⚠ The system reads the surface's clock, not its own — asserted by moving one and not the
    ///     other.
    /// </summary>
    /// <remarks>
    ///     <b>§ D10's "the fixed step's water time, never a frame time", made measurable.</b> A solver
    ///     that reached for <c>GameTime</c> would produce the same forces here whatever the surface
    ///     said, and the symptom in a game is a force that changes when the frame rate does — which in
    ///     a networked game is a client and a server disagreeing about where a boat is.
    /// </remarks>
    [Fact]
    public void ThePontoonForcesFollowTheSurfacesClockAndNothingElse() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        // A real swell, because a dead calm is the same surface at every time and would pass this
        // whatever the solver read.
        var lake = new Swell();
        var buoyancy = new BuoyancySystem(scene, lake);

        Crate(scene, new(0f, 0.2f, 0f), mass: 300f, radius: 0.6f);
        scene.Synchronize(Step);

        lake.WaterTime = 0f;
        buoyancy.Step(entities);
        var first = buoyancy.Forces[0].SurfaceHeight;

        lake.WaterTime = 3.7f;
        buoyancy.Step(entities);
        var later = buoyancy.Forces[0].SurfaceHeight;

        Assert.NotEqual(first, later, 4);
    }

    /// <summary>Open water with a real sea state, so the surface height depends on the time.</summary>
    sealed class Swell : IWaterSurface {
        readonly WaterQuery query = new(null, WaterWaveSpectrum.Default);

        public float WaterTime { get; set; }

        public WaterQuery? QueryAt(Vector2 position) => query;
    }

    /// <summary>
    ///     ⚠ It runs between the sync and the step, and the ordering is declared rather than assumed.
    /// </summary>
    /// <remarks>
    ///     <b>Both halves are load-bearing and both fail silently.</b> Jolt accumulates forces and
    ///     clears them at the step, so a system ordered after the step applies forces that are thrown
    ///     away — a boat that sinks with the system visibly running and its counters all non-zero. And
    ///     a system before the sync applies forces to bodies the sync has not created yet, which loses
    ///     the first step of every boat.
    /// </remarks>
    [Fact]
    public void ItIsDeclaredBetweenTheSyncAndTheStep() {
        var type = typeof(BuoyancySystem);

        Assert.Equal(
            SystemPhase.FixedUpdate,
            type.GetCustomAttribute<UpdateInGroupAttribute>()!.Phase
        );

        Assert.Contains(
            type.GetCustomAttributes<UpdateAfterAttribute>(),
            attribute => attribute.SystemType == typeof(PhysicsSyncSystem)
        );

        Assert.Contains(
            type.GetCustomAttributes<UpdateBeforeAttribute>(),
            attribute => attribute.SystemType == typeof(PhysicsStepSystem)
        );
    }

    /// <summary>A body with no pontoons is an entity part-way through being authored, not an error.</summary>
    [Fact]
    public void ABodyWithNoPontoonsIsSkipped() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var lake = new Lake(0f);
        var buoyancy = new BuoyancySystem(scene, lake);

        var crate = entities.Create(LocalTransform.At(new(0f, 1f, 0f)));

        entities.Add(crate, Collider.Of(scene.Shapes.Box(0.5f)));
        entities.Add(crate, RigidBody.Dynamic());
        entities.Add(crate, BuoyancyBody.Default);

        scene.Synchronize(Step);
        buoyancy.Step(entities);

        Assert.Equal(0, buoyancy.Pontoons);
        Assert.Equal(0, buoyancy.Floating);
        Assert.Equal(0, lake.Asks);
    }

    /// <summary>An unset coefficient is one and not zero, which would be a boat with no buoyancy.</summary>
    /// <remarks>
    ///     A chunk's column is zeroed memory, so a component added from the inspector without the
    ///     field filled in holds zero — and zero lift is a crate that sinks, which reads as the whole
    ///     system being unwired rather than as a field nobody typed.
    /// </remarks>
    [Fact]
    public void AZeroedComponentStillFloats() {
        var zeroed = default(BuoyancyBody);

        Assert.Equal(1f, zeroed.Settings.Coefficient);
        Assert.Equal(1f, BuoyancyBody.Default.Coefficient);
    }

    /// <summary>
    ///     ⚠ A thousand ticks of a floating body, twice, bit for bit — which is what a predicted body
    ///     rests on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>§ W7's second exit criterion, and it is met by a property rather than by
    ///         machinery.</b> A buoyant body needs no replication of its own: it rides
    ///         <c>Vixen.Net.Physics</c>' existing rigid-body path unchanged, because the force is a
    ///         <em>pure function</em> of things both peers already have — the pontoons (authored), the
    ///         pose and velocity (replicated), the field and the spectrum (content), and the water
    ///         time (one clock, derived from the tick). Nothing about it is a message.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is exactly why no ripples are passed.</b> A wake is a simulation whose state
    ///         <em>is</em> its history, so a client rolling back six ticks cannot ask it where the
    ///         surface was — and a force that depended on one would be a force the two peers computed
    ///         differently. That is § D12's asymmetry, and this test is what would fail if somebody
    ///         helpfully threaded a ripple field through <c>BuoyancySystem</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bit-identical and not "close enough"</b>, on
    ///         <c>PhysicsDeterminismTests</c>' terms: a tolerance here means the divergence is found
    ///         by a desync months later instead of by this file. It pins the thread count for the
    ///         same reason that suite does — Jolt is deterministic for a given thread count and not
    ///         across different ones.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AThousandTicksOfAFloatingBodyRunTheSameTwice() {
        Assert.Equal(Predicted(1000), Predicted(1000));
    }

    static (Vector3 Position, Quaternion Rotation, float Submerged) Predicted(int steps) {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities, new PhysicsWorldSettings { ThreadCount = 1, Deterministic = true });

        var lake = new Lake(0f);
        var buoyancy = new BuoyancySystem(scene, lake);

        // Off-centre and moving, so the body pitches and drifts: a crate dropped straight down and
        // left to settle runs the same twice whether or not anything here is deterministic.
        var raft = entities.Create(LocalTransform.At(new(3f, 2f, -4f)));

        entities.Add(raft, Collider.Of(scene.Shapes.Box(new Vector3(2f, 0.25f, 3f))));
        entities.Add(raft, RigidBody.Dynamic() with { Mass = 800f, AllowSleeping = false });
        entities.Add(raft, BuoyancyBody.Raft(halfLength: 2.5f, halfWidth: 1.5f, radius: 0.7f));
        entities.Add(raft, new LinearVelocity { Value = new(1.5f, 0f, -0.5f) });
        entities.Add<BuoyancyState>(raft);

        Advance(scene, buoyancy, entities, steps, lake);

        var pose = entities.Read<LocalTransform>(raft);

        return (pose.Position, pose.Rotation, entities.Read<BuoyancyState>(raft).Submerged);
    }

    static Entity Crate(
        PhysicsScene scene,
        Vector3 position,
        float mass,
        float radius,
        float coefficient = 1f
    ) {
        var entity = scene.Entities.Create(LocalTransform.At(position));

        scene.Entities.Add(entity, Collider.Of(scene.Shapes.Sphere(radius)));
        scene.Entities.Add(entity, RigidBody.Dynamic() with { Mass = mass, AllowSleeping = false });
        scene.Entities.Add(entity, BuoyancyBody.Sphere(radius) with { Coefficient = coefficient });
        scene.Entities.Add<BuoyancyState>(entity);

        return entity;
    }

    /// <summary>One frame's worth of the phases this join lives between, N times over.</summary>
    /// <remarks>
    ///     Sync, buoyancy, step, writeback — the order <c>ItIsDeclaredBetweenTheSyncAndTheStep</c>
    ///     asserts the attributes ask for, run by hand so a test does not need a runner.
    /// </remarks>
    static void Advance(
        PhysicsScene scene,
        BuoyancySystem buoyancy,
        EcsWorld entities,
        int steps,
        Lake lake
    ) {
        for (var index = 0; index < steps; index++) {
            lake.WaterTime = index * Step;

            scene.Synchronize(Step);
            buoyancy.Step(entities);
            scene.Step(Step);
            scene.Writeback();
        }
    }
}
