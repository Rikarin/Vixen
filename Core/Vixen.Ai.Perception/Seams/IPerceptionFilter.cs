// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai.Perception;

/// <summary>One end of a perception test: who, on whose side, and where.</summary>
/// <param name="Entity">Which entity.</param>
/// <param name="Team">Which side it is on.</param>
/// <param name="Position">Where it is, in world space, before either height offset is applied.</param>
/// <remarks>
///     Symmetric on purpose: a filter is given the same three facts about the listener and about the
///     source, so "only hostiles", "only my own team" and "everything except my owner" are all one
///     comparison rather than three different shapes of lookup.
/// </remarks>
public readonly record struct PerceptionParticipant(Entity Entity, byte Team, Vector3 Position);

/// <summary>Who a listener is allowed to perceive.</summary>
/// <remarks>
///     doc 37 § Part 4's seam. It runs <b>before</b> the radius test, which is what makes it the
///     cheapest place to say "guards do not notice other guards" — a level where three quarters of
///     the stimuli sources are on the listener's own side is a level where this removes three
///     quarters of the work rather than three quarters of the results.
/// </remarks>
public interface IPerceptionFilter {
    /// <summary>Whether this listener may perceive this source through this sense.</summary>
    /// <param name="listener">Who is looking.</param>
    /// <param name="source">What might be found.</param>
    /// <param name="sense">Which sense is asking.</param>
    /// <returns>Whether to go on and test it.</returns>
    bool CanPerceive(in PerceptionParticipant listener, in PerceptionParticipant source, AiSense sense);
}

/// <summary>A filter written as a lambda.</summary>
/// <param name="listener">Who is looking.</param>
/// <param name="source">What might be found.</param>
/// <param name="sense">Which sense is asking.</param>
/// <returns>Whether to go on and test it.</returns>
public delegate bool PerceptionPredicate(
    in PerceptionParticipant listener,
    in PerceptionParticipant source,
    AiSense sense
);

/// <summary>The filters that ship.</summary>
public static class PerceptionFilters {
    /// <summary>Everything is fair game. The default, and what a single-faction game wants.</summary>
    public static IPerceptionFilter Everyone { get; } = new EveryonePerceptionFilter();

    /// <summary>Hostiles only, on the usual reading of a team byte.</summary>
    public static IPerceptionFilter Hostiles { get; } = new TeamPerceptionFilter();

    /// <summary>A filter from a lambda.</summary>
    /// <param name="predicate">What it does.</param>
    /// <returns>The filter.</returns>
    public static IPerceptionFilter Where(PerceptionPredicate predicate) => new DelegatePerceptionFilter(predicate);
}

/// <summary>Perceives everything.</summary>
sealed class EveryonePerceptionFilter : IPerceptionFilter {
    public bool CanPerceive(in PerceptionParticipant listener, in PerceptionParticipant source, AiSense sense) =>
        listener.Entity != source.Entity;
}

/// <summary>Perceives by side.</summary>
/// <remarks>
///     <para>
///         The shipped reading of the team byte: same number means friendly, a different number means
///         hostile, and one reserved number means neutral. That covers a shooter, a stealth game and
///         an RTS; a game whose factions are a matrix uses <see cref="PerceptionFilters.Where" /> and
///         looks its own table up.
///     </para>
///     <para>
///         ⚠ <b><see cref="AiSense.Damage" /> is always allowed through.</b> An agent shot by its own
///         side has to notice, or friendly fire is invisible to the AI and a squad walks through its
///         own grenades. That is a rule about what damage <i>is</i> rather than a configurable, so it
///         is not a field.
///     </para>
/// </remarks>
public sealed class TeamPerceptionFilter : IPerceptionFilter {
    /// <summary>Whether it perceives its own side.</summary>
    public bool Friendly { get; init; }

    /// <summary>Whether it perceives other sides.</summary>
    public bool Hostile { get; init; } = true;

    /// <summary>Whether it perceives the neutral side.</summary>
    public bool Neutral { get; init; } = true;

    /// <summary>The team number that means "nobody's".</summary>
    public byte NeutralTeam { get; init; } = 255;

    /// <inheritdoc />
    public bool CanPerceive(in PerceptionParticipant listener, in PerceptionParticipant source, AiSense sense) {
        if (listener.Entity == source.Entity) {
            return false;
        }

        if (sense == AiSense.Damage) {
            return true;
        }

        if (source.Team == NeutralTeam) {
            return Neutral;
        }

        return source.Team == listener.Team ? Friendly : Hostile;
    }
}

/// <summary>A filter that is a lambda.</summary>
/// <param name="predicate">What it does.</param>
public sealed class DelegatePerceptionFilter(PerceptionPredicate predicate) : IPerceptionFilter {
    readonly PerceptionPredicate predicate =
        predicate ?? throw new ArgumentNullException(nameof(predicate));

    /// <inheritdoc />
    public bool CanPerceive(in PerceptionParticipant listener, in PerceptionParticipant source, AiSense sense) =>
        predicate(in listener, in source, sense);
}
