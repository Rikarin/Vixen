// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ai.Perception;

/// <summary>How what a listener perceived becomes keys a tree can branch on.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 4's seam, and the join between this assembly and <c>Vixen.Ai</c>. Perception
///         writes through <c>Blackboard.Set*</c>, which means every decorator observing those keys
///         gets its abort for free — a target appearing is already the mechanism that interrupts a
///         patrol, with nothing in the perception pass knowing a behaviour tree exists.
///     </para>
///     <para>
///         ⚠ <b>The binding runs on the pass, not on the frame.</b> A listener updating at 4 Hz writes
///         its keys at 4 Hz, so a tree observing them reacts at the rate the sense was configured for
///         and not at the rate the renderer happens to be running.
///     </para>
/// </remarks>
public interface IBlackboardBinding {
    /// <summary>Writes a pass's results.</summary>
    /// <param name="perceived">What the listener knows.</param>
    /// <param name="blackboard">Where to write it.</param>
    /// <param name="listener">Where the listener is, for anything that needs a distance.</param>
    /// <param name="now">The clock, for ages.</param>
    void Write(PerceivedTargets perceived, Blackboard blackboard, Vector3 listener, float now);
}

/// <summary>The default triple: who, where they were, and how long ago.</summary>
/// <param name="senses">Which senses feed it.</param>
/// <param name="target">The key holding the entity.</param>
/// <param name="location">The key holding its last known location, or null for none.</param>
/// <param name="age">The key holding the stimulus age in seconds, or null for none.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The target key stays set after the target is lost, and the age key is how a tree
///         tells.</b> Clearing it on losing sight would make "chase him" and "search where he was"
///         two different branches reading two different keys, and the second one would need its own
///         copy of the position and its own timer — which is the hand-written memory management this
///         exists to remove. A branch that only wants a live target tests
///         <c>age &lt; 0.5</c>; a branch that wants to search tests <c>age &gt; 0.5</c>; and both read
///         one key.
///     </para>
///     <para>
///         The keys are optional individually. A game that only branches on "is there anything" binds
///         the target and leaves the other two out, and pays for nothing it did not ask for.
///     </para>
///     <para>
///         ⚠ <b>Nullable rather than <c>default</c> for the optional two</b>, because
///         <c>default(BlackboardKey)</c> is index zero — a perfectly real key — and an omitted
///         argument would silently bind the first key in the layout.
///     </para>
/// </remarks>
public sealed class TargetLocationAgeBinding(
    SenseMask senses,
    BlackboardKey target,
    BlackboardKey? location = null,
    BlackboardKey? age = null
) : IBlackboardBinding {
    /// <summary>Which senses feed it.</summary>
    public SenseMask Senses { get; } = senses;

    /// <inheritdoc />
    public void Write(PerceivedTargets perceived, Blackboard blackboard, Vector3 listener, float now) {
        ArgumentNullException.ThrowIfNull(perceived);
        ArgumentNullException.ThrowIfNull(blackboard);

        if (!perceived.TryFreshest(Senses, out var best)) {
            Clear(blackboard);

            return;
        }

        blackboard.SetEntity(target, best.Source);

        if (location is { } where) {
            blackboard.SetVector3(where, best.LastKnownLocation);
        }

        if (age is { } elapsed) {
            blackboard.SetFloat(elapsed, best.AgeAt(now));
        }
    }

    void Clear(Blackboard blackboard) {
        blackboard.Clear(target);

        if (location is { } where) {
            blackboard.Clear(where);
        }

        if (age is { } elapsed) {
            blackboard.Clear(elapsed);
        }
    }
}

/// <summary>A different shape: how many, and whether there are any at all.</summary>
/// <param name="senses">Which senses feed it.</param>
/// <param name="alert">A bool key: whether anything is being perceived right now.</param>
/// <param name="count">An int key holding how many, or null for none.</param>
/// <remarks>
///     The second implementation the seam rule asks for, and it differs in shape rather than in
///     numbers: it names no target at all. That is what a turret, an alarm, a patrol that speeds up
///     when the area is busy, or a "you are outnumbered, fall back" branch actually reads — and each
///     of those written against the triple would be a decorator comparing an entity key to nothing in
///     particular.
///
///     ⚠ It counts only what is <i>currently</i> perceived, not what is remembered. "Two of them are
///     in the room" and "two of them were in the room at some point in the last five seconds" are
///     different claims, and only the first is worth an alarm.
/// </remarks>
public sealed class PerceivedCountBinding(SenseMask senses, BlackboardKey alert, BlackboardKey? count = null)
    : IBlackboardBinding {
    /// <summary>Which senses feed it.</summary>
    public SenseMask Senses { get; } = senses;

    /// <inheritdoc />
    public void Write(PerceivedTargets perceived, Blackboard blackboard, Vector3 listener, float now) {
        ArgumentNullException.ThrowIfNull(perceived);
        ArgumentNullException.ThrowIfNull(blackboard);

        var live = 0;

        foreach (var candidate in perceived.Targets) {
            if (candidate.Current && Senses.Has(candidate.Sense)) {
                live++;
            }
        }

        blackboard.SetBool(alert, live > 0);

        if (count is { } many) {
            blackboard.SetInt(many, live);
        }
    }
}
