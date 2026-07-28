// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.StateMachine;

/// <summary>Which state's transitions may cut a transition short while it is still running.</summary>
/// <remarks>
///     The default is <see cref="None" />, and that is not timidity. A graph in which every
///     transition can be interrupted by every other is a graph where a button pressed twice in
///     quick succession produces a pose blended from four clips at 25 % each — which is a character
///     that looks like nothing at all. Interruption is opted into, per transition, where the
///     responsiveness is worth the cost.
/// </remarks>
public enum TransitionInterruption {
    /// <summary>Nothing interrupts it. It runs to completion.</summary>
    None,

    /// <summary>The state it is leaving may still transition elsewhere.</summary>
    Source,

    /// <summary>The state it is arriving at may transition onwards before it has arrived.</summary>
    Destination,

    /// <summary>Both, with the source taking priority.</summary>
    SourceThenDestination
}

/// <summary>A way out of a state: where to, when, how long the crossfade takes.</summary>
/// <remarks>
///     <para>
///         <b>Exit time is normalised and conditions are not.</b> A transition with conditions and no
///         exit time fires the moment they hold — which is what a jump wants. One with an exit time
///         fires when the state's own playback passes that fraction — which is what an attack
///         wants, because it must finish its swing. One with both waits for the fraction
///         <em>and</em> the conditions, and one with neither fires immediately, which is how a
///         state that is only a stepping stone is written.
///     </para>
///     <para>
///         <b>The duration is in seconds and not in fractions.</b> A crossfade is a thing a person
///         perceives, and a fifth of a second is a fifth of a second whether the state it leaves is
///         a half-second attack or a four-second idle. Fixing it as a fraction of the source clip
///         makes the same authored transition feel wrong in two places.
///     </para>
/// </remarks>
public sealed class AnimationTransition {
    readonly List<AnimationCondition> conditions = [];

    /// <summary>Creates a transition.</summary>
    /// <param name="destination">Where it goes.</param>
    /// <param name="duration">How long the crossfade takes, in seconds.</param>
    public AnimationTransition(AnimationState destination, float duration = 0.2f) {
        ArgumentNullException.ThrowIfNull(destination);

        Destination = destination;
        Duration = MathF.Max(duration, 0f);
    }

    /// <summary>Where it goes.</summary>
    public AnimationState Destination { get; }

    /// <summary>How long the crossfade takes, in seconds. Zero is a cut.</summary>
    public float Duration { get; }

    /// <summary>Whether the source state has to reach <see cref="ExitTime" /> first.</summary>
    public bool HasExitTime { get; init; }

    /// <summary>
    ///     How far through the source state the transition may start, as a fraction of its length.
    /// </summary>
    /// <remarks>
    ///     One means the end of a pass, which for a looping state comes round every pass and for a
    ///     clamped one is where it stops. <b>Values above one are clamped to one</b> — counting
    ///     whole passes would need a pass counter on the playback entry that nothing else wants, and
    ///     "after three loops" is a condition on a parameter the game already knows how to set.
    /// </remarks>
    public float ExitTime { get; init; } = 1f;

    /// <summary>Where the destination starts, as a fraction of its length.</summary>
    /// <remarks>
    ///     What a run-to-stop transition needs: the stop clip is authored from the moment the foot
    ///     lands, and starting it at zero puts the character back on the other foot.
    /// </remarks>
    public float Offset { get; init; }

    /// <summary>What may cut this transition short.</summary>
    public TransitionInterruption Interruption { get; init; } = TransitionInterruption.None;

    /// <summary>Whether a state may transition to itself, restarting it.</summary>
    /// <remarks>
    ///     Off by default, because the common author error is a condition that stays true and a
    ///     transition back to the state it left, which restarts the clip every frame and produces a
    ///     character frozen on frame one.
    /// </remarks>
    public bool CanTransitionToSelf { get; init; }

    /// <summary>What has to hold for it to fire.</summary>
    public IReadOnlyList<AnimationCondition> Conditions => conditions;

    /// <summary>Adds a condition.</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>The transition, for chaining.</returns>
    public AnimationTransition When(AnimationCondition condition) {
        conditions.Add(condition);
        return this;
    }

    /// <summary>
    ///     Whether every condition holds. Does not consume triggers — see
    ///     <see cref="AnimationCondition.Consume" />.
    /// </summary>
    /// <param name="parameters">The parameter set.</param>
    /// <returns><see langword="true" /> if the conditions are met.</returns>
    public bool ConditionsHold(AnimationParameters parameters) {
        foreach (var condition in conditions) {
            if (!condition.IsSatisfied(parameters)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Takes whatever the conditions consume. Called only when the transition is taken.</summary>
    /// <param name="parameters">The parameter set.</param>
    public void ConsumeConditions(AnimationParameters parameters) {
        foreach (var condition in conditions) {
            condition.Consume(parameters);
        }
    }
}
