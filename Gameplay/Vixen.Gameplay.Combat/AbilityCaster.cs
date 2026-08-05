// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Combat;

/// <summary>Why an ability did not start.</summary>
/// <remarks>
///     A reason rather than a boolean, for <c>ContainerFailure</c>'s reason: every one of these is
///     something a player is told, and a client that has to guess shows the wrong message at the
///     worst moment.
/// </remarks>
public enum AbilityFailure {
    /// <summary>It started.</summary>
    None = 0,

    /// <summary>This build has no such ability.</summary>
    Unknown,

    /// <summary>Something else is being cast.</summary>
    Casting,

    /// <summary>It has no charge ready.</summary>
    OnCooldown,

    /// <summary>The global cooldown is running.</summary>
    GlobalCooldown,

    /// <summary>An effect on the caster blocks it — a silence, a stun.</summary>
    Blocked,

    /// <summary>The caster does not satisfy its requirements.</summary>
    Requirements,

    /// <summary>The caster cannot pay for it.</summary>
    Resources,

    /// <summary>It needs something selected and nothing is.</summary>
    NoTarget,

    /// <summary>What is selected is too far away.</summary>
    OutOfRange
}

/// <summary>What an ability was aimed at, as whatever owns the world resolved it.</summary>
/// <param name="Subject">The thing aimed at, or null for a point or a shape.</param>
/// <param name="Id">Its number, as the caller numbers things.</param>
/// <param name="Distance">How far away it is, in metres.</param>
/// <remarks>
///     ⚠ <b>The caller supplies the distance and this library never asks where anything is.</b>
///     Positions are <c>Vixen.Engine</c>'s, and a combat library that needed a scene could not be
///     tested without one, could not run in a headless simulation, and would drag a renderer's
///     dependency into a realm. What it validates is the <em>rule</em> — that a targeted ability has
///     a target and that the number the caller gave is inside the ability's range.
/// </remarks>
public readonly record struct AbilityTarget(GameplaySubject? Subject, ulong Id, float Distance) {
    /// <summary>Nothing selected.</summary>
    public static AbilityTarget None => default;

    /// <summary>Whether anything is selected.</summary>
    public bool IsSome => Subject is not null || Id != 0;
}

/// <summary>What happened to an ability.</summary>
public enum AbilityEventKind {
    /// <summary>A cast or a channel began.</summary>
    Started,

    /// <summary>An instant ability went off, or a cast finished.</summary>
    Completed,

    /// <summary>A channel came due.</summary>
    Ticked,

    /// <summary>Something stopped it before it finished.</summary>
    Interrupted,

    /// <summary>It would not start.</summary>
    Failed
}

/// <summary>One thing that happened to one ability.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Ability">Which ability.</param>
/// <param name="Target">What it was aimed at.</param>
/// <param name="Failure">Why not, for <see cref="AbilityEventKind.Failed" />.</param>
public readonly record struct AbilityEvent(
    AbilityEventKind Kind,
    DefId Ability,
    AbilityTarget Target,
    AbilityFailure Failure
);

/// <summary>One thing's abilities: what is ready, what is being cast, and what it costs.</summary>
/// <remarks>
///     <para>
///         <b>Timing and eligibility, and nothing about damage.</b> What an ability <em>does</em> when
///         it completes is <see cref="CombatResolver" />'s, because that needs the targets it hit and
///         this only knows the one it was aimed at. The split is what lets a cone ability go through
///         the same caster as a single-target one.
///     </para>
///     <para>
///         ⚠ <b>Costs are paid when the cast <em>completes</em>, not when it starts.</b> Paying up
///         front means an interrupted cast has spent the resource, which every game refunds by hand
///         and gets wrong for channels. A channel pays per tick, which is the only reading of "it
///         costs mana per second" that survives being interrupted halfway.
///     </para>
/// </remarks>
public sealed class AbilityCaster {
    readonly Dictionary<uint, Cooldown> cooldowns = [];

    AbilityTemplate? casting;
    AbilityTarget target;
    float remaining;
    int ticks;

    /// <summary>Makes a caster.</summary>
    /// <param name="subject">Whose abilities these are.</param>
    /// <param name="abilities">Where ability templates come from.</param>
    public AbilityCaster(GameplaySubject subject, AbilityLibrary abilities) {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(abilities);

        Subject = subject;
        Abilities = abilities;
    }

    /// <summary>Whose abilities these are.</summary>
    public GameplaySubject Subject { get; }

    /// <summary>Where ability templates come from.</summary>
    public AbilityLibrary Abilities { get; }

    /// <summary>How long the global cooldown lasts, in seconds.</summary>
    public float GlobalCooldown { get; set; } = 1.5f;

    /// <summary>How much of it is left.</summary>
    public float GlobalCooldownRemaining { get; private set; }

    /// <summary>What is being cast, or null.</summary>
    public AbilityTemplate? Casting => casting;

    /// <summary>What it is aimed at.</summary>
    public AbilityTarget CastTarget => target;

    /// <summary>How much longer the cast or channel has, in seconds.</summary>
    public float CastRemaining => remaining;

    /// <summary>Whether anything is being cast.</summary>
    public bool IsCasting => casting is not null;

    /// <summary>Whether an ability could start right now.</summary>
    /// <param name="ability">Which one.</param>
    /// <param name="at">What it would be aimed at.</param>
    /// <returns>Why not, or <see cref="AbilityFailure.None" />.</returns>
    /// <remarks>
    ///     <b>The same check <see cref="TryBegin" /> runs, exposed so a client can grey out a button
    ///     with the reason the realm would give.</b> Doc 28 § Requirements' point, one level up: two
    ///     implementations of "can I do this" is how a player learns to spam a button that says no.
    /// </remarks>
    public AbilityFailure CanBegin(DefId ability, in AbilityTarget at) {
        if (Abilities.Find(ability) is not { } template) {
            return AbilityFailure.Unknown;
        }

        if (IsCasting) {
            return AbilityFailure.Casting;
        }

        // ⚠ The order of the remaining checks is the order a client shows them in, so it runs from
        // the longest-lived reason to the shortest. Several are usually true at once — a silenced
        // player who just pressed something is silenced *and* on the global cooldown — and "you are
        // silenced" is the one still true in a second. Sorting them the other way round produces a
        // button that blames the global cooldown for four seconds of silence.
        foreach (var tag in template.Tags) {
            if (Subject.Effects.Blocks(tag)) {
                return AbilityFailure.Blocked;
            }
        }

        if (!template.Requirements.IsMetBy(Subject)) {
            return AbilityFailure.Requirements;
        }

        if (ChargesOf(ability) <= 0) {
            return AbilityFailure.OnCooldown;
        }

        if (template.RespectsGlobalCooldown && GlobalCooldownRemaining > 0f) {
            return AbilityFailure.GlobalCooldown;
        }

        if (template.NeedsTarget && !at.IsSome) {
            return AbilityFailure.NoTarget;
        }

        if (template.Range > 0f && at.IsSome && at.Distance > template.Range) {
            return AbilityFailure.OutOfRange;
        }

        return CanAfford(template) ? AbilityFailure.None : AbilityFailure.Resources;
    }

    /// <summary>Starts an ability.</summary>
    /// <param name="ability">Which one.</param>
    /// <param name="at">What it is aimed at.</param>
    /// <param name="events">Where to report what happened, or null.</param>
    /// <returns>Why not, or <see cref="AbilityFailure.None" />.</returns>
    /// <remarks>
    ///     An instant ability completes inside this call and reports both
    ///     <see cref="AbilityEventKind.Started" /> and <see cref="AbilityEventKind.Completed" />,
    ///     because a caller that special-cased instants would be a caller with two code paths for one
    ///     thing.
    /// </remarks>
    public AbilityFailure TryBegin(DefId ability, in AbilityTarget at, ICollection<AbilityEvent>? events = null) {
        var failure = CanBegin(ability, at);

        if (failure != AbilityFailure.None) {
            events?.Add(new(AbilityEventKind.Failed, ability, at, failure));

            return failure;
        }

        var template = Abilities.Get(ability);

        Spend(ability);
        events?.Add(new(AbilityEventKind.Started, ability, at, AbilityFailure.None));

        if (template.TriggersGlobalCooldown) {
            GlobalCooldownRemaining = MathF.Max(GlobalCooldownRemaining, GlobalCooldown);
        }

        if (template.CastTime <= 0f && !template.IsChannel) {
            Pay(template);
            events?.Add(new(AbilityEventKind.Completed, ability, at, AbilityFailure.None));

            return AbilityFailure.None;
        }

        casting = template;
        target = at;
        remaining = template.IsChannel ? template.ChannelTime : template.CastTime;
        ticks = 0;

        return AbilityFailure.None;
    }

    /// <summary>Stops whatever is being cast.</summary>
    /// <param name="events">Where to report what happened, or null.</param>
    /// <returns>Whether anything was.</returns>
    /// <remarks>
    ///     ⚠ <b>An interrupted cast refunds nothing, because it paid nothing.</b> That is the whole
    ///     reason costs are taken at completion.
    /// </remarks>
    public bool Interrupt(ICollection<AbilityEvent>? events = null) {
        if (casting is not { } template) {
            return false;
        }

        events?.Add(new(AbilityEventKind.Interrupted, template.Id, target, AbilityFailure.None));
        Stop();

        return true;
    }

    /// <summary>Advances the cast, the channel, the global cooldown and every recharging ability.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <param name="events">Where to report what happened, or null.</param>
    public void Tick(float delta, ICollection<AbilityEvent>? events = null) {
        if (delta <= 0f) {
            return;
        }

        GlobalCooldownRemaining = MathF.Max(0f, GlobalCooldownRemaining - delta);

        foreach (var id in cooldowns.Keys.ToArray()) {
            var state = cooldowns[id];

            if (state.Charges >= state.Maximum) {
                continue;
            }

            state.Remaining -= delta;

            while (state.Remaining <= 0f && state.Charges < state.Maximum) {
                state.Charges++;
                state.Remaining += state.Recharge;
            }

            if (state.Charges >= state.Maximum) {
                state.Remaining = 0f;
            }

            cooldowns[id] = state;
        }

        if (casting is not { } template) {
            return;
        }

        // ⚠ Blocked *while* casting ends the cast. A silence that only stopped new casts would let a
        // three-second cast finish after the caster was silenced two seconds into it.
        foreach (var tag in template.Tags) {
            if (Subject.Effects.Blocks(tag)) {
                Interrupt(events);

                return;
            }
        }

        remaining -= delta;

        if (template.IsChannel) {
            // Counted from elapsed time against ticks already emitted, for EffectSet's reason: an
            // accumulate-and-subtract channel loses a tick to rounding often enough to matter.
            var elapsed = template.ChannelTime - remaining;
            var due = Math.Min(
                (int)MathF.Floor((elapsed / template.ChannelPeriod) + 0.0001f),
                (int)MathF.Floor((template.ChannelTime / template.ChannelPeriod) + 0.0001f)
            );

            while (ticks < due) {
                ticks++;

                if (!CanAfford(template)) {
                    Interrupt(events);

                    return;
                }

                Pay(template);
                events?.Add(new(AbilityEventKind.Ticked, template.Id, target, AbilityFailure.None));
            }

            if (remaining > 0f) {
                return;
            }

            events?.Add(new(AbilityEventKind.Completed, template.Id, target, AbilityFailure.None));
            Stop();

            return;
        }

        if (remaining > 0f) {
            return;
        }

        if (!CanAfford(template)) {
            Interrupt(events);

            return;
        }

        Pay(template);
        events?.Add(new(AbilityEventKind.Completed, template.Id, target, AbilityFailure.None));
        Stop();
    }

    /// <summary>How many charges of an ability are ready.</summary>
    /// <param name="ability">Which one.</param>
    /// <returns>The count.</returns>
    public int ChargesOf(DefId ability) {
        if (cooldowns.TryGetValue(ability.Value, out var state)) {
            return state.Charges;
        }

        return Abilities.Find(ability)?.Charges ?? 0;
    }

    /// <summary>How long until an ability's next charge, in seconds.</summary>
    /// <param name="ability">Which one.</param>
    /// <returns>The time, or zero when one is ready.</returns>
    public float CooldownOf(DefId ability) =>
        cooldowns.TryGetValue(ability.Value, out var state) && state.Charges < state.Maximum ? state.Remaining : 0f;

    /// <summary>Puts every charge back and clears the global cooldown. What a reset does.</summary>
    public void ResetCooldowns() {
        cooldowns.Clear();
        GlobalCooldownRemaining = 0f;
    }

    bool CanAfford(AbilityTemplate template) {
        foreach (ref readonly var cost in template.Costs) {
            if (Subject.Attributes.ValueOf(cost.Attribute) < cost.Amount) {
                return false;
            }
        }

        return true;
    }

    void Pay(AbilityTemplate template) {
        foreach (ref readonly var cost in template.Costs) {
            Subject.Attributes.SetBase(
                cost.Attribute,
                Subject.Attributes.BaseOf(cost.Attribute) - cost.Amount
            );
        }
    }

    void Spend(DefId ability) {
        var template = Abilities.Get(ability);

        // ⚠ An ability with no cooldown has nothing to spend, and tracking it anyway would leave it
        // at zero charges recharging over zero seconds — which is "ready again next tick" rather
        // than "ready now", and reads to a player as a filler spell that stutters. What limits an
        // ability with no cooldown is the global cooldown, and that is checked separately.
        if (template.Cooldown <= 0f) {
            return;
        }

        if (!cooldowns.TryGetValue(ability.Value, out var state)) {
            state = new() { Charges = template.Charges, Maximum = template.Charges, Recharge = template.Cooldown };
        }

        state.Charges--;

        if (state.Charges < state.Maximum && state.Remaining <= 0f) {
            state.Remaining = state.Recharge;
        }

        cooldowns[ability.Value] = state;
    }

    void Stop() {
        casting = null;
        target = AbilityTarget.None;
        remaining = 0f;
        ticks = 0;
    }

    struct Cooldown {
        public int Charges;
        public int Maximum;
        public float Recharge;
        public float Remaining;
    }
}
