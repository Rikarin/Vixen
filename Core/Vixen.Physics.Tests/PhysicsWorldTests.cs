// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Events;
using Vixen.Physics.Queries;
using Xunit;

namespace Vixen.Physics.Tests;

public sealed class PhysicsWorldTests {
    const float Step = 1f / 60f;

    [Fact]
    public void AWorldStartsEmptyAndWithEarthGravity() {
        using var world = new PhysicsWorld();

        Assert.Equal(0, world.BodyCount);
        Assert.Equal(0, world.ConstraintCount);
        Assert.Equal(new Vector3(0f, -9.81f, 0f), world.Gravity);
    }

    [Fact]
    public void ADynamicBodyFallsAndAStaticOneDoesNot() {
        using var world = new PhysicsWorld();

        var falling = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), new(0f, 10f, 0f)));
        var fixedBody = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(5f, 10f, 0f)));

        for (var step = 0; step < 60; step++) {
            Assert.Equal(PhysicsStepResult.Ok, world.Step(Step));
        }

        // A second of free fall is about 4.9 m. The tolerance is wide because a semi-implicit Euler
        // integrator over sixty steps is not the closed-form answer, and pinning the exact value
        // would make this a change detector for the solver rather than a test of gravity.
        var fallen = 10f - world.GetPosition(falling).Y;
        Assert.InRange(fallen, 4f, 6f);
        Assert.Equal(new Vector3(5f, 10f, 0f), world.GetPosition(fixedBody));
    }

    [Fact]
    public void ABodyLandsOnStaticGeometryAndStops() {
        using var world = new PhysicsWorld();

        world.CreateBody(BodyDescription.Static(world.Shapes.Box(new Vector3(50f, 1f, 50f)), new(0f, -1f, 0f)));
        var crate = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Box(0.5f), new(0f, 5f, 0f)));

        for (var step = 0; step < 180; step++) {
            world.Step(Step);
        }

        var resting = world.GetPosition(crate);
        Assert.InRange(resting.Y, 0.4f, 0.6f);
    }

    [Fact]
    public void ASettledBodyFallsAsleep() {
        using var world = new PhysicsWorld();

        world.CreateBody(BodyDescription.Static(world.Shapes.Box(new Vector3(50f, 1f, 50f)), new(0f, -1f, 0f)));
        var crate = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Box(0.5f), new(0f, 1f, 0f)));

        Assert.True(world.IsActive(crate));

        for (var step = 0; step < 300; step++) {
            world.Step(Step);
        }

        Assert.False(world.IsActive(crate));
        Assert.Equal(0, world.ActiveBodyCount);
    }

    [Fact]
    public void DestroyingABodyInvalidatesItsHandleAndAnySecondDestroyIsHarmless() {
        using var world = new PhysicsWorld();

        var body = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), Vector3.Zero));
        Assert.True(world.IsAlive(body));

        world.DestroyBody(body);
        Assert.False(world.IsAlive(body));

        world.DestroyBody(body);
        Assert.Throws<PhysicsHandleException>(() => world.GetPosition(body));
    }

    /// <summary>
    ///     The reason <c>BodyHandle</c> carries Jolt's sequence number: without it, a handle to a
    ///     destroyed body silently addresses whichever body took the freed index.
    /// </summary>
    [Fact]
    public void AHandleToADestroyedBodyDoesNotAddressTheBodyThatReplacedIt() {
        using var world = new PhysicsWorld();

        var first = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), Vector3.Zero));
        world.DestroyBody(first);

        var second = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), new(1f, 2f, 3f)));

        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first, second);
        Assert.False(world.IsAlive(first));
        Assert.True(world.IsAlive(second));
    }

    [Fact]
    public void AnImpulseMovesABodyInTheDirectionItWasGiven() {
        using var world = new PhysicsWorld(new() { Gravity = Vector3.Zero });

        // A kilogram, so the impulse is a velocity in the same numbers. Left to the shape's density
        // a half-metre sphere weighs half a tonne, and 10 N·s moves it two centimetres a second.
        var body = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), Vector3.Zero) with { Mass = 1f }
        );

        world.ApplyImpulse(body, new(10f, 0f, 0f));

        for (var step = 0; step < 30; step++) {
            world.Step(Step);
        }

        Assert.True(world.GetPosition(body).X > 1f);
        Assert.True(world.GetLinearVelocity(body).X > 0f);
    }

    [Fact]
    public void ALockedAxisIsNeverIntegrated() {
        using var world = new PhysicsWorld(new() { Gravity = Vector3.Zero });

        var body = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), Vector3.Zero) with {
                Mass = 1f,
                DegreesOfFreedom = BodyDegreesOfFreedom.TranslationX
            }
        );

        world.ApplyImpulse(body, new(5f, 5f, 5f));

        for (var step = 0; step < 30; step++) {
            world.Step(Step);
        }

        var position = world.GetPosition(body);
        Assert.True(position.X > 0.1f);
        Assert.Equal(0f, position.Y, 5);
        Assert.Equal(0f, position.Z, 5);
    }

    [Fact]
    public void AKinematicBodyGoesWhereItIsDrivenAndIsNotPulledDown() {
        using var world = new PhysicsWorld();

        var platform = world.CreateBody(
            BodyDescription.Kinematic(world.Shapes.Box(new Vector3(2f, 0.5f, 2f)), Vector3.Zero)
        );

        for (var step = 0; step < 60; step++) {
            var target = new Vector3(step * 0.05f, 0f, 0f);
            world.MoveKinematic(platform, target, Quaternion.Identity, Step);
            world.Step(Step);
        }

        var position = world.GetPosition(platform);
        Assert.InRange(position.X, 2.5f, 3.1f);
        Assert.Equal(0f, position.Y, 3);
    }

    [Fact]
    public void ADynamicBodyCannotUseAMeshShape() {
        using var world = new PhysicsWorld();

        Vector3[] vertices = [Vector3.Zero, new(1f, 0f, 0f), new(0f, 0f, 1f)];
        var mesh = world.Shapes.Mesh(vertices, [0, 1, 2]);

        var error = Assert.Throws<PhysicsShapeException>(
            () => world.CreateBody(BodyDescription.Dynamic(mesh, Vector3.Zero))
        );

        Assert.Contains("inertia", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyOnALayerThatCollidesWithNothingPassesStraightThrough() {
        var layers = PhysicsLayers.Define()
            .Add("Ground", PhysicsBroadPhase.Static)
            .Add("Ghost")
            .Separate("Ground", "Ghost")
            .Build();

        using var world = new PhysicsWorld(new() { Layers = layers });

        Assert.True(layers.TryFind("Ground", out var ground));
        Assert.True(layers.TryFind("Ghost", out var ghost));
        Assert.False(layers.Collide(ground, ghost));

        world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(new Vector3(50f, 1f, 50f)), new(0f, -1f, 0f)) with {
                Layer = ground
            }
        );

        var falling = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), new(0f, 5f, 0f)) with { Layer = ghost }
        );

        for (var step = 0; step < 120; step++) {
            world.Step(Step);
        }

        Assert.True(world.GetPosition(falling).Y < -1f);
    }

    [Fact]
    public void TwoBodiesThatTouchRaiseAContactThatBeginsAndThenEnds() {
        using var world = new PhysicsWorld(new() { Gravity = Vector3.Zero });

        var first = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), new(-2f, 0f, 0f)));
        world.CreateBody(BodyDescription.Static(world.Shapes.Sphere(0.5f), Vector3.Zero));
        world.SetLinearVelocity(first, new(20f, 0f, 0f));

        var began = false;
        var ended = false;

        for (var step = 0; step < 120 && !ended; step++) {
            world.Step(Step);

            foreach (var contact in world.Contacts) {
                began |= contact.Phase == ContactPhase.Began;
                ended |= contact.Phase == ContactPhase.Ended;
            }
        }

        Assert.True(began);
        Assert.True(ended);
    }

    [Fact]
    public void ASensorReportsEntryAndExitAndStopsNothing() {
        using var world = new PhysicsWorld(new() { Gravity = Vector3.Zero });

        var sensor = world.CreateBody(BodyDescription.Trigger(world.Shapes.Box(1f), Vector3.Zero));
        var mover = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(-5f, 0f, 0f)));
        world.SetLinearVelocity(mover, new(10f, 0f, 0f));

        var entered = false;
        var left = false;

        for (var step = 0; step < 120 && !left; step++) {
            world.Step(Step);

            foreach (var trigger in world.Triggers) {
                Assert.Equal(sensor, trigger.Sensor);
                Assert.Equal(mover, trigger.Other);
                entered |= trigger.Phase == ContactPhase.Began;
                left |= trigger.Phase == ContactPhase.Ended;
            }
        }

        Assert.True(entered);
        Assert.True(left);

        // Passed through rather than bounced off: a sensor resolves nothing.
        Assert.True(world.GetPosition(mover).X > 1f);
    }

    /// <summary>
    ///     Bounds are read through a body lock, because <c>BodyInterface.GetTransformedShape</c> in
    ///     JoltPhysicsSharp 2.22.0 hands back an identity transform — so its "world space" bounds are
    ///     the shape's own, sitting at the origin whatever the body is doing.
    /// </summary>
    [Fact]
    public void TheBoundsOfABodyFollowIt() {
        using var world = new PhysicsWorld();

        var body = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.5f), new(10f, 3f, -4f)));
        var bounds = world.GetBounds(body);

        Assert.Equal(10f, bounds.Center.X, 2);
        Assert.Equal(3f, bounds.Center.Y, 2);
        Assert.Equal(-4f, bounds.Center.Z, 2);
        Assert.InRange(bounds.Maximum.X - bounds.Minimum.X, 0.9f, 1.2f);
    }

    [Fact]
    public void AFastBodyTunnelsThroughAThinWallUntilContinuousDetectionIsTurnedOn() {
        static float CrossWall(BodyMotionQuality quality) {
            using var world = new PhysicsWorld(new() { Gravity = Vector3.Zero });

            world.CreateBody(
                BodyDescription.Static(world.Shapes.Box(new Vector3(5f, 5f, 0.02f)), Vector3.Zero)
            );

            var bullet = world.CreateBody(
                BodyDescription.Dynamic(world.Shapes.Sphere(0.02f), new(0f, 0f, -5f)) with {
                    MotionQuality = quality,
                    Layer = new(1),
                    LinearVelocity = new(0f, 0f, 400f)
                }
            );

            for (var step = 0; step < 10; step++) {
                world.Step(Step);
            }

            return world.GetPosition(bullet).Z;
        }

        Assert.True(CrossWall(BodyMotionQuality.Discrete) > 0f);
        Assert.True(CrossWall(BodyMotionQuality.Continuous) < 0f);
    }
}
