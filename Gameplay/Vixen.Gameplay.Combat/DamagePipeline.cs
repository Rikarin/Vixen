// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Combat;

/// <summary>The fixed sequence a hit passes through.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 § Combat's own list, and the order is the whole of the opinion:</b>
///         <c>Compute → Crit → Mitigate → Absorb → Apply → React</c>. A game inserts a rule at a
///         named point rather than replacing the pipeline — G-Q4, decided in favour of extensible,
///         because a wholesale replacement gets a game a pipeline with none of the tested edge cases
///         and no way back.
///     </para>
///     <para>
///         ⚠ <b>Absorb after mitigate is not arbitrary.</b> A shield that soaked the pre-mitigation
///         number would be worth several times its face value against an armoured target and nothing
///         against a naked one, which is not what anybody writing "absorbs 500 damage" means.
///     </para>
/// </remarks>
public enum DamageStage {
    /// <summary>What the ability is worth, before anything happens to it.</summary>
    Compute,

    /// <summary>Whether it crit, and what that multiplies it by.</summary>
    Crit,

    /// <summary>What armour, resistance and reductions take off.</summary>
    Mitigate,

    /// <summary>What shields soak, out of what is left.</summary>
    Absorb,

    /// <summary>What actually happens to the target's health.</summary>
    Apply,

    /// <summary>What the world does about it: threat, procs, interrupts, death.</summary>
    React
}

/// <summary>One hit, as it passes through the pipeline.</summary>
/// <remarks>
///     <para>
///         <b>A mutable struct passed by <c>ref</c>, because a raid is thousands of these a second</b>
///         and a class per hit is a garbage collection during a boss pull. Every stage reads what the
///         ones before it wrote.
///     </para>
///     <para>
///         ⚠ <b><see cref="Random" /> is seeded from <see cref="EventId" /> and nothing else.</b> The
///         same reason a loot roll is: a crit that cannot be recomputed from a number in the log is a
///         support ticket nobody can answer. It is also why the realm rolls it and the client does
///         not — doc 28 § Authority gives the client hit feedback and never the number.
///     </para>
/// </remarks>
public struct DamageEvent {
    /// <summary>Which ability caused it.</summary>
    public DefId Ability { get; init; }

    /// <summary>What caused it, as the caller numbers events. The one number it is reproducible from.</summary>
    public ulong EventId { get; init; }

    /// <summary>Who did it, or null for the world — a fall, a fire, a hazard.</summary>
    public GameplaySubject? Source { get; init; }

    /// <summary>Who it happened to.</summary>
    public GameplaySubject Target { get; init; }

    /// <summary>What kind of damage — <c>Damage.Fire</c>. One school per hit.</summary>
    public GameplayTag School { get; init; }

    /// <summary>Whether this heals rather than harms.</summary>
    public bool IsHealing { get; init; }

    /// <summary>How much threat it makes per point.</summary>
    public float ThreatMultiplier { get; init; }

    /// <summary>The stream every roll in the pipeline comes from.</summary>
    public GameplayRandom Random;

    /// <summary>What it is worth right now. Every stage before <see cref="DamageStage.Apply" /> edits this.</summary>
    public float Amount;

    /// <summary>Whether it crit.</summary>
    public bool IsCritical;

    /// <summary>How much mitigation took off.</summary>
    public float Mitigated;

    /// <summary>How much a shield soaked.</summary>
    public float Absorbed;

    /// <summary>How much actually landed.</summary>
    public float Applied;

    /// <summary>How much threat it made.</summary>
    public float Threat;

    /// <summary>Whether it killed the target.</summary>
    public bool Killed;

    /// <summary>Whether a rule stopped it. Every later stage is skipped.</summary>
    /// <remarks>
    ///     What an immunity, a miss and a phase transition all say. Cancelling in
    ///     <see cref="DamageStage.React" /> does nothing, because the damage has already landed —
    ///     which is why "prevent this hit" is a mitigate rule and not a react one.
    /// </remarks>
    public bool Cancelled;
}

/// <summary>One rule at one stage.</summary>
/// <remarks>
///     <b>What the shipped rules are, too.</b> Doc 28 G-R1: the built-ins are written <em>through</em>
///     the seams, so the extension point is the one the engine itself uses and therefore the one that
///     works. Everything in <c>DamageRules.cs</c> implements this and nothing else.
/// </remarks>
public interface IDamageRule {
    /// <summary>Which stage it runs in.</summary>
    DamageStage Stage { get; }

    /// <summary>Where it sits among the rules of that stage. Lower runs first.</summary>
    /// <remarks>
    ///     Explicit rather than registration order, because registration order is composition order
    ///     and a game must be able to put a rule before one of the built-ins without controlling when
    ///     the built-in was added.
    /// </remarks>
    int Order { get; }

    /// <summary>What it does.</summary>
    /// <param name="hit">The hit, as the stages before it left it.</param>
    void Apply(ref DamageEvent hit);
}

/// <summary>The six stages, and the rules a game put in them.</summary>
/// <remarks>
///     Built once at start-up and shared. Running a hit allocates nothing: the rules are an array per
///     stage and the event is a struct.
/// </remarks>
public sealed class DamagePipeline {
    readonly IDamageRule[][] stages = new IDamageRule[6][];

    /// <summary>Makes an empty pipeline. Nothing happens to a hit until rules are added.</summary>
    public DamagePipeline() {
        for (var stage = 0; stage < stages.Length; stage++) {
            stages[stage] = [];
        }
    }

    /// <summary>The pipeline every game starts from: the six shipped rules, in their stages.</summary>
    /// <param name="attributes">Which stats the rules read. Null uses <see cref="CombatAttributes.Default" />.</param>
    /// <returns>The pipeline.</returns>
    public static DamagePipeline Standard(CombatAttributes? attributes = null) {
        attributes ??= CombatAttributes.Default;

        return new DamagePipeline()
            .Add(new BaseDamageRule())
            .Add(new CriticalStrikeRule(attributes))
            .Add(new ResistanceRule(attributes))
            .Add(new ShieldAbsorbRule(attributes))
            .Add(new HealthRule(attributes))
            .Add(new ThreatRule());
    }

    /// <summary>Adds a rule.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The pipeline, so rules chain.</returns>
    public DamagePipeline Add(IDamageRule rule) {
        ArgumentNullException.ThrowIfNull(rule);

        var index = (int)rule.Stage;
        var existing = stages[index];
        var replacement = new IDamageRule[existing.Length + 1];

        existing.CopyTo(replacement, 0);
        replacement[^1] = rule;

        // Stable within an order, so two rules a game gave the same number run in the order it added
        // them — which is the only answer that is not arbitrary.
        Array.Sort(replacement, static (left, right) => left.Order.CompareTo(right.Order));

        stages[index] = replacement;

        return this;
    }

    /// <summary>The rules in one stage, in the order they run.</summary>
    /// <param name="stage">Which stage.</param>
    /// <returns>Them.</returns>
    public ReadOnlySpan<IDamageRule> Rules(DamageStage stage) => stages[(int)stage];

    /// <summary>Runs a hit through every stage.</summary>
    /// <param name="hit">The hit. Filled in as it goes.</param>
    /// <remarks>
    ///     ⚠ <b>A cancelled hit stops between stages, not between rules.</b> A rule that cancels has
    ///     said "this does not happen"; the other rules of its own stage still run, because they are
    ///     peers deciding the same question and one of them may be a rule about cancellation itself.
    ///     Nothing after the stage runs.
    /// </remarks>
    public void Run(ref DamageEvent hit) {
        foreach (var stage in stages) {
            foreach (var rule in stage) {
                rule.Apply(ref hit);
            }

            if (hit.Cancelled) {
                return;
            }
        }
    }
}
