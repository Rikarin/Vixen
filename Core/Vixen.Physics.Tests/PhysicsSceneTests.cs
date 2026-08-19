// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Frames;
using Vixen.Engine.Transforms;
using Vixen.Physics.Bodies;
using Vixen.Physics.Constraints;
using Vixen.Physics.Ecs;
using Vixen.Physics.Events;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Vixen.Physics.Tests;

public sealed class PhysicsSceneTests {
    const float Step = 1f / 60f;

    static void Advance(PhysicsScene scene, int steps) {
        for (var step = 0; step < steps; step++) {
            scene.Synchronize(Step);
            scene.Step(Step);
            scene.Writeback();
        }
    }

    static Entity Ground(PhysicsScene scene, float top = 0f) {
        var entity = scene.Entities.Create(LocalTransform.At(new(0f, top - 1f, 0f)));
        scene.Entities.Add(entity, Collider.Of(scene.Shapes.Box(new Vector3(50f, 1f, 50f))));
        return entity;
    }

    static Entity Crate(PhysicsScene scene, Vector3 position, float halfExtent = 0.5f) {
        var entity = scene.Entities.Create(LocalTransform.At(position));
        scene.Entities.Add(entity, Collider.Of(scene.Shapes.Box(halfExtent)));
        scene.Entities.Add(entity, RigidBody.Dynamic());
        return entity;
    }

    [Fact]
    public void AColliderWithNoRigidBodyBecomesAStaticBodyAndAColliderWithOneDoesNot() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var ground = Ground(scene);
        var crate = Crate(scene, new(0f, 5f, 0f));

        scene.Synchronize(Step);

        Assert.Equal(2, scene.BodyCount);
        Assert.True(scene.TryGetBody(ground, out var groundBody));
        Assert.True(scene.TryGetBody(crate, out var crateBody));
        Assert.Equal(BodyMotion.Static, scene.World.GetMotion(groundBody));
        Assert.Equal(BodyMotion.Dynamic, scene.World.GetMotion(crateBody));
    }

    [Fact]
    public void ABodysMotionIsWrittenBackIntoItsTransform() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var crate = Crate(scene, new(0f, 5f, 0f));

        Advance(scene, 240);

        var landed = entities.Read<LocalTransform>(crate).Position;
        Assert.InRange(landed.Y, 0.4f, 0.6f);
    }

    [Fact]
    public void AStaticBodysTransformIsNeverWrittenBack() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var ground = Ground(scene);
        entities.Add<PhysicsInterpolation>(ground);

        Advance(scene, 10);

        // Not merely "unchanged in value" — untouched, so a hundred thousand pieces of level geometry
        // do not dirty their chunk's LocalTransform column every step and wake the transform pass.
        Assert.Equal(new Vector3(0f, -1f, 0f), entities.Read<LocalTransform>(ground).Position);

        // The interpolation state is seeded once, when the body is made, and then left alone: both
        // ends of it are the authored pose, so a renderer that lerps between them draws the same
        // thing whatever the alpha is.
        var interpolation = entities.Read<PhysicsInterpolation>(ground);
        Assert.Equal(new Vector3(0f, -1f, 0f), interpolation.PreviousPosition);
        Assert.Equal(new Vector3(0f, -1f, 0f), interpolation.CurrentPosition);
    }

    [Fact]
    public void VelocityComponentsMirrorTheBodyBothWays() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities, new PhysicsWorldSettings { Gravity = Vector3.Zero });

        var crate = Crate(scene, Vector3.Zero);
        entities.Add(crate, new LinearVelocity { Value = new(2f, 0f, 0f) });

        Advance(scene, 60);

        Assert.True(entities.Read<LocalTransform>(crate).Position.X > 1.5f);
        Assert.True(entities.Read<LinearVelocity>(crate).Value.X > 1.5f);

        entities.Get<LinearVelocity>(crate).Value = new(-2f, 0f, 0f);
        Advance(scene, 60);

        Assert.True(entities.Read<LinearVelocity>(crate).Value.X < 0f);
    }

    [Fact]
    public void RemovingAColliderDestroysTheBodyAndTheComponentThatTrackedIt() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var crate = Crate(scene, new(0f, 5f, 0f));
        scene.Synchronize(Step);

        Assert.True(scene.TryGetBody(crate, out var body));

        entities.Remove<Collider>(crate);
        scene.Synchronize(Step);

        Assert.False(entities.Has<PhysicsBody>(crate));
        Assert.False(scene.World.IsAlive(body));
        Assert.Equal(0, scene.BodyCount);
    }

    [Fact]
    public void ChangingTheShapeRebuildsTheBodyInPlace() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var crate = Crate(scene, new(0f, 5f, 0f));
        scene.Synchronize(Step);

        var before = entities.Read<PhysicsBody>(crate).Handle;

        entities.Get<Collider>(crate).Shape = scene.Shapes.Sphere(1f);
        scene.Synchronize(Step);

        var after = entities.Read<PhysicsBody>(crate).Handle;

        Assert.NotEqual(before, after);
        Assert.False(scene.World.IsAlive(before));
        Assert.True(scene.World.IsAlive(after));
        Assert.Equal(1, scene.BodyCount);
    }

    [Fact]
    public void ChangingTheMotionTypeRebuildsTheBodyAndChangingTheMassDoesNot() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var crate = Crate(scene, new(0f, 5f, 0f));
        scene.Synchronize(Step);

        var before = entities.Read<PhysicsBody>(crate).Handle;

        entities.Get<RigidBody>(crate).Mass = 42f;
        scene.Synchronize(Step);
        Assert.Equal(before, entities.Read<PhysicsBody>(crate).Handle);

        entities.Get<RigidBody>(crate).Motion = BodyMotion.Kinematic;
        scene.Synchronize(Step);

        var after = entities.Read<PhysicsBody>(crate).Handle;
        Assert.NotEqual(before, after);
        Assert.Equal(BodyMotion.Kinematic, scene.World.GetMotion(after));
    }

    [Fact]
    public void AKinematicBodyIsDrivenTowardsItsAuthoredTransform() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        var platform = entities.Create(LocalTransform.At(Vector3.Zero));
        entities.Add(platform, Collider.Of(scene.Shapes.Box(new Vector3(2f, 0.5f, 2f))));
        entities.Add(platform, RigidBody.Kinematic());

        for (var step = 0; step < 60; step++) {
            entities.Get<LocalTransform>(platform).Position = new(step * 0.05f, 0f, 0f);
            Advance(scene, 1);
        }

        Assert.True(scene.TryGetBody(platform, out var body));
        Assert.InRange(scene.World.GetPosition(body).X, 2.5f, 3.1f);
    }

    /// <summary>
    ///     The bridge writes <c>LocalTransform</c> every step for every dynamic body, so "the
    ///     transform changed" says nothing about who changed it. <see cref="PhysicsTeleport" /> is
    ///     how game code says it was them.
    /// </summary>
    [Fact]
    public void ATeleportTagMovesTheBodyAndIsThenTakenBackOff() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities, new PhysicsWorldSettings { Gravity = Vector3.Zero });

        var crate = Crate(scene, Vector3.Zero);
        Advance(scene, 5);

        entities.Get<LocalTransform>(crate).Position = new(100f, 0f, 0f);
        Advance(scene, 1);

        // Without the tag the write is thrown away: the body is still where the simulation put it.
        Assert.True(entities.Read<LocalTransform>(crate).Position.X < 1f);

        entities.Get<LocalTransform>(crate).Position = new(100f, 0f, 0f);
        entities.Add<PhysicsTeleport>(crate);
        Advance(scene, 1);

        Assert.False(entities.Has<PhysicsTeleport>(crate));
        Assert.Equal(100f, entities.Read<LocalTransform>(crate).Position.X, 2);
    }

    /// <summary>
    ///     ⚠ <b>A teleport is not motion, and the smoothing must not draw it as motion.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>PhysicsInterpolationSystem</c> draws between the last two simulated poses, and a
    ///         teleport makes those two poses the two ends of the jump. So the body was drawn
    ///         <i>crossing the level</i> for a whole fixed step — and on a frame exactly one step long,
    ///         where <c>alpha</c> is zero, it was drawn at the position it had left, which is a
    ///         teleport that has visibly not happened yet.
    ///     </para>
    ///     <para>
    ///         The signal is <see cref="PhysicsTeleport" /> and not a distance: a body genuinely moving
    ///         at 200 m/s covers the same gap in a step and must still be smoothed, so any threshold
    ///         that catches this catches that. <c>NetworkRigidBodyCorrectionSystem</c>'s hard snap
    ///         already adds the tag and its comment already says "so nothing draws the body sliding to
    ///         where it was teleported" — this is that sentence being made true.
    ///     </para>
    ///     <para>
    ///         Both alphas, because they fail differently. At zero the drawn pose is the pre-teleport
    ///         one exactly; at one half it is the midpoint of the jump, which is the frame you would
    ///         actually catch in a capture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ATeleportedBodyIsDrawnWhereItWasPutRatherThanSlidingToIt() {
        using var loop = new EngineLoop();
        using var scene = new PhysicsScene(loop.World, new PhysicsWorldSettings { Gravity = Vector3.Zero });

        loop.AddPhysics(scene);

        // Moving, not parked: the body has real motion to smooth on either side of the jump, so this
        // asserts the teleport is excluded rather than that the smoothing is switched off.
        var crate = Crate(scene, Vector3.Zero);
        loop.World.Add(crate, new LinearVelocity { Value = new(2f, 0f, 0f) });
        loop.World.Add<PhysicsInterpolation>(crate);

        for (var frame = 0; frame < 10; frame++) {
            loop.Frame(TimeSpan.FromSeconds(Step));
        }

        var left = loop.World.Read<LocalTransform>(crate).Position.X;
        Assert.InRange(left, 0.1f, 1f);

        loop.World.Get<LocalTransform>(crate).Position = new(100f, 0f, 0f);
        loop.World.Add<PhysicsTeleport>(crate);

        // One whole step, which leaves alpha at zero — every machine holding its refresh rate.
        loop.Frame(TimeSpan.FromSeconds(Step));

        var state = loop.World.Read<PhysicsInterpolation>(crate);

        Assert.Equal(0f, loop.FixedStep.Alpha, 3);

        Assert.True(
            state.PreviousPosition.X > 99f,
            $"The interpolation still holds {state.PreviousPosition.X} as the previous pose, so the "
            + "smoothing has a 100 m segment to slide the body along after a teleport."
        );

        Assert.Equal(100f, loop.World.Read<LocalTransform>(crate).Position.X, 1);

        // And half a step, which runs no simulation and puts alpha in the middle of the segment.
        loop.Frame(TimeSpan.FromSeconds(Step / 2f));

        Assert.InRange(loop.FixedStep.Alpha, 0.4f, 0.6f);
        Assert.Equal(100f, loop.World.Read<LocalTransform>(crate).Position.X, 1);
    }

    [Fact]
    public void ContactsAndTriggersArriveNamingEntities() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities, new PhysicsWorldSettings { Gravity = Vector3.Zero });

        var trigger = entities.Create(LocalTransform.At(Vector3.Zero));
        entities.Add(trigger, Collider.Trigger(scene.Shapes.Box(1f)));

        var wall = entities.Create(LocalTransform.At(new(6f, 0f, 0f)));
        entities.Add(wall, Collider.Of(scene.Shapes.Box(new Vector3(0.5f, 5f, 5f))));

        var mover = Crate(scene, new(-5f, 0f, 0f), 0.25f);
        entities.Add(mover, new LinearVelocity { Value = new(8f, 0f, 0f) });

        var entered = false;
        var left = false;
        var hitWall = false;

        for (var step = 0; step < 300; step++) {
            scene.Synchronize(Step);
            scene.Step(Step);
            scene.Writeback();

            foreach (var trigger1 in scene.Triggers) {
                Assert.Equal(trigger, trigger1.Sensor);
                Assert.Equal(mover, trigger1.Other);
                entered |= trigger1.Phase == ContactPhase.Began;
                left |= trigger1.Phase == ContactPhase.Ended;
            }

            foreach (var contact in scene.Contacts) {
                if (contact.Phase != ContactPhase.Began) {
                    continue;
                }

                hitWall |= (contact.First == wall && contact.Second == mover)
                    || (contact.First == mover && contact.Second == wall);
            }
        }

        Assert.True(entered);
        Assert.True(left);
        Assert.True(hitWall);
    }

    [Fact]
    public void TheLoopExtensionRunsTheWholeChainOnItsOwn() {
        using var loop = new EngineLoop();
        using var scene = new PhysicsScene(loop.World);

        loop.AddPhysics(scene);

        Ground(scene);
        var crate = Crate(scene, new(0f, 5f, 0f));
        loop.World.Add(crate, default(PhysicsInterpolation));

        for (var frame = 0; frame < 300; frame++) {
            loop.Frame(TimeSpan.FromSeconds(Step));
        }

        var landed = loop.World.Read<LocalTransform>(crate).Position;
        Assert.InRange(landed.Y, 0.3f, 0.7f);

        var interpolation = loop.World.Read<PhysicsInterpolation>(crate);
        Assert.InRange(interpolation.CurrentPosition.Y, 0.3f, 0.7f);
    }

    /// <summary>
    ///     A 60 Hz simulation drawn at any other rate shows each step twice and then once, which reads
    ///     as a stutter no frame pacing fixes. The interpolation pass is what removes it, and this is
    ///     the assertion that it actually lands between the two steps rather than on one of them.
    /// </summary>
    [Fact]
    public void AnInterpolatedTransformSitsBetweenTheLastTwoSteps() {
        using var loop = new EngineLoop();
        using var scene = new PhysicsScene(loop.World, new PhysicsWorldSettings { Gravity = Vector3.Zero });

        loop.AddPhysics(scene);

        var crate = Crate(scene, Vector3.Zero);
        loop.World.Add(crate, new LinearVelocity { Value = new(10f, 0f, 0f) });
        loop.World.Add<PhysicsInterpolation>(crate);

        // Whole steps first, so the accumulator is empty and the two poses are a step apart.
        for (var frame = 0; frame < 10; frame++) {
            loop.Frame(TimeSpan.FromSeconds(Step));
        }

        // Then half a step, which runs no simulation and leaves alpha at one half.
        loop.Frame(TimeSpan.FromSeconds(Step / 2f));

        var state = loop.World.Read<PhysicsInterpolation>(crate);
        var drawn = loop.World.Read<LocalTransform>(crate).Position;

        Assert.Equal(0, loop.LastFixedSteps);
        Assert.InRange(loop.FixedStep.Alpha, 0.4f, 0.6f);
        Assert.True(state.CurrentPosition.X > state.PreviousPosition.X);

        var midpoint = (state.PreviousPosition.X + state.CurrentPosition.X) * 0.5f;
        Assert.Equal(midpoint, drawn.X, 3);
    }

    [Fact]
    public void TheDebugOverlayDrawsSomethingForEveryBodyAndNothingWhenItIsOff() {
        using var loop = new EngineLoop();
        using var scene = new PhysicsScene(loop.World);
        var draw = new DebugDraw();
        var overlay = new PhysicsDebugDrawSystem(scene, draw);

        loop.AddPhysics(scene).Add(overlay);

        Ground(scene);
        Crate(scene, new(0f, 5f, 0f));

        loop.Frame(TimeSpan.FromSeconds(Step));
        Assert.Equal(0, draw.Count);

        overlay.Enabled = true;
        draw.Clear();
        loop.Frame(TimeSpan.FromSeconds(Step));

        Assert.True(draw.Count > 0);

        // Constraints are part of the default overlay — docs/plan/13 § Overlays lists them beside
        // collider wireframes and contact points — so adding one has to add lines.
        var withoutJoint = draw.Count;

        var hanging = Crate(scene, new(3f, 5f, 0f));
        scene.Synchronize(Step);

        Assert.True(scene.TryGetBody(hanging, out var body));
        scene.World.CreateConstraint(ConstraintDescription.Point(body, BodyHandle.None, new(3f, 5f, 0f)));

        draw.Clear();
        loop.Frame(TimeSpan.FromSeconds(Step));

        Assert.True(draw.Count > withoutJoint);
    }

    [Fact]
    public void ASceneCanBeGivenAWorldItDoesNotOwn() {
        using var entities = new EcsWorld("Test");
        using var world = new PhysicsWorld();

        using (var scene = new PhysicsScene(entities, world)) {
            Crate(scene, Vector3.Zero);
            scene.Synchronize(Step);
            Assert.Equal(1, world.BodyCount);
        }

        Assert.False(world.IsDisposed);
    }
}
