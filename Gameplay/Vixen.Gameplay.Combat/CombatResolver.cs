// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Combat;

/// <summary>What an ability did to one thing it hit.</summary>
/// <param name="Target">Who.</param>
/// <param name="Amount">How much landed.</param>
/// <param name="Absorbed">How much a shield soaked.</param>
/// <param name="Mitigated">How much resistance took off.</param>
/// <param name="Critical">Whether it crit.</param>
/// <param name="Threat">How much threat it made.</param>
/// <param name="Killed">Whether it killed them.</param>
public readonly record struct AbilityHit(
    ulong Target,
    float Amount,
    float Absorbed,
    float Mitigated,
    bool Critical,
    float Threat,
    bool Killed
);

/// <summary>Turns a completed ability into damage and effects on the things it hit.</summary>
/// <remarks>
///     <para>
///         <b>Separate from <see cref="AbilityCaster" />, because a caster knows what an ability was
///         aimed at and this knows what it hit.</b> Resolving a cone into a list of victims needs a
///         world, which is the caller's; what happens to that list is the same code whether there is
///         one of them or forty.
///     </para>
///     <para>
///         ⚠ <b>The event id is the caller's and every roll comes out of it.</b> One id per ability
///         completion, salted by the victim's index, so a forty-target cleave is reproducible as a
///         whole and each of its forty hits is reproducible on its own.
///     </para>
/// </remarks>
public sealed class CombatResolver {
    /// <summary>Makes a resolver.</summary>
    /// <param name="pipeline">The stages a hit passes through.</param>
    /// <param name="attributes">Which stats the shipped rules read, or null for the obvious names.</param>
    public CombatResolver(DamagePipeline pipeline, CombatAttributes? attributes = null) {
        ArgumentNullException.ThrowIfNull(pipeline);

        Pipeline = pipeline;
        Attributes = attributes ?? CombatAttributes.Default;
    }

    /// <summary>The stages a hit passes through.</summary>
    public DamagePipeline Pipeline { get; }

    /// <summary>Which stats the shipped rules read.</summary>
    public CombatAttributes Attributes { get; }

    /// <summary>Applies a completed ability to the things it hit.</summary>
    /// <param name="ability">Which ability.</param>
    /// <param name="source">Who cast it, or null for the world.</param>
    /// <param name="targets">What it hit, as whatever owns the world resolved them.</param>
    /// <param name="eventId">What caused it. Every roll comes out of this.</param>
    /// <param name="hits">Where to report what happened to each, or null.</param>
    /// <remarks>
    ///     The caster's own effects are applied once, before the targets, so a self-buff that raises
    ///     power is already up when the damage is computed — which is what an ability that reads
    ///     "gain 20 % power, then strike" means.
    /// </remarks>
    public void Resolve(
        AbilityTemplate ability,
        GameplaySubject? source,
        IReadOnlyList<AbilityTarget> targets,
        ulong eventId,
        ICollection<AbilityHit>? hits = null
    ) {
        ArgumentNullException.ThrowIfNull(ability);
        ArgumentNullException.ThrowIfNull(targets);

        if (source is not null) {
            foreach (var effect in ability.AppliesToSelf) {
                source.Effects.Apply(effect, eventId);
            }

            source.Attributes.Recompute();
        }

        for (var index = 0; index < targets.Count; index++) {
            var target = targets[index];

            if (target.Subject is not { } subject) {
                continue;
            }

            var hit = Strike(ability, source, subject, eventId, (ulong)index);

            foreach (var effect in ability.AppliesToTarget) {
                subject.Effects.Apply(effect, eventId);
            }

            subject.Attributes.Recompute();

            hits?.Add(
                new(
                    target.Id,
                    hit.Applied,
                    hit.Absorbed,
                    hit.Mitigated,
                    hit.IsCritical,
                    hit.Threat,
                    hit.Killed
                )
            );
        }
    }

    /// <summary>Runs one hit through the pipeline.</summary>
    /// <param name="ability">Which ability.</param>
    /// <param name="source">Who cast it, or null.</param>
    /// <param name="target">Who it hit.</param>
    /// <param name="eventId">What caused it.</param>
    /// <param name="salt">Which of the ability's targets this is.</param>
    /// <returns>The event, as the stages left it.</returns>
    /// <remarks>
    ///     Public because an environmental hazard, a falling-damage rule and a damage-over-time tick
    ///     all want the pipeline without an ability's targeting — and because a test wants to look at
    ///     one hit rather than a list of results.
    /// </remarks>
    public DamageEvent Strike(
        AbilityTemplate ability,
        GameplaySubject? source,
        GameplaySubject target,
        ulong eventId,
        ulong salt = 0
    ) {
        ArgumentNullException.ThrowIfNull(ability);
        ArgumentNullException.ThrowIfNull(target);

        var hit = new DamageEvent {
            Ability = ability.Id,
            EventId = eventId,
            Source = source,
            Target = target,
            School = ability.School,
            IsHealing = ability.Damage?.IsHealing ?? false,
            ThreatMultiplier = ability.Damage?.ThreatMultiplier ?? 1f,
            Random = GameplayRandom.For(eventId, salt),
            Amount = ability.BaseAmount(source)
        };

        if (ability.Damage is not null) {
            Pipeline.Run(ref hit);
        }

        return hit;
    }
}
