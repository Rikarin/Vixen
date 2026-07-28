// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Characters;
using Xunit;

namespace Vixen.Physics.Tests;

public sealed class CharacterControllerTests {
    const float Step = 1f / 60f;

    static PhysicsWorld WorldWithFloor(out CharacterControllerSettings settings, float floorTop = 0f) {
        var world = new PhysicsWorld();

        world.CreateBody(
            BodyDescription.Static(
                world.Shapes.Box(new Vector3(50f, 1f, 50f)),
                new(0f, floorTop - 1f, 0f)
            )
        );

        settings = new() { Shape = world.Shapes.Capsule(0.6f, 0.3f), Position = new(0f, floorTop + 2f, 0f) };
        return world;
    }

    /// <summary>
    ///     Gravity is the caller's — see <see cref="CharacterController" />. This is the loop every
    ///     game writes around it, and the tests share it so they are testing the controller rather
    ///     than four slightly different loops.
    /// </summary>
    static void Advance(PhysicsWorld world, CharacterController character, int steps, Vector3 horizontal) {
        for (var step = 0; step < steps; step++) {
            var velocity = character.Velocity;

            velocity = character.IsGrounded
                ? new(horizontal.X, MathF.Max(velocity.Y, 0f), horizontal.Z)
                : new(horizontal.X, velocity.Y + (world.Gravity.Y * Step), horizontal.Z);

            character.Velocity = velocity;
            world.Step(Step);
            character.Update(Step);
        }
    }

    [Fact]
    public void ACharacterFallsUntilItIsStandingOnSomething() {
        using var world = WorldWithFloor(out var settings);
        using var character = world.CreateCharacter(settings);

        Assert.Equal(CharacterGround.Airborne, character.Ground);

        Advance(world, character, 120, Vector3.Zero);

        Assert.True(character.IsGrounded);
        Assert.Equal(CharacterGround.Grounded, character.Ground);

        // The capsule's origin is its centre, so its bottom is halfHeight + radius below it.
        Assert.Equal(0.9f, character.Position.Y, 1);
        Assert.Equal(1f, character.GroundNormal.Y, 2);
    }

    [Fact]
    public void ACharacterWalksAcrossFlatGround() {
        using var world = WorldWithFloor(out var settings);
        using var character = world.CreateCharacter(settings);

        Advance(world, character, 60, Vector3.Zero);
        var start = character.Position.X;

        Advance(world, character, 60, new(3f, 0f, 0f));

        Assert.InRange(character.Position.X - start, 2.5f, 3.5f);
    }

    [Fact]
    public void ACharacterIsStoppedByAWallRatherThanPassingThroughIt() {
        using var world = WorldWithFloor(out var settings);

        world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(new Vector3(0.5f, 3f, 10f)), new(3f, 2f, 0f))
        );

        using var character = world.CreateCharacter(settings);

        Advance(world, character, 60, Vector3.Zero);
        Advance(world, character, 120, new(5f, 0f, 0f));

        // Stopped at the wall's near face, less the capsule's radius and Jolt's own padding.
        Assert.InRange(character.Position.X, 1.9f, 2.5f);
    }

    [Fact]
    public void ACharacterWalksUpAStepWithoutJumping() {
        using var world = WorldWithFloor(out var settings);

        world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(new Vector3(2f, 0.15f, 2f)), new(3f, 0.15f, 0f))
        );

        using var character = world.CreateCharacter(settings);

        Advance(world, character, 60, Vector3.Zero);

        // Two metres a second for two seconds, onto a step whose far edge is at x = 5. Walking for
        // much longer would take the character off the other end and back down, which is a different
        // — and equally correct — thing to assert.
        Advance(world, character, 120, new(2f, 0f, 0f));

        Assert.True(character.IsGrounded);
        Assert.True(character.Position.X > 2.5f);
        Assert.InRange(character.Position.Y, 1.1f, 1.4f);
    }

    [Fact]
    public void ACharacterCanJumpAndComeDown() {
        using var world = WorldWithFloor(out var settings);
        using var character = world.CreateCharacter(settings);

        Advance(world, character, 60, Vector3.Zero);
        var ground = character.Position.Y;

        character.Velocity = new(0f, 6f, 0f);
        Advance(world, character, 20, Vector3.Zero);

        Assert.False(character.IsGrounded);
        Assert.True(character.Position.Y > ground + 0.5f);

        Advance(world, character, 180, Vector3.Zero);

        Assert.True(character.IsGrounded);
        Assert.Equal(ground, character.Position.Y, 1);
    }

    [Fact]
    public void ACharacterCanOnlyStandUpWhereThereIsRoom() {
        using var world = WorldWithFloor(out var settings);

        var standing = world.Shapes.Capsule(0.6f, 0.3f);
        var crouched = world.Shapes.Capsule(0.2f, 0.3f);

        // A ceiling low enough for the crouched capsule and not the standing one.
        world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(new Vector3(5f, 0.1f, 5f)), new(0f, 1.2f, 0f))
        );

        using var character = world.CreateCharacter(settings with { Shape = crouched, Position = new(0f, 0.5f, 0f) });

        Advance(world, character, 60, Vector3.Zero);

        Assert.False(character.TrySetShape(standing));
        Assert.True(character.TrySetShape(crouched));
    }

    [Fact]
    public void ACharacterIsForgottenByItsWorldWhenItIsDisposed() {
        using var world = WorldWithFloor(out var settings);

        var character = world.CreateCharacter(settings);
        Assert.Equal(1, world.CharacterCount);

        character.Dispose();
        Assert.Equal(0, world.CharacterCount);
        Assert.True(character.IsDisposed);

        character.Dispose();
        Assert.Throws<ObjectDisposedException>(() => character.Update(Step));
    }

    [Fact]
    public void ACharacterWithoutAShapeIsRefused() {
        using var world = new PhysicsWorld();
        Assert.Throws<PhysicsShapeException>(() => world.CreateCharacter(new()));
    }
}
