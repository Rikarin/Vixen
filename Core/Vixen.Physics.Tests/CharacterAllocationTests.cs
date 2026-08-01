// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Physics.Bodies;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;
using Vixen.Testing;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Vixen.Physics.Tests;

/// <summary>
///     The character bridge runs on every fixed step of every game that has a player in it, so what
///     it allocates per step is a number rather than a matter of taste.
/// </summary>
/// <remarks>
///     Measured over the bridge alone rather than over a whole <c>PhysicsScene</c> pass: the world
///     step is a native call whose managed cost is Jolt's binding to answer for, and folding it in
///     here would measure that instead of this.
/// </remarks>
public sealed class CharacterAllocationTests {
    const float Step = 1f / 60f;

    [Fact]
    public void SteppingCharactersAllocatesNothing() {
        using var entities = new EcsWorld("character-allocation");
        using var scene = new PhysicsScene(entities);

        var ground = entities.Create(LocalTransform.At(new(0f, -1f, 0f)));
        entities.Add(ground, Collider.Of(scene.Shapes.Box(new Vector3(50f, 1f, 50f))));

        for (var index = 0; index < 4; index++) {
            var walker = entities.Create(LocalTransform.At(new(index * 2f, 0.1f, 0f)));

            entities.Add(
                walker,
                CharacterMovement.Default with {
                    Shape = scene.Shapes.Capsule(0.6f, 0.3f),
                    CrouchShape = scene.Shapes.Capsule(0.3f, 0.3f)
                }
            );

            entities.Add(walker, new MoveIntent { Move = new(0f, 1f) });
        }

        // Settled first: creating the controllers is structural, and a structural change is not the
        // steady state this measures.
        for (var warm = 0; warm < 10; warm++) {
            scene.Synchronize(Step);
            scene.Step(Step);
            scene.StepCharacters(Step);
        }

        Assert.Equal(4, scene.CharacterCount);
        Assert.Equal(0, Measured.Bytes(() => scene.StepCharacters(Step), warmUp: 16, passes: 300));
    }

    /// <summary>
    ///     And the rule underneath it, which a rollback runs several times in one frame. A pure
    ///     function that allocated would make a reconciliation cost proportional to the round trip.
    /// </summary>
    [Fact]
    public void TheMotionRuleAllocatesNothing() {
        var settings = CharacterMovement.Default with { Shape = new(1), CrouchShape = new(2) };
        var state = default(CharacterState);
        var intent = new MoveIntent { Move = new(0.7f, 0.7f), Yaw = 1.1f, Buttons = MoveButtons.Sprint };

        Assert.Equal(
            0,
            Measured.Bytes(
                () => CharacterMotion.Step(settings, ref state, intent, CharacterGround.Grounded, Step),
                warmUp: 16,
                passes: 1_000
            )
        );
    }
}
