// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Combat;

/// <summary>What an ability is aimed at.</summary>
/// <remarks>
///     <b>Five, and the list is closed on purpose.</b> Every targeting mode any game ships is one of
///     these with different numbers — a cleave is a cone, a chain lightning is a target with a
///     follow-up ability, a ground-targeted circle is `Ground` with a radius. A sixth would be a
///     sixth branch in every validator that has to agree with it.
/// </remarks>
public enum AbilityTargeting {
    /// <summary>The caster. Needs nothing selected.</summary>
    Self,

    /// <summary>One other thing, within range.</summary>
    Target,

    /// <summary>A point, within range, affecting a radius around it.</summary>
    Ground,

    /// <summary>Everything in a cone from the caster.</summary>
    Cone,

    /// <summary>Everything along a line from the caster.</summary>
    Direction
}

/// <summary>What an ability costs, as a definition holds it.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///     <see cref="ModifierDefinition" />.
/// </remarks>
[DataContract("AbilityCost")]
public sealed class AbilityCostDefinition {
    /// <summary>Which resource — <c>Mana</c>, <c>Energy</c>, <c>Rage</c>.</summary>
    public string Attribute { get; set; } = string.Empty;

    /// <summary>How much of it.</summary>
    public float Amount { get; set; }
}

/// <summary>What an ability does to whatever it hits.</summary>
[DataContract("AbilityDamage")]
public sealed class DamageDefinition {
    /// <summary>What kind of damage this is — <c>Damage.Fire</c>. Empty is untyped.</summary>
    /// <remarks>
    ///     ⚠ <b>One school per hit, deliberately.</b> An ability that deals fire and frost is two
    ///     hits, because a single event with two schools has to answer "which resistance applies"
    ///     twice and every answer is a rule somebody has to remember. Two events answer it once each.
    /// </remarks>
    public string School { get; set; } = string.Empty;

    /// <summary>What it does before anything scales it.</summary>
    public float Amount { get; set; }

    /// <summary>Which of the caster's stats it scales with, or empty for none.</summary>
    public string ScalesWith { get; set; } = string.Empty;

    /// <summary>How much of that stat it adds.</summary>
    public float Coefficient { get; set; }

    /// <summary>How much threat it makes, as a multiple of the damage done.</summary>
    public float ThreatMultiplier { get; set; } = 1f;

    /// <summary>Whether it heals rather than harms.</summary>
    /// <remarks>
    ///     A flag rather than a second pipeline, because a heal is mitigated by nothing, absorbed by
    ///     nothing and applied to the same attribute — the stages that do not apply skip themselves,
    ///     and every rule about crits, threat and death is the one already written.
    /// </remarks>
    public bool IsHealing { get; set; }
}

/// <summary>An ability, as a designer wrote it.</summary>
/// <remarks>
///     <para>
///         Doc 28 § Combat: abilities on top of kernel effects — cast time, channel, global cooldown,
///         charges, resource costs and a targeting mode. What an ability <em>does</em> is damage plus
///         the effects it applies, both of which are the kernel's, so an ability is almost entirely
///         data.
///     </para>
///     <para>
///         ⚠ <b>An ability that applies an effect names it by address rather than carrying it.</b>
///         The same buff is applied by a talent, a trinket and three abilities, and four copies of it
///         is four places a balance change has to reach.
///     </para>
/// </remarks>
[DataContract("AbilityDefinition")]
public sealed record AbilityDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The address of its icon.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>What it is aimed at.</summary>
    public AbilityTargeting Targeting { get; set; }

    /// <summary>How far, in metres. Zero is unlimited, which is what a self-cast wants.</summary>
    public float Range { get; set; }

    /// <summary>How wide, in metres, for <see cref="AbilityTargeting.Ground" /> and the two shapes.</summary>
    public float Radius { get; set; }

    /// <summary>How wide, in degrees, for <see cref="AbilityTargeting.Cone" />.</summary>
    public float Angle { get; set; }

    /// <summary>How long it takes to cast, in seconds. Zero is instant.</summary>
    public float CastTime { get; set; }

    /// <summary>How long it channels for, in seconds. Zero is not a channel.</summary>
    /// <remarks>
    ///     A cast and a channel are different: a cast does its work at the end and a channel does it
    ///     every <see cref="ChannelPeriod" /> until it stops. An ability with both is a cast that
    ///     becomes a channel, which nothing ships, so <see cref="CastTime" /> wins and the channel is
    ///     ignored — reported by the library rather than silently.
    /// </remarks>
    public float ChannelTime { get; set; }

    /// <summary>How often a channel ticks, in seconds.</summary>
    public float ChannelPeriod { get; set; } = 1f;

    /// <summary>How long before it can be used again, in seconds.</summary>
    public float Cooldown { get; set; }

    /// <summary>How many uses it banks. One is an ordinary cooldown.</summary>
    public int Charges { get; set; } = 1;

    /// <summary>Whether using it starts the caster's global cooldown.</summary>
    public bool TriggersGlobalCooldown { get; set; } = true;

    /// <summary>Whether the global cooldown stops it being used.</summary>
    /// <remarks>
    ///     Separate from <see cref="TriggersGlobalCooldown" />, because the two are genuinely
    ///     independent: an interrupt is usually off the global cooldown in both directions, and a
    ///     racial is often on it one way and not the other.
    /// </remarks>
    public bool RespectsGlobalCooldown { get; set; } = true;

    /// <summary>What it costs.</summary>
    public List<AbilityCostDefinition> Costs { get; set; } = [];

    /// <summary>What has to be true of the caster.</summary>
    public List<RequirementDefinition> Requirements { get; set; } = [];

    /// <summary>What it does to what it hits, or null for an ability that only applies effects.</summary>
    public DamageDefinition? Damage { get; set; }

    /// <summary>The addresses of the effects it puts on what it hits.</summary>
    public List<string> AppliesToTarget { get; set; } = [];

    /// <summary>The addresses of the effects it puts on the caster.</summary>
    public List<string> AppliesToSelf { get; set; } = [];

    /// <summary>What this ability is — <c>Ability.Cast.Fireball</c>. What a silence blocks.</summary>
    public List<string> Tags { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in Tags) {
            tags.Add(tag);
        }

        if (Damage is { School.Length: > 0 }) {
            tags.Add(Damage.School);
        }

        foreach (var requirement in Requirements) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}
