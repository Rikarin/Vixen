// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Combat;

/// <summary>Which stats the shipped rules read.</summary>
/// <remarks>
///     <para>
///         <b>Named here rather than hard-coded, because a game's stats are its own.</b> The shipped
///         rules need to know which attribute is health and which is crit chance; they do not need
///         those to be called <c>Health</c> and <c>CritChance</c>.
///     </para>
///     <para>
///         ⚠ <b>Resistance is per school and therefore a map rather than a name.</b> "Fire resistance
///         reduces <c>Damage.Fire.*</c>" is a tag query, and building the attribute name by
///         concatenating a school's name would be a string operation on the damage path.
///     </para>
/// </remarks>
public sealed record CombatAttributes {
    /// <summary>The obvious names, for a game that has no opinion yet.</summary>
    public static CombatAttributes Default { get; } = new();

    /// <summary>What damage comes off and healing goes on to.</summary>
    public AttributeId Health { get; init; } = AttributeId.From("Health");

    /// <summary>The ceiling healing may not pass. Zero means no ceiling.</summary>
    public AttributeId MaximumHealth { get; init; } = AttributeId.From("MaximumHealth");

    /// <summary>How often a hit crits, from zero to one.</summary>
    public AttributeId CriticalChance { get; init; } = AttributeId.From("CritChance");

    /// <summary>What a crit multiplies by. Below one is read as the shipped default of two.</summary>
    public AttributeId CriticalMultiplier { get; init; } = AttributeId.From("CritMultiplier");

    /// <summary>What a shield soaks before health is touched.</summary>
    public AttributeId Absorb { get; init; } = AttributeId.From("Absorb");

    /// <summary>Which stat resists which school. Each value is a fraction from zero to one.</summary>
    public IReadOnlyList<(GameplayTagRange School, AttributeId Attribute)> Resistances { get; init; } = [];

    /// <summary>The same, with the resistances resolved against a tag table.</summary>
    /// <param name="tags">The table.</param>
    /// <param name="resistances">Pairs of tag prefix and attribute name.</param>
    /// <returns>The attributes.</returns>
    public CombatAttributes WithResistances(
        GameplayTagTable tags,
        params (string School, string Attribute)[] resistances
    ) {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(resistances);

        return this with {
            Resistances = [.. resistances.Select(pair => (tags.RangeOf(pair.School), AttributeId.From(pair.Attribute)))]
        };
    }
}

/// <summary>Compute: what the ability is worth before anything happens to it.</summary>
/// <remarks>
///     The amount is already on the event when the pipeline runs — <see cref="AbilityCaster" /> puts
///     the definition's base plus its coefficient there — so this rule exists to make the stage
///     non-empty and to be the thing a game replaces when its formula is not "base plus coefficient".
///     It clamps rather than computing, which is the only universally true statement about damage.
/// </remarks>
public sealed class BaseDamageRule : IDamageRule {
    /// <inheritdoc />
    public DamageStage Stage => DamageStage.Compute;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public void Apply(ref DamageEvent hit) => hit.Amount = MathF.Max(0f, hit.Amount);
}

/// <summary>Crit: whether it crit, and what that multiplies it by.</summary>
public sealed class CriticalStrikeRule(CombatAttributes attributes) : IDamageRule {
    /// <summary>What a crit multiplies by when the caster declares no multiplier.</summary>
    public const float DefaultMultiplier = 2f;

    /// <inheritdoc />
    public DamageStage Stage => DamageStage.Crit;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The roll happens whether or not the caster can crit.</b> Drawing conditionally would
    ///     make the stream's position depend on the caster's stats, so the same event id would
    ///     produce different later rolls for two different attackers — and an audit that cannot
    ///     recompute a hit is not an audit.
    /// </remarks>
    public void Apply(ref DamageEvent hit) {
        var chance = hit.Source?.Attributes.ValueOf(attributes.CriticalChance) ?? 0f;
        var rolled = hit.Random.Chance(chance);

        if (!rolled) {
            return;
        }

        var multiplier = hit.Source?.Attributes.ValueOf(attributes.CriticalMultiplier) ?? 0f;

        hit.IsCritical = true;
        hit.Amount *= multiplier > 1f ? multiplier : DefaultMultiplier;
    }
}

/// <summary>Mitigate: what resistance takes off.</summary>
public sealed class ResistanceRule(CombatAttributes attributes) : IDamageRule {
    /// <summary>The most any resistance may take off. Above this, damage stops meaning anything.</summary>
    public const float Cap = 0.75f;

    /// <inheritdoc />
    public DamageStage Stage => DamageStage.Mitigate;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     Healing skips the stage rather than being mitigated by zero, so a game that adds a
    ///     mitigation rule does not have to remember to exclude heals from it.
    /// </remarks>
    public void Apply(ref DamageEvent hit) {
        if (hit.IsHealing || !hit.School.IsSome) {
            return;
        }

        foreach (var (school, attribute) in attributes.Resistances) {
            if (!school.Contains(hit.School)) {
                continue;
            }

            var fraction = Math.Clamp(hit.Target.Attributes.ValueOf(attribute), 0f, Cap);
            var removed = hit.Amount * fraction;

            hit.Mitigated += removed;
            hit.Amount -= removed;

            return;
        }
    }
}

/// <summary>Absorb: what a shield soaks, out of what mitigation left.</summary>
public sealed class ShieldAbsorbRule(CombatAttributes attributes) : IDamageRule {
    /// <inheritdoc />
    public DamageStage Stage => DamageStage.Absorb;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The shield is a base value the rule spends, not a modifier.</b> A modifier belongs to
    ///     whatever granted it and is removed by source; a shield is consumed, and consuming a
    ///     modifier would mean editing a value an effect will later take back off in full.
    /// </remarks>
    public void Apply(ref DamageEvent hit) {
        if (hit.IsHealing || hit.Amount <= 0f) {
            return;
        }

        var shield = hit.Target.Attributes.ValueOf(attributes.Absorb);

        if (shield <= 0f) {
            return;
        }

        var soaked = MathF.Min(shield, hit.Amount);

        hit.Absorbed = soaked;
        hit.Amount -= soaked;
        hit.Target.Attributes.SetBase(
            attributes.Absorb,
            hit.Target.Attributes.BaseOf(attributes.Absorb) - soaked
        );
    }
}

/// <summary>Apply: what actually happens to the target's health.</summary>
public sealed class HealthRule(CombatAttributes attributes) : IDamageRule {
    /// <inheritdoc />
    public DamageStage Stage => DamageStage.Apply;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Health is a base value rather than a modifier, and the difference matters.</b> A
    ///     modifier is removed by source and recomputed from the survivors, which is exactly right
    ///     for a buff and exactly wrong for a wound: taking damage is not something that gets
    ///     undone when the thing that caused it expires.
    /// </remarks>
    public void Apply(ref DamageEvent hit) {
        var health = hit.Target.Attributes;
        var before = health.ValueOf(attributes.Health);
        var applied = MathF.Max(0f, hit.Amount);

        if (hit.IsHealing) {
            var ceiling = health.ValueOf(attributes.MaximumHealth);

            if (ceiling > 0f) {
                applied = MathF.Min(applied, MathF.Max(0f, ceiling - before));
            }

            health.SetBase(attributes.Health, health.BaseOf(attributes.Health) + applied);
        } else {
            health.SetBase(attributes.Health, health.BaseOf(attributes.Health) - applied);
        }

        hit.Applied = applied;
        hit.Killed = !hit.IsHealing && before > 0f && health.ValueOf(attributes.Health) <= 0f;
    }
}

/// <summary>React: what the world does about it.</summary>
/// <remarks>
///     Threat only. Procs, interrupts and death are a game's, and each is a rule of its own at this
///     stage — which is what the stage is for.
/// </remarks>
public sealed class ThreatRule : IDamageRule {
    /// <inheritdoc />
    public DamageStage Stage => DamageStage.React;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Healing makes threat on everything already fighting the healer's side, and this rule
    ///     cannot know who that is.</b> So it records the number and leaves the fan-out to whoever
    ///     owns the encounter — which is <c>Vixen.Gameplay.Instances</c>'s, and is why
    ///     <see cref="DamageEvent.Threat" /> is a figure on the event rather than a table write.
    /// </remarks>
    public void Apply(ref DamageEvent hit) => hit.Threat = hit.Applied * hit.ThreatMultiplier;
}
