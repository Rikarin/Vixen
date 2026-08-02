// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Moves;

/// <summary>What a body is doing right now, as the gait model needs to see it.</summary>
/// <param name="Velocity">Planar velocity in world space, in metres a second.</param>
/// <param name="Facing">Which way the body points, in radians about the world's up axis.</param>
/// <param name="TurnRate">How fast the facing is changing, in radians a second.</param>
/// <param name="IsGrounded">Whether the feet are on something.</param>
/// <remarks>
///     ⚠ <b>Measured, not wanted.</b> This is the body's actual state, which is the controller's
///     output; what the player asked for is <c>MoveIntent</c> and the two differ whenever a wall,
///     a slope or an acceleration limit gets in the way. Animating the intent rather than the result
///     is what makes a character run on the spot against a wall.
/// </remarks>
public readonly record struct MoveState(Vector2 Velocity, float Facing, float TurnRate, bool IsGrounded);

/// <summary>Turns what a body is doing into numbers a move can be measured against.</summary>
/// <remarks>
///     <para>
///         <b>The one place a body's kind shows up in selection.</b> A biped, a quadruped and
///         something with wheels differ here and nowhere else in this namespace, which is why the
///         interface exists before the second implementation does.
///     </para>
///     <para>
///         ⚠ <b>The boundary with the character controller, drawn once and enforced by the
///         signature.</b> A body's movement model has two halves with different owners:
///     </para>
///     <list type="table">
///         <item>
///             <term>How the body <i>may</i> move</term>
///             <description>
///                 Acceleration and braking, top speed per gait, turn radius, whether it strafes or
///                 turns first. The controller's, in doc 29. Simulated, replicated,
///                 server-authoritative.
///             </description>
///         </item>
///         <item>
///             <term>What the animation should therefore <i>be</i></term>
///             <description>
///                 The numeric targets a query is scored against. This. A presentation detail that
///                 may legitimately differ per client and per level of detail.
///             </description>
///         </item>
///     </list>
///     <para>
///         They are easy to confuse because a quadruped differs in both, and the temptation is one
///         object answering both questions. <b>A gait model reads the controller's state and never
///         sets it</b> — which is why <see cref="Describe" /> takes the state by value and writes
///         only into the targets.
///     </para>
/// </remarks>
public interface IGaitModel {
    /// <summary>Describes what the animation should be doing.</summary>
    /// <param name="state">What the body is doing.</param>
    /// <param name="targets">Where the answer goes.</param>
    void Describe(in MoveState state, ref MoveTargets targets);
}

/// <summary>The shipped model: two legs, forward-facing, turning on the spot below a threshold.</summary>
/// <remarks>
///     <para>
///         <b>Speed is the planar magnitude and nothing else.</b> A biped's stride length is a
///         function of how fast it is going, which is the whole of what a locomotion set needs to
///         know to pick between a walk, a jog and a run.
///     </para>
///     <para>
///         ⚠ <b>Stride length depends on leg length, and this model does not know any.</b> A
///         character half a metre shorter takes shorter steps at the same speed, so a model given
///         only a velocity picks the same clip and the same retiming for both — and retiming a walk
///         to a speed the legs cannot reach is the sliding-feet bug arriving by the front door.
///         <see cref="LegLength" /> is the knob, defaulting to the proportions the shipped clips are
///         authored against; a project with a body model feeds it from there.
///     </para>
/// </remarks>
public sealed class BipedGaitModel : IGaitModel {
    /// <summary>The hip-to-floor distance the move set's clips were authored against, in metres.</summary>
    public float ReferenceLegLength { get; init; } = 0.9f;

    /// <summary>This character's, in metres.</summary>
    public float LegLength { get; init; } = 0.9f;

    /// <summary>Below this speed the body is treated as turning in place rather than walking.</summary>
    /// <remarks>
    ///     A threshold and not a taper, because the distinction is discrete in the content: an idle
    ///     turn and a walking turn are different clips, and a query that asked for something halfway
    ///     between them would score both badly and pick whichever was nearer by accident.
    /// </remarks>
    public float StandingSpeed { get; init; } = 0.15f;

    /// <inheritdoc />
    public void Describe(in MoveState state, ref MoveTargets targets) {
        var speed = state.Velocity.Length();

        // Normalised into the clips' own proportions, so a short character asking for 3 m/s asks the
        // set for the gait that a reference-sized character would use at a proportionally higher
        // speed — which is the one whose stride actually covers the ground at that size.
        var scale = LegLength > 1e-3f ? ReferenceLegLength / LegLength : 1f;

        targets = targets with {
            Speed = speed < StandingSpeed ? 0f : speed * scale,
            TurnRate = state.TurnRate
        };
    }
}
