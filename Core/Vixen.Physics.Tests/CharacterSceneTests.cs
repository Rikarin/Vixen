// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Physics.Bodies;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Vixen.Physics.Tests;

/// <summary>The bridge, against a real simulation: creation, the sweep, the shape swap and the writeback.</summary>
public sealed class CharacterSceneTests {
    const float Step = 1f / 60f;

    static void Advance(PhysicsScene scene, int steps) {
        for (var step = 0; step < steps; step++) {
            scene.Synchronize(Step);
            scene.Step(Step);
            scene.StepCharacters(Step);
            scene.Writeback();
        }
    }

    static void Ground(PhysicsScene scene, float top = 0f) {
        var entity = scene.Entities.Create(LocalTransform.At(new(0f, top - 1f, 0f)));
        scene.Entities.Add(entity, Collider.Of(scene.Shapes.Box(new Vector3(50f, 1f, 50f))));
    }

    /// <summary>The described human: 1.8 m in a 0.3 m capsule, crouching to half of it.</summary>
    static CharacterMovement Human(PhysicsScene scene) => CharacterMovement.Default with {
        Shape = scene.Shapes.Capsule(0.6f, 0.3f),
        CrouchShape = scene.Shapes.Capsule(0.3f, 0.3f)
    };

    static Entity Walker(PhysicsScene scene, Vector3 feet, CharacterMovement? movement = null) {
        var entity = scene.Entities.Create(LocalTransform.At(feet));
        scene.Entities.Add(entity, movement ?? Human(scene));
        scene.Entities.Add(entity, default(MoveIntent));
        return entity;
    }

    [Fact]
    public void ACharacterMovementComponentGetsAControllerAndAState() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, new(0f, 0f, 0f));

        Assert.Equal(0, scene.CharacterCount);

        Advance(scene, 1);

        Assert.Equal(1, scene.CharacterCount);
        Assert.True(entities.Has<CharacterState>(walker));
        Assert.True(scene.TryGetCharacter(walker, out var controller));
        Assert.NotNull(controller);
    }

    /// <summary>
    ///     A shape names a volume only a live <c>PhysicsShapes</c> can issue, so
    ///     <c>CharacterMovement.Default</c> cannot carry one. An entity that reached the bridge without
    ///     one is retried rather than thrown at, so filling it in later works.
    /// </summary>
    [Fact]
    public void ACharacterWithNoShapeIsSkippedAndPickedUpLater() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, Vector3.Zero, CharacterMovement.Default);

        Advance(scene, 3);
        Assert.Equal(0, scene.CharacterCount);

        entities.Get<CharacterMovement>(walker).Shape = scene.Shapes.Capsule(0.6f, 0.3f);
        Advance(scene, 1);

        Assert.Equal(1, scene.CharacterCount);
    }

    [Fact]
    public void RemovingTheComponentDestroysTheController() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, Vector3.Zero);
        Advance(scene, 1);

        entities.Remove<CharacterMovement>(walker);
        Advance(scene, 1);

        Assert.Equal(0, scene.CharacterCount);
        Assert.False(entities.Has<CharacterBody>(walker));
    }

    /// <summary>
    ///     The entity's origin is at the character's feet, not at its capsule's centre — which is what
    ///     <c>CharacterMovement.ShapeOffset</c> exists for, and what makes a dropped character land
    ///     standing on the floor rather than buried to the waist.
    /// </summary>
    [Fact]
    public void ACharacterFallsAndLandsWithItsOriginAtItsFeet() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, new(0f, 3f, 0f));

        Advance(scene, 180);

        Assert.True(entities.Read<CharacterState>(walker).IsGrounded);
        Assert.Equal(CharacterMoveMode.Walking, entities.Read<CharacterState>(walker).Mode);
        Assert.Equal(0f, entities.Read<LocalTransform>(walker).Position.Y, 2);
    }

    [Fact]
    public void AWalkingCharacterGoesWhereItsIntentPoints() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, new(0f, 0.1f, 0f));

        Advance(scene, 30);
        entities.Set(walker, new MoveIntent { Move = new(0f, 1f) });
        Advance(scene, 60);

        // Forward at zero yaw is -Z, and a second of walking at 4 m/s covers most of four metres —
        // most, because the first frames are spent accelerating.
        var position = entities.Read<LocalTransform>(walker).Position;

        Assert.True(position.Z < -3f, $"only reached {position.Z}");
        Assert.Equal(0f, position.X, 3);
    }

    [Fact]
    public void AJumpingCharacterLeavesTheGroundAndComesBack() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, new(0f, 0.1f, 0f));

        Advance(scene, 30);
        Assert.True(entities.Read<CharacterState>(walker).IsGrounded);

        entities.Set(walker, new MoveIntent { Buttons = MoveButtons.Jump });
        Advance(scene, 10);

        Assert.False(entities.Read<CharacterState>(walker).IsGrounded);
        Assert.True(entities.Read<LocalTransform>(walker).Position.Y > 0.5f);

        entities.Set(walker, default(MoveIntent));
        Advance(scene, 120);

        Assert.True(entities.Read<CharacterState>(walker).IsGrounded);
        Assert.Equal(0f, entities.Read<LocalTransform>(walker).Position.Y, 2);
    }

    [Fact]
    public void CrouchingSwapsTheShapeAndStandingSwapsItBack() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, new(0f, 0.1f, 0f));
        Advance(scene, 30);

        var standing = entities.Read<CharacterBody>(walker).BuiltShape;

        entities.Set(walker, new MoveIntent { Buttons = MoveButtons.Crouch });
        Advance(scene, 5);

        Assert.True(entities.Read<CharacterState>(walker).IsCrouching);
        Assert.NotEqual(standing, entities.Read<CharacterBody>(walker).BuiltShape);

        entities.Set(walker, default(MoveIntent));
        Advance(scene, 5);

        Assert.False(entities.Read<CharacterState>(walker).IsCrouching);
        Assert.Equal(standing, entities.Read<CharacterBody>(walker).BuiltShape);
    }

    /// <summary>
    ///     Standing up under a low ceiling is refused, and the refusal <i>is</i> the behaviour: the
    ///     character stays crouched with no call site anywhere having to know a ceiling exists.
    /// </summary>
    [Fact]
    public void ACharacterUnderALowCeilingCannotStandUp() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);

        var walker = Walker(scene, new(0f, 0.1f, 0f));

        // Crouched first, on open ground — a character spawned already inside a slab is a test of
        // penetration recovery rather than of the ceiling check.
        entities.Set(walker, new MoveIntent { Buttons = MoveButtons.Crouch });
        Advance(scene, 30);
        Assert.True(entities.Read<CharacterState>(walker).IsCrouching);

        // Now a slab whose underside is at 1.0 m: over a crouched capsule (0.6 m tall) and under a
        // standing one (1.8 m).
        var ceiling = entities.Create(LocalTransform.At(new(0f, 1.5f, 0f)));
        entities.Add(ceiling, Collider.Of(scene.Shapes.Box(new Vector3(5f, 0.5f, 5f))));
        Advance(scene, 5);

        entities.Set(walker, default(MoveIntent));
        Advance(scene, 10);

        var state = entities.Read<CharacterState>(walker);

        Assert.True(state.IsCrouching, $"stood up under a ceiling; y = {entities.Read<LocalTransform>(walker).Position.Y}");
    }

    [Fact]
    public void AWallStopsTheCharacterRatherThanBankingItsSpeed() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);

        var wall = entities.Create(LocalTransform.At(new(0f, 1f, -2f)));
        entities.Add(wall, Collider.Of(scene.Shapes.Box(new Vector3(5f, 2f, 0.5f))));

        var walker = Walker(scene, new(0f, 0.1f, 0f));
        entities.Set(walker, new MoveIntent { Move = new(0f, 1f) });
        Advance(scene, 120);

        // Held against the wall, the stored velocity is what the sweep achieved rather than what was
        // asked for. Without the readback it would be the full walk speed, and releasing the wall
        // would fire the character forwards.
        var state = entities.Read<CharacterState>(walker);
        var position = entities.Read<LocalTransform>(walker).Position;

        Assert.True(MathF.Abs(state.Velocity.Z) < 0.5f, $"velocity {state.Velocity}, position {position}");
        Assert.True(position.Z > -2f, $"position {position}");
    }

    /// <summary>
    ///     Jumping into a ceiling stops the rise rather than banking it. <c>CharacterVirtual</c> leaves
    ///     the velocity it was given, so a character that trusted it would hold its full jump speed
    ///     against the slab and hang there until gravity had eaten all of it.
    /// </summary>
    [Fact]
    public void JumpingIntoACeilingStopsTheRise() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);

        // Underside at 2.2 m: clear of a standing capsule at rest, and squarely in the way of a jump.
        var ceiling = entities.Create(LocalTransform.At(new(0f, 2.7f, 0f)));
        entities.Add(ceiling, Collider.Of(scene.Shapes.Box(new Vector3(5f, 0.5f, 5f))));

        var walker = Walker(scene, new(0f, 0.1f, 0f));
        Advance(scene, 30);

        entities.Set(walker, new MoveIntent { Buttons = MoveButtons.Jump });
        Advance(scene, 20);

        var state = entities.Read<CharacterState>(walker);

        Assert.True(state.Velocity.Y <= 0f, $"still rising at {state.Velocity.Y}");
        Assert.True(entities.Read<LocalTransform>(walker).Position.Y < 0.5f);
    }

    [Fact]
    public void TurningWithAimWritesTheFacingAndNotTurningLeavesItAlone() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);

        var turner = Walker(scene, new(0f, 0.1f, 0f), Human(scene) with { TurnWithAim = true });
        var still = Walker(scene, new(3f, 0.1f, 0f));

        entities.Set(turner, new MoveIntent { Yaw = 1f });
        entities.Set(still, new MoveIntent { Yaw = 1f });
        Advance(scene, 5);

        var turned = entities.Read<LocalTransform>(turner).Rotation;

        Assert.Equal(MathF.Sin(0.5f), turned.Y, 3);
        Assert.Equal(Quaternion.Identity, entities.Read<LocalTransform>(still).Rotation);
    }

    [Fact]
    public void DisposingTheSceneDisposesEveryCharacter() {
        using var entities = new EcsWorld("Test");
        var scene = new PhysicsScene(entities);

        Ground(scene);
        Walker(scene, Vector3.Zero);
        Walker(scene, new(2f, 0f, 0f));
        Advance(scene, 1);

        Assert.Equal(2, scene.CharacterCount);

        scene.Dispose();

        Assert.Equal(0, scene.CharacterCount);
    }
}
