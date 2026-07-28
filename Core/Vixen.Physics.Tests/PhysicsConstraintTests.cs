// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Constraints;
using Xunit;

namespace Vixen.Physics.Tests;

public sealed class PhysicsConstraintTests {
    const float Step = 1f / 60f;

    [Fact]
    public void APointConstraintHoldsTwoBodiesTogetherWhileLettingThemTurn() {
        using var world = new PhysicsWorld();

        var anchor = world.CreateBody(BodyDescription.Static(world.Shapes.Sphere(0.25f), new(0f, 5f, 0f)));

        var hanging = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(0f, 4f, 0f)) with { Mass = 1f }
        );

        var joint = world.CreateConstraint(
            ConstraintDescription.Point(anchor, hanging, new(0f, 4.5f, 0f))
        );

        Assert.True(world.IsAlive(joint));
        Assert.Equal(1, world.ConstraintCount);

        for (var step = 0; step < 240; step++) {
            world.Step(Step);
        }

        // It swings, but the distance from the anchor cannot grow: a point constraint is rigid.
        var separation = (world.GetPosition(hanging) - new Vector3(0f, 5f, 0f)).Length();
        Assert.InRange(separation, 0.9f, 1.1f);
    }

    [Fact]
    public void ADistanceConstraintLetsABodyFallOnlyAsFarAsTheRopeIsLong() {
        using var world = new PhysicsWorld();

        var anchor = world.CreateBody(BodyDescription.Static(world.Shapes.Sphere(0.1f), new(0f, 10f, 0f)));

        var weight = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(0f, 9f, 0f)) with { Mass = 5f }
        );

        world.CreateConstraint(
            ConstraintDescription.Distance(anchor, weight, new(0f, 10f, 0f), new(0f, 9f, 0f), 0f, 3f)
        );

        for (var step = 0; step < 300; step++) {
            world.Step(Step);
        }

        var drop = 10f - world.GetPosition(weight).Y;
        Assert.InRange(drop, 2.8f, 3.2f);
    }

    [Fact]
    public void AConstraintCanBePinnedToTheWorldWithNoSecondBody() {
        using var world = new PhysicsWorld();

        var weight = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(0f, 5f, 0f)) with { Mass = 1f }
        );

        var joint = world.CreateConstraint(
            ConstraintDescription.Point(weight, BodyHandle.None, new(0f, 5f, 0f))
        );

        world.GetConstraintBodies(joint, out var first, out var second);
        Assert.Equal(weight, first);
        Assert.True(second.IsNone);

        for (var step = 0; step < 240; step++) {
            world.Step(Step);
        }

        Assert.InRange(world.GetPosition(weight).Y, 4.9f, 5.1f);
    }

    [Fact]
    public void AHingeLimitStopsTheBodyAtTheAngleItWasGiven() {
        using var world = new PhysicsWorld();

        var post = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.1f), Vector3.Zero));

        var arm = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Box(new Vector3(1f, 0.1f, 0.1f)), new(1f, 0f, 0f)) with {
                Mass = 1f
            }
        );

        world.CreateConstraint(
            ConstraintDescription.Hinge(post, arm, Vector3.Zero, Vector3.UnitZ) with {
                LimitMinimum = -0.1f,
                LimitMaximum = 0.1f
            }
        );

        for (var step = 0; step < 300; step++) {
            world.Step(Step);
        }

        // Free, the arm would hang straight down at y ≈ −1. Held to a tenth of a radian either way,
        // its far end can only dip about 10 cm.
        Assert.True(world.GetPosition(arm).Y > -0.2f);
    }

    [Fact]
    public void AMotorisedHingeDrivesItselfRound() {
        using var world = new PhysicsWorld(new() { Gravity = Vector3.Zero });

        var post = world.CreateBody(BodyDescription.Static(world.Shapes.Box(0.1f), Vector3.Zero));

        var wheel = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Box(new Vector3(1f, 0.1f, 0.1f)), new(1f, 0f, 0f)) with {
                Mass = 1f
            }
        );

        var joint = world.CreateConstraint(
            ConstraintDescription.Hinge(post, wheel, Vector3.Zero, Vector3.UnitZ) with {
                Motor = ConstraintMotor.Velocity,
                MotorTarget = 5f,
                MotorMaximum = 1000f
            }
        );

        for (var step = 0; step < 60; step++) {
            world.Step(Step);
        }

        Assert.True(world.GetAngularVelocity(wheel).Z > 1f);

        world.SetConstraintMotor(joint, ConstraintMotor.Off, 0f);
        world.SetAngularVelocity(wheel, Vector3.Zero);

        for (var step = 0; step < 60; step++) {
            world.Step(Step);
        }

        Assert.True(MathF.Abs(world.GetAngularVelocity(wheel).Z) < 1f);
    }

    [Fact]
    public void AskingAJointWithNoMotorToDriveIsAnErrorRatherThanSilence() {
        using var world = new PhysicsWorld();

        var first = world.CreateBody(BodyDescription.Static(world.Shapes.Sphere(0.25f), Vector3.Zero));

        var second = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(0f, -1f, 0f)) with { Mass = 1f }
        );

        var joint = world.CreateConstraint(ConstraintDescription.Point(first, second, Vector3.Zero));

        Assert.Throws<PhysicsHandleException>(
            () => world.SetConstraintMotor(joint, ConstraintMotor.Velocity, 1f)
        );
    }

    [Fact]
    public void ADisabledConstraintStopsActingAndCanBeTurnedBackOn() {
        using var world = new PhysicsWorld();

        var anchor = world.CreateBody(BodyDescription.Static(world.Shapes.Sphere(0.1f), new(0f, 10f, 0f)));

        var weight = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(0f, 9f, 0f)) with { Mass = 1f }
        );

        var joint = world.CreateConstraint(
            ConstraintDescription.Point(anchor, weight, new(0f, 9.5f, 0f))
        );

        for (var step = 0; step < 60; step++) {
            world.Step(Step);
        }

        var held = world.GetPosition(weight).Y;
        Assert.InRange(held, 8.5f, 9.5f);

        world.SetConstraintEnabled(joint, false);
        world.Activate(weight);

        for (var step = 0; step < 120; step++) {
            world.Step(Step);
        }

        Assert.True(world.GetPosition(weight).Y < held - 1f);
    }

    /// <summary>
    ///     The overlay draws a joint where it is now, not where it was authored, so the anchors have
    ///     to be kept in body space and transformed back out. The gap between the two is the error
    ///     the solver has not worked off.
    /// </summary>
    [Fact]
    public void AConstraintsAnchorsFollowItsBodiesAndMeetWhenItIsSatisfied() {
        using var world = new PhysicsWorld(new() { Gravity = Vector3.Zero });

        var anchor = world.CreateBody(BodyDescription.Static(world.Shapes.Sphere(0.25f), new(0f, 5f, 0f)));

        var hanging = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(0f, 4f, 0f)) with { Mass = 1f }
        );

        var joint = world.CreateConstraint(
            ConstraintDescription.Hinge(anchor, hanging, new(0f, 4.5f, 0f), Vector3.UnitZ)
        );

        Assert.Equal(ConstraintKind.Hinge, world.GetConstraintKind(joint));
        Assert.Equal(joint, Assert.Single(world.ConstraintHandles.ToArray()));

        world.GetConstraintAnchors(joint, out var first, out var second);
        Assert.Equal(new Vector3(0f, 4.5f, 0f), first);
        Assert.Equal(new Vector3(0f, 4.5f, 0f), second);

        // A hinge about Z keeps its axis pointing along Z, and the anchors stay together while the
        // arm swings around them.
        Assert.Equal(1f, world.GetConstraintAxis(joint).Z, 3);

        world.ApplyImpulse(hanging, new(5f, 0f, 0f));

        for (var step = 0; step < 120; step++) {
            world.Step(Step);
        }

        world.GetConstraintAnchors(joint, out var movedFirst, out var movedSecond);
        Assert.True((movedFirst - movedSecond).Length() < 0.05f);
        Assert.NotEqual(new Vector3(0f, 4f, 0f), world.GetPosition(hanging));
    }

    [Fact]
    public void DestroyingAConstraintTwiceIsHarmlessAndTheHandleGoesStale() {
        using var world = new PhysicsWorld();

        var first = world.CreateBody(BodyDescription.Static(world.Shapes.Sphere(0.25f), Vector3.Zero));

        var second = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.25f), new(0f, -1f, 0f)) with { Mass = 1f }
        );

        var joint = world.CreateConstraint(ConstraintDescription.Point(first, second, Vector3.Zero));

        world.DestroyConstraint(joint);
        Assert.False(world.IsAlive(joint));
        Assert.Equal(0, world.ConstraintCount);
        Assert.Empty(world.ConstraintHandles.ToArray());

        world.DestroyConstraint(joint);
        Assert.Throws<PhysicsHandleException>(() => world.SetConstraintEnabled(joint, true));
    }
}
