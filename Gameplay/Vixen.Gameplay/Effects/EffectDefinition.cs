// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay;

/// <summary>What a second application of an effect that is already there does.</summary>
/// <remarks>
///     <b>Five policies rather than five systems.</b> Doc 28 § Effects: buff, debuff,
///     damage-over-time, cooldown, crowd control, aura, shield and stance are one type with a policy,
///     which is one replication path, one save path, one inspector and one set of stacking bugs to
///     fix once.
/// </remarks>
public enum EffectStacking {
    /// <summary>The second application is refused while the first is running.</summary>
    None,

    /// <summary>The remaining duration goes back to full. One instance, one stack.</summary>
    Refresh,

    /// <summary>The full duration is added to what is left. One instance, one stack.</summary>
    Extend,

    /// <summary>
    ///     One instance whose stack count rises to <see cref="EffectDefinition.MaximumStacks" />, with
    ///     the duration refreshed each time. Modifiers scale with the count.
    /// </summary>
    StackTo,

    /// <summary>Every application is its own instance with its own duration.</summary>
    Independent
}

/// <summary>One modifier, as a definition holds it: names rather than ids.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable</b>, because the YAML binder takes part only in members it can
///     write on both sides — the rule <c>BehaviorKeyContent</c> records at more length. A get-only
///     property round-trips to nothing.
/// </remarks>
[DataContract("ModifierDefinition")]
public sealed class ModifierDefinition {
    /// <summary>Which stat — <c>Power</c>.</summary>
    public string Attribute { get; set; } = string.Empty;

    /// <summary>Which bucket, and therefore which stage of the evaluation.</summary>
    public ModifierOp Op { get; set; }

    /// <summary>How much. A percentage is a fraction: <c>0.15</c> is 15 %.</summary>
    public float Value { get; set; }
}

/// <summary>Anything with a duration: a buff, a debuff, a stun, a shield, a stance, a mount.</summary>
/// <remarks>
///     <para>
///         <b>One type, and doc 28 means it literally.</b> "Everything with a duration in this
///         document is an effect: a mount is an effect that grants <c>State.Mounted</c> and swaps a
///         model; a resurrection sickness, a crafting station's attunement, a PvP flag, a raid buff, a
///         quest's timed escort — all of them."
///     </para>
///     <para>
///         <b>"Until a condition" is <see cref="CancelOn" />.</b> Doc 28's duration column offers
///         finite, infinite or until-a-condition; the first two are <see cref="Duration" /> and the
///         third is a tag query over gameplay events, because that is what every such condition
///         actually is — until damaged, until they move, until they die, until they cast. Giving it a
///         second mechanism would mean two ways to write the same rule and two places to fix it.
///     </para>
/// </remarks>
[DataContract("EffectDefinition")]
public sealed record EffectDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How long it lasts, in seconds. Zero or less is forever.</summary>
    public float Duration { get; set; }

    /// <summary>How often it ticks, in seconds. Zero is never — the effect is not periodic.</summary>
    public float Period { get; set; }

    /// <summary>What a second application does.</summary>
    public EffectStacking Stacking { get; set; }

    /// <summary>How high <see cref="EffectStacking.StackTo" /> counts. Ignored by the other policies.</summary>
    public int MaximumStacks { get; set; } = 1;

    /// <summary>What it does to the target's stats. Values scale with the stack count.</summary>
    public List<ModifierDefinition> Modifiers { get; set; } = [];

    /// <summary>What this effect <em>is</em> — <c>Effect.Control.Stun</c>. What an immunity matches.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Tags the target has for as long as this is active — <c>State.Stunned</c>.</summary>
    public List<string> GrantedTags { get; set; } = [];

    /// <summary>
    ///     Tag prefixes the target may not do while this is active — a stun blocks
    ///     <c>Ability.Cast</c>.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="Immunities" /> and the difference is what is being prevented:
    ///     this stops the <em>target acting</em>, and an immunity stops something <em>being applied to
    ///     the target</em>. Folding the two would make "silenced" and "immune to silence" the same
    ///     field.
    /// </remarks>
    public List<string> BlockedTags { get; set; } = [];

    /// <summary>Tag prefixes of effects that cannot be applied while this is active.</summary>
    public List<string> Immunities { get; set; } = [];

    /// <summary>Tag prefixes of gameplay events that end this — <c>Event.Damaged</c>, <c>Event.Moved</c>.</summary>
    public List<string> CancelOn { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var tag in Tags) {
            tags.Add(tag);
        }

        foreach (var tag in GrantedTags) {
            tags.Add(tag);
        }

        foreach (var tag in BlockedTags) {
            tags.Add(tag);
        }

        foreach (var tag in Immunities) {
            tags.Add(tag);
        }

        foreach (var tag in CancelOn) {
            tags.Add(tag);
        }
    }
}
