// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Net.Engine;
using Vixen.Net.Engine.Players;
using Vixen.Net.Prediction;
using Vixen.Physics.Ecs;

namespace Vixen.Net.Physics;

/// <summary>One tick of a player's movement, as the predictor replays it.</summary>
/// <remarks>
///     <para>
///         <b>This is the whole of P3's new simulation code, and it is deliberately thin.</b> It
///         writes the tick's input onto the pawn and steps the physics scene — which runs
///         <c>CharacterMotion</c>, the pure rule <c>Vixen.Physics</c> already had and already tested
///         with no Jolt at all. Prediction did not need a second movement implementation, and a
///         framework that made you write one would be a framework whose replay and whose live
///         simulation could disagree.
///     </para>
///     <para>
///         <b>Why it lives here and not in <c>Vixen.Net.Engine</c>.</b> It is the only type that has
///         to see both a <c>PhysicsScene</c> and a <c>Tick</c>, and neither <c>Vixen.Net</c> nor
///         <c>Vixen.Physics</c> may reference the other — the same reason
///         <c>Vixen.Net.Physics</c> exists at all, applied one layer up.
///     </para>
///     <para>
///         ⚠ <b>It requires <c>PhysicsScene</c> to adopt a written transform.</b> A rollback restores
///         the pawn's <c>LocalTransform</c> from the server's snapshot; a <c>CharacterController</c>'s
///         position lives in Jolt and is not restored by anything the ECS does. Without the adopt,
///         every replay would start from the guess it was correcting and the correction would never
///         converge — the failure <c>ClientPrediction</c>'s own remarks describe as "correcting the
///         present directly", arrived at from the other end.
///     </para>
/// </remarks>
/// <param name="scene">The physics bridge to step.</param>
/// <param name="fixedStep">How long one tick is, in seconds.</param>
public sealed class PredictedPlayerMovement(PhysicsScene scene, float fixedStep) {
    readonly PhysicsScene scene = scene ?? throw new ArgumentNullException(nameof(scene));

    readonly float fixedStep = fixedStep > 0f
        ? fixedStep
        : throw new ArgumentOutOfRangeException(nameof(fixedStep), fixedStep, "A tick has a length.");

    /// <summary>The player whose input this applies. Usually <c>LocalPlayerSystem.Controller</c>.</summary>
    /// <remarks>
    ///     <b>Setting this is enough</b> — the pawn is followed through the possession edge, so a
    ///     death, a respawn and a vehicle entry need nothing here. Two properties that had to be kept
    ///     in agreement is exactly the kind of wire a game forgets, and the failure is a client that
    ///     silently stops predicting after its first respawn.
    /// </remarks>
    public Entity Controller { get; set; } = Entity.Null;

    /// <summary>The pawn this client's input drives, or <see cref="Entity.Null" />.</summary>
    /// <remarks>
    ///     <para>
    ///         Set directly only by a game that drives a body without a <c>PlayerController</c>;
    ///         <see cref="Controller" /> is the ordinary way and overrides this when it is set.
    ///     </para>
    ///     <para>
    ///         Null is not an error. A client between bodies still ticks the world forward — the other
    ///         players are still moving — and simply has no input to apply.
    ///     </para>
    /// </remarks>
    public Entity Pawn { get; set; } = Entity.Null;

    /// <summary>What the tick will actually write its intent onto.</summary>
    /// <param name="world">The world.</param>
    /// <returns>The pawn, or <see cref="Entity.Null" />.</returns>
    /// <remarks>
    ///     A lookup per tick rather than a cached handle, which is two component reads and is what
    ///     buys the property above: there is no second thing to keep in step.
    /// </remarks>
    public Entity PawnIn(World world) {
        ArgumentNullException.ThrowIfNull(world);

        if (Controller.IsNull || !world.IsAlive(Controller)) {
            return Pawn;
        }

        var possessed = Player.PawnOf(world, Controller);

        return possessed.IsNull ? Pawn : possessed;
    }

    /// <summary>How many ticks have been simulated through this, replays included.</summary>
    public long StepCount { get; private set; }

    /// <summary>What copies the stepped transforms into the ones prediction compares.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the prediction is vacuously right.</b> Physics writes
    ///     <c>LocalTransform</c>; a snapshot and <c>PredictionHistory</c> both speak
    ///     <c>NetworkTransform</c>, and nothing carries one to the other inside a tick. A step that
    ///     skipped the publish would record the same bytes every tick, <c>Matches</c> would always
    ///     agree, and <c>MispredictionCount</c> would sit at zero while the client and the server
    ///     drifted apart — a green number measuring nothing.
    /// </remarks>
    public NetworkTransformCaptureSystem Transforms { get; } = new();

    /// <summary>The delegate <c>ClientPrediction</c> takes.</summary>
    /// <returns>The step.</returns>
    /// <remarks>
    ///     A method group rather than a lambda, so a stack trace inside a replayed tick names this
    ///     type rather than a display class.
    /// </remarks>
    public PredictedStep<PlayerMoveInput> AsStep() => Step;

    /// <summary>Advances the world by one tick with one player's input.</summary>
    /// <param name="world">The world to advance.</param>
    /// <param name="tick">The tick being simulated.</param>
    /// <param name="input">What the local player did on it.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The input is applied and never accumulated</b>, which is what makes the step a pure
    ///         function of the world and the input — the property
    ///         [16](../../../docs/plan/16-networking.md) requires and the one a replay of the same
    ///         tick twice depends on. Everything that <i>is</i> carried between ticks —
    ///         the velocity, the coyote window, the jump buffer — is in <c>CharacterState</c>, which
    ///         is world state and is therefore restored by the rollback along with everything else.
    ///     </para>
    ///     <para>
    ///         The four passes in order, because the order is the difference between a character
    ///         swept against this tick's world and one swept against the last: synchronize, step,
    ///         step the characters, write back. The same order <c>PhysicsSystems.AddPhysics</c>
    ///         registers, run directly because a replay has no frame loop.
    ///     </para>
    /// </remarks>
    public void Step(World world, Tick tick, in PlayerMoveInput input) {
        ArgumentNullException.ThrowIfNull(world);

        // ⚠ A replayed tick is a whole simulation phase, and a phase boundary is where the world's
        // change version moves on — SystemRunner does this and nothing else does. Without it every
        // write this tick is stamped with the same version as the last one, so `WithChanged` matches
        // nothing from the second tick onwards: the transform publish below silently stops, the
        // recorded history stops changing, and `MispredictionCount` reads zero for ever while the two
        // machines drift apart. A green number measuring nothing is worse than a red one.
        world.AdvanceVersion();

        var pawn = PawnIn(world);

        if (!pawn.IsNull && world.IsAlive(pawn) && world.Has<MoveIntent>(pawn)) {
            world.Set(pawn, input.ToIntent());
        }

        scene.Synchronize(fixedStep);
        scene.Step(fixedStep);
        scene.StepCharacters(fixedStep);
        scene.Writeback();

        // Last, and inside the tick rather than in the frame loop: the history is recorded the moment
        // this returns, and a publish that happened later would record the previous tick's pose.
        Transforms.Publish(world);

        StepCount++;

        // Read but not used: the tick is the predictor's bookkeeping, and a step that behaved
        // differently on different tick numbers would be exactly the impurity this must not have.
        _ = tick;
    }
}
