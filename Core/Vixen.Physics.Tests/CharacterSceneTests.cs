// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Frames;
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

    /// <summary>
    ///     ⚠ Writing a character's transform used to do nothing: its position lives in Jolt and the
    ///     bridge only ever wrote out of it, so a teleport, a checkpoint load and a spawn point were
    ///     all overwritten on the same step. It is also the prerequisite for prediction — a rollback
    ///     restores the transform and replays from it.
    /// </summary>
    [Fact]
    public void WritingTheTransformMovesTheCharacter() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, new(0f, 0.1f, 0f));
        Advance(scene, 30);

        entities.Get<LocalTransform>(walker).Position = new(12f, 0.1f, -8f);
        Advance(scene, 1);

        var position = entities.Read<LocalTransform>(walker).Position;

        Assert.Equal(12f, position.X, 2);
        Assert.Equal(-8f, position.Z, 2);
        Assert.True(scene.TryGetCharacter(walker, out var controller));
        Assert.Equal(12f, controller!.Position.X, 2);
    }

    /// <summary>
    ///     And a step that moved nothing does not read as a teleport. The writeback subtracts the
    ///     shape offset and the adopt adds it back, so exact equality would snap on the rounding.
    /// </summary>
    [Fact]
    public void AStillCharacterIsNotTeleportedByItsOwnWriteback() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);
        var walker = Walker(scene, new(3.7f, 0.1f, -2.3f));
        Advance(scene, 60);

        var settled = entities.Read<LocalTransform>(walker).Position;
        Advance(scene, 60);

        Assert.Equal(settled.X, entities.Read<LocalTransform>(walker).Position.X, 5);
        Assert.Equal(settled.Z, entities.Read<LocalTransform>(walker).Position.Z, 5);
    }

    /// <summary>One scripted walk through a whole frame loop, with the smoothing on or off.</summary>
    /// <param name="interpolated">Whether the walker carries a <c>PhysicsInterpolation</c>.</param>
    /// <param name="frames">How many frames to run. Each is exactly one fixed step.</param>
    /// <param name="adoptions">How many times the bridge took the transform as a teleport.</param>
    /// <returns>How far the character's <i>simulated</i> pose travelled, in the ground plane.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A whole <c>EngineLoop</c> and not <c>Advance</c>, because the defect this measures
    ///         is not in the fixed step at all.</b> <c>PhysicsInterpolationSystem</c> runs in
    ///         <c>LateUpdate</c>, so calling the four physics passes by hand — which is what every
    ///         other test in this file does — cannot see it. That is why a bug worth 50% of every
    ///         character's speed survived a suite this size.
    ///     </para>
    ///     <para>
    ///         The frame delta is exactly one step, which leaves <c>alpha</c> at zero and is the worst
    ///         case rather than a contrived one: it is every machine holding its refresh rate and
    ///         every <c>--vixen-fixed-step</c> capture.
    ///     </para>
    ///     <para>
    ///         The controller's own position, not the entity's. What the smoothing writes onto the
    ///         transform is up to a step behind by design, so comparing transforms would measure the
    ///         interpolation's lag and call it a difference in speed.
    ///     </para>
    /// </remarks>
    static float Walked(bool interpolated, int frames, out long adoptions) {
        using var loop = new EngineLoop();
        using var scene = new PhysicsScene(loop.World);

        loop.AddPhysics(scene);
        Ground(scene);

        var start = new Vector3(0f, 0f, 0f);
        var walker = Walker(scene, start);

        // Forward, at full throttle, held for the whole run. Nothing clears it: no PlayerInputSystem
        // is registered, so the intent is a constant and the walk is a function of the step alone.
        loop.World.Set(walker, new MoveIntent { Move = new(0f, 1f) });

        if (interpolated) {
            // Seeded at the spawn pose. A zeroed one drags the entity to the origin, which this file's
            // sibling in PhysicsSceneTests and the sample's own remarks both warn about.
            loop.World.Add(
                walker,
                new PhysicsInterpolation {
                    PreviousPosition = start,
                    CurrentPosition = start,
                    PreviousRotation = Quaternion.Identity,
                    CurrentRotation = Quaternion.Identity,
                    DrawnPosition = start
                }
            );
        }

        for (var frame = 0; frame < frames; frame++) {
            loop.Frame(TimeSpan.FromSeconds(Step));
        }

        Assert.True(scene.TryGetCharacter(walker, out var controller));

        adoptions = scene.CharacterAdoptionCount;

        var travelled = controller!.Position - (start + CharacterMovement.Default.ShapeOffset);

        return MathF.Sqrt((travelled.X * travelled.X) + (travelled.Z * travelled.Z));
    }

    /// <summary>
    ///     ⚠ <b>A component that only asks for smoothing must not change how fast the character
    ///     walks, and for as long as this repository has existed it halved it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>PhysicsInterpolationSystem</c> writes <c>LocalTransform</c> to the pose at
    ///         <c>alpha</c>, which on a frame one fixed step long is the <i>previous</i> step's; the
    ///         next <c>StepCharacters</c> read that as a teleport and pulled the controller back to
    ///         it. Every other step was therefore undone, and the arithmetic gives exactly half.
    ///     </para>
    ///     <para>
    ///         Measured rather than reasoned about, because nothing in this repository could measure a
    ///         moving character until a scripted walk existed: sample 13 covered 1.819 m of a scripted
    ///         first second with the component and 3.821 m without it, on the same route.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SmoothingACharacterDoesNotChangeHowFarItWalks() {
        const int Frames = 120;

        var smoothed = Walked(true, Frames, out var adoptions);
        var plain = Walked(false, Frames, out _);

        Assert.True(
            MathF.Abs(smoothed - plain) < 1e-3f,
            $"A character with PhysicsInterpolation walked {smoothed} m in {Frames} frames "
            + $"and one without it walked {plain} m — a ratio of {smoothed / plain:0.0000}. "
            + "The smoothing is writing LocalTransform and PhysicsScene.Adopt is taking it as a "
            + "teleport, so every other step is being undone."
        );

        // And the mechanism itself, not only its consequence. A walking character is teleported by
        // nobody; a count that climbs with the frames is the two writers fighting.
        Assert.Equal(0L, adoptions);
    }

    /// <summary>
    ///     And the thing the adopt exists for still works on a smoothed character: a written transform
    ///     is a teleport, which is what a respawn, a checkpoint and a rollback all are.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the half the fix could have broken.</b> Teaching the bridge to ignore an
    ///     interpolated pose is one step away from teaching it to ignore a correction, and a client
    ///     whose corrections stopped reaching the controller would replay from the guess it was
    ///     correcting for ever. <c>PredictedPlayerMovement</c>'s own tests are the other guard.
    /// </remarks>
    [Fact]
    public void ASmoothedCharacterIsStillTeleportedByAWrittenTransform() {
        using var loop = new EngineLoop();
        using var scene = new PhysicsScene(loop.World);

        loop.AddPhysics(scene);
        Ground(scene);

        var walker = Walker(scene, Vector3.Zero);

        loop.World.Set(walker, new MoveIntent { Move = new(0f, 1f) });
        loop.World.Add(walker, new PhysicsInterpolation { DrawnPosition = Vector3.Zero });

        for (var frame = 0; frame < 30; frame++) {
            loop.Frame(TimeSpan.FromSeconds(Step));
        }

        var before = scene.CharacterAdoptionCount;

        // The stick released and the velocity cleared by hand, which is exactly what a respawn does
        // and for the reason its own remarks give: the controller keeps the walk it was doing and
        // would otherwise arrive at the spawn point already moving. Without both, the assertion below
        // measures one more step of walking rather than where the write put the character.
        loop.World.Set(walker, default(MoveIntent));
        loop.World.Get<CharacterState>(walker).Velocity = Vector3.Zero;
        loop.World.Get<LocalTransform>(walker).Position = new(12f, 0.1f, -8f);
        loop.Frame(TimeSpan.FromSeconds(Step));

        Assert.True(scene.TryGetCharacter(walker, out var controller));
        Assert.Equal(12f, controller!.Position.X, 1);
        Assert.Equal(-8f, controller.Position.Z, 1);
        Assert.Equal(before + 1, scene.CharacterAdoptionCount);
    }

    /// <summary>
    ///     ⚠ <b>And the teleport has to be where it was put on the frame it was drawn, not on the one
    ///     after.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The sibling above asserts the <i>controller</i> moved, which is the simulation's half.
    ///         This asserts the half a player sees: <c>PhysicsInterpolationSystem</c> draws between the
    ///         last two poses, and an adopted transform makes those two the two ends of the jump — so a
    ///         respawning character was drawn sliding back across the level it had just left, and on a
    ///         frame one fixed step long it was drawn at the old spot outright.
    ///     </para>
    ///     <para>
    ///         The signal here is not a tag but the adopt itself, which is already provenance rather
    ///         than geometry: <see cref="PhysicsInterpolation.DrawnPosition" /> is what proves the
    ///         transform was written by somebody other than the smoothing, and that is exactly the
    ///         question "was this a teleport" asks. A walking character is adopted zero times, which
    ///         <see cref="SmoothingACharacterDoesNotChangeHowFarItWalks" /> pins.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ATeleportedCharacterIsDrawnWhereItWasPutRatherThanSlidingToIt() {
        using var loop = new EngineLoop();
        using var scene = new PhysicsScene(loop.World);

        loop.AddPhysics(scene);
        Ground(scene);

        var walker = Walker(scene, Vector3.Zero);

        loop.World.Set(walker, new MoveIntent { Move = new(0f, 1f) });
        loop.World.Add(walker, new PhysicsInterpolation { DrawnPosition = Vector3.Zero });

        for (var frame = 0; frame < 30; frame++) {
            loop.Frame(TimeSpan.FromSeconds(Step));
        }

        // Walked somewhere first, so the pose it would slide back to is a real one.
        var walked = loop.World.Read<LocalTransform>(walker).Position;
        Assert.True(MathF.Abs(walked.Z) > 1f, $"The walker only reached {walked}, so there is nothing to slide back to.");

        // The stick released and the velocity cleared by hand, exactly as the sibling test does and
        // for the same reason: otherwise this measures one more step of walking.
        loop.World.Set(walker, default(MoveIntent));
        loop.World.Get<CharacterState>(walker).Velocity = Vector3.Zero;
        loop.World.Get<LocalTransform>(walker).Position = new(12f, 0.1f, -8f);

        loop.Frame(TimeSpan.FromSeconds(Step));

        var drawn = loop.World.Read<LocalTransform>(walker).Position;

        Assert.Equal(0f, loop.FixedStep.Alpha, 3);
        Assert.Equal(12f, drawn.X, 1);
        Assert.Equal(-8f, drawn.Z, 1);

        // Half a step: no simulation, alpha in the middle of whatever segment is left.
        loop.Frame(TimeSpan.FromSeconds(Step / 2f));

        var midway = loop.World.Read<LocalTransform>(walker).Position;

        Assert.InRange(loop.FixedStep.Alpha, 0.4f, 0.6f);
        Assert.Equal(12f, midway.X, 1);
        Assert.Equal(-8f, midway.Z, 1);
    }

    /// <summary>
    ///     ⚠ <b><c>PhysicsTeleport</c> on a character was inert, so it stayed on for ever.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only <c>PhysicsScene.PushAuthoredState</c> read the tag, and it walks
    ///         <c>WithAll&lt;PhysicsBody, LocalTransform&gt;</c> — a character has a
    ///         <c>CharacterBody</c> instead. Nothing crashed, which is why it lasted: the character
    ///         was adopted anyway, because writing its transform <i>is</i> the teleport. But the tag
    ///         was never taken off, and every consumer that reads it as an event rather than a state
    ///         then fires on every tick for ever.
    ///     </para>
    ///     <para>
    ///         The other half is what the tag now buys: a teleport that lands exactly on the pose the
    ///         smoothing last drew is indistinguishable from the smoothing's own write, and
    ///         <c>Adopt</c> refuses it on that evidence. A rollback that lands on its own guess and a
    ///         respawn at the spot you died are both that case.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APhysicsTeleportOnACharacterIsObeyedAndThenTakenOff() {
        using var entities = new EcsWorld("Test");
        using var scene = new PhysicsScene(entities);

        Ground(scene);

        var walker = Walker(scene, Vector3.Zero);
        Advance(scene, 2);

        var elsewhere = new Vector3(12f, 0.1f, -8f);

        // The write and the receipt agree, which is exactly the shape of the smoothing's own write.
        entities.Add(walker, new PhysicsInterpolation { DrawnPosition = elsewhere });
        entities.Get<LocalTransform>(walker).Position = elsewhere;

        var refused = scene.CharacterAdoptionCount;
        Advance(scene, 1);

        Assert.Equal(refused, scene.CharacterAdoptionCount);
        Assert.True(scene.TryGetCharacter(walker, out var held));
        Assert.True(MathF.Abs(held!.Position.X) < 1f, $"Nothing said teleport, yet the controller moved to {held.Position}.");

        // The same write, now with the caller saying so out loud.
        entities.Get<PhysicsInterpolation>(walker).DrawnPosition = elsewhere;
        entities.Get<LocalTransform>(walker).Position = elsewhere;
        entities.Add<PhysicsTeleport>(walker);

        Advance(scene, 1);

        Assert.Equal(refused + 1, scene.CharacterAdoptionCount);
        Assert.True(scene.TryGetCharacter(walker, out var moved));
        Assert.Equal(12f, moved!.Position.X, 1);
        Assert.Equal(-8f, moved.Position.Z, 1);

        // And it is gone rather than left to fire again on the next tick, and the one after that.
        Assert.False(entities.Has<PhysicsTeleport>(walker));
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
