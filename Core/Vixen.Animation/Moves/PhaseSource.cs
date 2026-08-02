// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Moves;

/// <summary>Something that has a cycle another set can hang off.</summary>
/// <remarks>
///     <para>
///         <b>Why an interface rather than a reference to a <see cref="MoveSetMotion" />.</b> What an
///         upper body needs to stay in step with is "the thing driving the legs", and that is a move
///         set today, a blend tree in a project that has not adopted move sets, and a walk cycle
///         computed in code in a prototype. All three can say where they are.
///     </para>
/// </remarks>
public interface IPhaseSource {
    /// <summary>Where the cycle is, in <c>[0, 1]</c>.</summary>
    /// <param name="phase">The fraction.</param>
    /// <param name="footPhase">
    ///     Where in that cycle a foot plants, so a follower can align on contact rather than on
    ///     fraction. <see cref="float.NaN" /> when the source has no feet to speak of.
    /// </param>
    /// <returns>Whether the source has a cycle at all right now.</returns>
    bool TryGetPhase(out float phase, out float footPhase);
}

/// <summary>Where a set takes its cycle from.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The omission that would otherwise have surfaced in P7, with content.</b>
///         <c>AnimationLayer</c> and <c>BoneMask</c> already mix a masked upper body over a base, so
///         the layer half looked built — and is. What was missing is the half that makes it not look
///         wrong: an upper-body carry cycle free-running over a walk drifts against the footfalls,
///         and shoulders that stop agreeing with feet read instantly as two animations to somebody
///         who cannot say why. One move set looks fine. Two look wrong.
///     </para>
/// </remarks>
public enum PhaseSourceMode {
    /// <summary>Free-running, on the layer's own clock. Right for a gesture.</summary>
    Own,

    /// <summary>Driven by another set's fraction.</summary>
    Follow,

    /// <summary>
    ///     Driven by another set's <em>contacts</em>: this set's footfall is aligned to the source's.
    /// </summary>
    /// <remarks>
    ///     The one that matters, and why <see cref="MoveTraits.FootPhase" /> is stored on an entry
    ///     rather than derived. A carry cycle authored at four steps and a walk playing at two are
    ///     aligned by contact, not by fraction.
    /// </remarks>
    FollowFootfall
}

/// <summary>How a set is driven: on its own clock, or off another one.</summary>
/// <param name="Mode">Which of the three.</param>
/// <param name="Source">
///     What it follows, or <see langword="null" /> for <see cref="PhaseSourceMode.Own" />.
/// </param>
public readonly record struct PhaseSource(PhaseSourceMode Mode, IPhaseSource? Source = null) {
    /// <summary>Free-running.</summary>
    public static PhaseSource Own => new(PhaseSourceMode.Own);

    /// <summary>Following another set's fraction.</summary>
    /// <param name="source">The set to follow.</param>
    /// <returns>The source.</returns>
    public static PhaseSource Follow(IPhaseSource source) {
        ArgumentNullException.ThrowIfNull(source);
        return new(PhaseSourceMode.Follow, source);
    }

    /// <summary>Following another set's contacts.</summary>
    /// <param name="source">The set to follow.</param>
    /// <returns>The source.</returns>
    public static PhaseSource FollowFootfall(IPhaseSource source) {
        ArgumentNullException.ThrowIfNull(source);
        return new(PhaseSourceMode.FollowFootfall, source);
    }

    /// <summary>The phase a follower should play at, given its own move's contact.</summary>
    /// <param name="own">Where the follower's own clock is, in <c>[0, 1]</c>.</param>
    /// <param name="ownFootPhase">Where the follower's move plants, in <c>[0, 1]</c>.</param>
    /// <returns>The phase to sample at.</returns>
    /// <remarks>
    ///     ⚠ <b>The offset is the difference between the two contacts, not between the two
    ///     phases.</b> Matching phases makes the two cycles agree about where their <i>starts</i> are,
    ///     which is a fact about how somebody trimmed the clips and not about the character.
    /// </remarks>
    public float Resolve(float own, float ownFootPhase) {
        if (Mode is PhaseSourceMode.Own || Source is null || !Source.TryGetPhase(out var driven, out var theirs)) {
            return own;
        }

        if (Mode is PhaseSourceMode.Follow || float.IsNaN(theirs) || float.IsNaN(ownFootPhase)) {
            return Wrap(driven);
        }

        return Wrap(driven - theirs + ownFootPhase);
    }

    static float Wrap(float phase) {
        var wrapped = phase % 1f;
        return wrapped < 0f ? wrapped + 1f : wrapped;
    }
}
