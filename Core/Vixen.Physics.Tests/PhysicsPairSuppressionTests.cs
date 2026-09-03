// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Constraints;
using Xunit;

namespace Vixen.Physics.Tests;

/// <summary>
///     Per-pair collision suppression: two named bodies that do not collide with one another and still
///     collide with everything else.
/// </summary>
/// <remarks>
///     ⚠ <b>The oracle here is a distance, not a flag.</b> Every one of these could be made to pass by
///     a class that recorded the request and never told Jolt — which is the failure this binding has
///     already shipped once (<c>BodyCreationSettings.MotionQuality</c>). So the question every test
///     asks is whether two overlapping bodies were pushed apart by the solver, and the separation is
///     closed-form: two spheres of radius 0.5 that touch are one metre apart, and two that were never
///     tested against each other are wherever they were put.
/// </remarks>
public sealed class PhysicsPairSuppressionTests {
    const float Step = 1f / 60f;

    /// <summary>Gravity off, so the only thing that can move a body is the contact under test.</summary>
    static PhysicsWorld Weightless() => new(new() { Gravity = Vector3.Zero });

    /// <summary>Two unit spheres 10 cm apart — deeply overlapping, and free to be pushed apart.</summary>
    static (BodyHandle First, BodyHandle Second) OverlappingPair(PhysicsWorld world) {
        var shape = world.Shapes.Sphere(0.5f);

        var first = world.CreateBody(
            BodyDescription.Dynamic(shape, new(-0.05f, 0f, 0f)) with { Mass = 1f, AllowSleeping = false }
        );

        var second = world.CreateBody(
            BodyDescription.Dynamic(shape, new(0.05f, 0f, 0f)) with { Mass = 1f, AllowSleeping = false }
        );

        return (first, second);
    }

    static float Separation(PhysicsWorld world, BodyHandle first, BodyHandle second) =>
        (world.GetPosition(first) - world.GetPosition(second)).Length();

    static void Advance(PhysicsWorld world, int steps) {
        for (var step = 0; step < steps; step++) {
            world.Step(Step);
        }
    }

    /// <summary>The control. Without suppression the solver pushes the two apart until they touch.</summary>
    /// <remarks>
    ///     Here so that the assertion below has a measured other half rather than a remembered one.
    ///     If Jolt ever stopped separating overlapping spheres this test would say so, and every
    ///     suppression test in the file would otherwise have quietly become a tautology.
    /// </remarks>
    [Fact]
    public void TwoOverlappingBodiesArePushedApartWhenNothingSuppressesThem() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        Assert.True(world.CanBodiesCollide(first, second));
        Assert.Equal(0, world.GroupedBodyCount);

        Advance(world, 120);

        Assert.InRange(Separation(world, first, second), 0.9f, 1.1f);
    }

    [Fact]
    public void ASuppressedPairIsLeftOverlappingBecauseItIsNeverTested() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        world.SetPairCollision(first, second, false);

        Assert.False(world.CanBodiesCollide(first, second));
        Assert.Equal(2, world.GroupedBodyCount);

        Advance(world, 120);

        Assert.Equal(0.1f, Separation(world, first, second), 2);
    }

    /// <summary>
    ///     The half that makes it a <i>pair</i> suppression rather than a layer: both bodies still hit
    ///     everything else.
    /// </summary>
    [Fact]
    public void ASuppressedBodyStillCollidesWithEverythingElse() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        world.SetPairCollision(first, second, false);

        // A third sphere overlapping both, in no group at all.
        var third = world.CreateBody(
            BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), new(0f, 0.1f, 0f))
                with { Mass = 1f, AllowSleeping = false }
        );

        Assert.True(world.CanBodiesCollide(first, third));
        Assert.True(world.CanBodiesCollide(second, third));

        Advance(world, 120);

        Assert.InRange(Separation(world, first, third), 0.9f, 1.1f);
        Assert.InRange(Separation(world, second, third), 0.9f, 1.1f);
    }

    [Fact]
    public void ASuppressionCanBeTakenBackAndTheBodiesSeparateAgain() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        world.SetPairCollision(first, second, false);
        Advance(world, 60);
        Assert.Equal(0.1f, Separation(world, first, second), 2);

        world.SetPairCollision(first, second, true);
        Assert.True(world.CanBodiesCollide(first, second));

        Advance(world, 120);

        Assert.InRange(Separation(world, first, second), 0.9f, 1.1f);
    }

    /// <summary>A joint asking for it is the case the README named, and it is the same mechanism.</summary>
    [Fact]
    public void AConstraintCanSuppressTheCollisionBetweenTheTwoBodiesItHolds() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        world.CreateConstraint(
            ConstraintDescription.Point(first, second, Vector3.Zero) with { SuppressPairCollision = true }
        );

        Assert.False(world.CanBodiesCollide(first, second));

        Advance(world, 120);

        Assert.Equal(0.1f, Separation(world, first, second), 2);
    }

    [Fact]
    public void AConstraintThatDoesNotAskForItLeavesTheBodiesColliding() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        world.CreateConstraint(ConstraintDescription.Point(first, second, Vector3.Zero));

        Assert.True(world.CanBodiesCollide(first, second));
        Assert.Equal(0, world.GroupedBodyCount);
    }

    /// <summary>
    ///     ⚠ Two joints over one pair are ordinary — a hinge and a distance limiter on the same door,
    ///     a cone and a twist on the same shoulder — so destroying one must not let the pair collide
    ///     while the other is still holding it.
    /// </summary>
    [Fact]
    public void OneOfTwoSuppressingConstraintsGoingAwayDoesNotRestoreTheCollision() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        var one = world.CreateConstraint(
            ConstraintDescription.Point(first, second, Vector3.Zero) with { SuppressPairCollision = true }
        );

        world.CreateConstraint(
            ConstraintDescription.Point(first, second, Vector3.Zero) with { SuppressPairCollision = true }
        );

        world.DestroyConstraint(one);

        Assert.False(world.CanBodiesCollide(first, second));

        Advance(world, 120);

        Assert.Equal(0.1f, Separation(world, first, second), 2);
    }

    /// <summary>And the last one going away hands the pair back.</summary>
    [Fact]
    public void DestroyingTheLastSuppressingConstraintRestoresTheCollision() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        var joint = world.CreateConstraint(
            ConstraintDescription.Point(first, second, Vector3.Zero) with { SuppressPairCollision = true }
        );

        world.DestroyConstraint(joint);

        Assert.True(world.CanBodiesCollide(first, second));

        Advance(world, 120);

        Assert.InRange(Separation(world, first, second), 0.9f, 1.1f);
    }

    /// <summary>
    ///     ⚠ A caller's suppression outlives every joint over the same pair, and a joint's outlives a
    ///     caller's re-enable. Either source alone keeps the pair apart, which is the only arrangement
    ///     where neither can silently undo the other.
    /// </summary>
    [Fact]
    public void AConstraintAndACallerSuppressTheSamePairIndependently() {
        using var world = Weightless();
        var (first, second) = OverlappingPair(world);

        world.SetPairCollision(first, second, false);

        var joint = world.CreateConstraint(
            ConstraintDescription.Point(first, second, Vector3.Zero) with { SuppressPairCollision = true }
        );

        // The joint goes; the caller's word still stands.
        world.DestroyConstraint(joint);
        Assert.False(world.CanBodiesCollide(first, second));

        // The caller takes it back with nothing else holding it, and only now does it collide.
        world.SetPairCollision(first, second, true);
        Assert.True(world.CanBodiesCollide(first, second));
    }

    /// <summary>
    ///     Growth: the table's size is fixed at construction, so passing its capacity rebuilds it —
    ///     and every suppression made before the rebuild has to survive it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Forty-two members against a first capacity of sixteen, so the table is rebuilt twice.
    ///     </para>
    ///     <para>
    ///         The pair asserted on spans both rebuilds: its first body joined the group before the
    ///         first and its second after the second.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the re-pointing half is asserted through <c>StaleFilterCount</c> rather than
    ///         through a distance, because a distance cannot see it.</b> This test was written first
    ///         with only the behavioural assertion and it <b>passed against a build that never
    ///         re-pointed a single body</b> — which makes the test the defect, not the code. It passes
    ///         because a body still naming the old, smaller table makes Jolt index a bitmask past its
    ///         end, and the bit it read there happened to say "disabled", which is the answer the
    ///         assertion wanted. Reading each body's group back out of Jolt is the only form of this
    ///         that cannot pass by luck.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SuppressionsSurviveTheFilterTableOutgrowingItself() {
        using var world = Weightless();
        var shape = world.Shapes.Sphere(0.5f);

        // Joins the group first, so it is a member of the smallest table there ever was.
        var early = world.CreateBody(
            BodyDescription.Dynamic(shape, new(-0.05f, 0f, 0f)) with { Mass = 1f, AllowSleeping = false }
        );

        var filler = new BodyHandle[40];

        for (var index = 0; index < filler.Length; index++) {
            filler[index] = world.CreateBody(
                BodyDescription.Dynamic(shape, new(20f + (index * 4f), 0f, 0f)) with { Mass = 1f }
            );
        }

        world.SetPairCollision(early, filler[0], false);

        for (var index = 1; index + 1 < filler.Length; index += 2) {
            world.SetPairCollision(filler[index], filler[index + 1], false);
        }

        // Sixteen, thirty-two, sixty-four: two rebuilds behind us.
        Assert.Equal(40, world.GroupedBodyCount);
        Assert.False(world.CanBodiesCollide(early, filler[0]));
        Assert.True(world.CanBodiesCollide(filler[2], filler[3]));

        var late = world.CreateBody(
            BodyDescription.Dynamic(shape, new(0.05f, 0f, 0f)) with { Mass = 1f, AllowSleeping = false }
        );

        world.SetPairCollision(early, late, false);

        Assert.Equal(41, world.GroupedBodyCount);
        Assert.False(world.CanBodiesCollide(early, late));

        // The invariant a distance cannot see: nobody is left naming a dead table.
        Assert.Equal(0, world.StaleFilterCount());

        Advance(world, 120);

        Assert.Equal(0.1f, Separation(world, early, late), 2);
    }

    /// <summary>
    ///     ⚠ A body reusing a destroyed body's index does not inherit its group membership. Jolt
    ///     recycles body indices, and a slot left holding the old body's sub-group would make the new
    ///     one pass through whatever the old one was suppressed against — with nothing anywhere
    ///     naming it.
    /// </summary>
    [Fact]
    public void ABodyThatTakesADestroyedBodysIndexIsInNoGroup() {
        using var world = Weightless();
        var shape = world.Shapes.Sphere(0.5f);

        var keeper = world.CreateBody(BodyDescription.Dynamic(shape, Vector3.Zero) with { Mass = 1f });
        var doomed = world.CreateBody(BodyDescription.Dynamic(shape, new(0.1f, 0f, 0f)) with { Mass = 1f });

        world.SetPairCollision(keeper, doomed, false);
        world.DestroyBody(doomed);

        var replacement = world.CreateBody(
            BodyDescription.Dynamic(shape, new(0.1f, 0f, 0f)) with { Mass = 1f, AllowSleeping = false }
        );

        Assert.True(world.CanBodiesCollide(keeper, replacement));

        Advance(world, 120);

        Assert.InRange(Separation(world, keeper, replacement), 0.9f, 1.1f);
    }

    [Fact]
    public void ABodyCannotBeToldWhetherItCollidesWithItself() {
        using var world = Weightless();
        var body = world.CreateBody(BodyDescription.Dynamic(world.Shapes.Sphere(0.5f), Vector3.Zero));

        Assert.Throws<PhysicsHandleException>(() => world.SetPairCollision(body, body, false));
    }

    [Fact]
    public void AStaleHandleIsRefusedRatherThanSilentlySuppressingNothing() {
        using var world = Weightless();
        var shape = world.Shapes.Sphere(0.5f);

        var alive = world.CreateBody(BodyDescription.Dynamic(shape, Vector3.Zero));
        var dead = world.CreateBody(BodyDescription.Dynamic(shape, new(2f, 0f, 0f)));

        world.DestroyBody(dead);

        Assert.Throws<PhysicsHandleException>(() => world.SetPairCollision(alive, dead, false));
        Assert.Throws<PhysicsHandleException>(() => world.CanBodiesCollide(alive, dead));
    }
}
