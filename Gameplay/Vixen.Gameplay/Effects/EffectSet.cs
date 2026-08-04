// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Gameplay;

/// <summary>Which application of an effect this is. Unique within one <see cref="EffectSet" />.</summary>
/// <param name="Value">The number. Zero is <see cref="None" />.</param>
public readonly record struct EffectHandle(uint Value) {
    /// <summary>Not an effect. What a refused application returns.</summary>
    public static EffectHandle None => default;

    /// <summary>Whether an application happened.</summary>
    public bool IsSome => Value != 0;

    /// <inheritdoc />
    public override string ToString() =>
        Value == 0 ? "no effect" : string.Create(CultureInfo.InvariantCulture, $"effect #{Value}");
}

/// <summary>What happened to an effect, for whoever needs to react to it.</summary>
public enum EffectEventKind {
    /// <summary>A new instance started.</summary>
    Applied,

    /// <summary>An existing instance was refreshed, extended or stacked up.</summary>
    Restacked,

    /// <summary>An application was refused — by <see cref="EffectStacking.None" /> or by an immunity.</summary>
    Refused,

    /// <summary>A periodic effect came due.</summary>
    Period,

    /// <summary>The duration ran out.</summary>
    Expired,

    /// <summary>A gameplay event matched <see cref="EffectDefinition.CancelOn" />.</summary>
    Cancelled,

    /// <summary>Something took it off deliberately.</summary>
    Removed
}

/// <summary>One thing that happened to one effect.</summary>
/// <remarks>
///     <b>The kernel reports, it does not act.</b> What a periodic tick <em>does</em> — damage, a heal,
///     a resource drain — is the combat library's, because it needs a damage pipeline the kernel must
///     not contain. So a tick is an event in a list the caller passed in, and the caller decides.
/// </remarks>
/// <param name="Kind">What happened.</param>
/// <param name="Handle">Which instance, or <see cref="EffectHandle.None" /> for a refusal.</param>
/// <param name="Definition">Which effect.</param>
/// <param name="Stacks">How many stacks it had when this happened.</param>
/// <param name="Instigator">Whoever applied it, as the caller numbered them.</param>
public readonly record struct EffectEvent(
    EffectEventKind Kind,
    EffectHandle Handle,
    DefId Definition,
    int Stacks,
    ulong Instigator
);

/// <summary>One running effect on one target.</summary>
public struct ActiveEffect {
    /// <summary>Which application this is.</summary>
    public EffectHandle Handle { get; internal set; }

    /// <summary>What it is running.</summary>
    public EffectTemplate Template { get; internal set; }

    /// <summary>Whoever applied it, as the caller numbered them. Zero is nobody in particular.</summary>
    public ulong Instigator { get; internal set; }

    /// <summary>What its modifiers are removed by.</summary>
    public ModifierSource Source { get; internal set; }

    /// <summary>How long it has been running, in seconds.</summary>
    public float Elapsed { get; internal set; }

    /// <summary>
    ///     How long <em>this instance</em> lasts, which <see cref="EffectStacking.Extend" /> grows past
    ///     the template's.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Per instance rather than read off the template, and
    ///     <see cref="EffectStacking.Extend" /> is why.</b>
    ///     Extending by winding <see cref="Elapsed" /> backwards would look equivalent and is not: a
    ///     periodic effect's tick schedule is counted from elapsed time, so winding it back pays every
    ///     period between the new and old positions a second time. Growing the duration leaves the
    ///     schedule where it is and simply buys more of it.
    /// </remarks>
    public float Duration { get; internal set; }

    /// <summary>How many stacks. One for every policy but <see cref="EffectStacking.StackTo" />.</summary>
    public int Stacks { get; internal set; }

    /// <summary>How many periodic ticks it has already produced.</summary>
    public int TicksEmitted { get; internal set; }

    /// <summary>How much longer it has, or infinity.</summary>
    public readonly float Remaining =>
        Template.IsInfinite ? float.PositiveInfinity : MathF.Max(0f, Duration - Elapsed);
}

/// <summary>Every effect running on one thing, and the tags and stats they imply.</summary>
/// <remarks>
///     <para>
///         <b>The one place the eight kinds of timed thing meet.</b> A set owns the target's
///         <see cref="AttributeSet" /> and <see cref="GameplayTagSet" /> for the duration of an
///         effect: applying one stamps its modifiers with a source and grants its tags, and removing
///         one takes exactly those back off. Nothing else may add a modifier whose source is an
///         effect, which is what makes "the buff fell off and my power did not come back down"
///         impossible rather than unlikely.
///     </para>
///     <para>
///         <b>Refusals are reported, not swallowed.</b> An application blocked by
///         <see cref="EffectStacking.None" /> or by an immunity produces an
///         <see cref="EffectEventKind.Refused" /> event, because the caller nearly always has
///         something to say about it — a floating "immune", a wasted cooldown, an achievement.
///     </para>
/// </remarks>
public sealed class EffectSet {
    readonly List<ActiveEffect> active = [];
    uint next = 1;

    /// <summary>Makes a set over a target's stats and tags.</summary>
    /// <param name="attributes">The target's stats. Effects add and remove modifiers on it.</param>
    /// <param name="tags">The target's tags. Effects grant and revoke on it.</param>
    public EffectSet(AttributeSet attributes, GameplayTagSet tags) {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(tags);

        Attributes = attributes;
        Tags = tags;
    }

    /// <summary>The stats effects here modify.</summary>
    public AttributeSet Attributes { get; }

    /// <summary>The tags effects here grant.</summary>
    public GameplayTagSet Tags { get; }

    /// <summary>How many effects are running.</summary>
    public int Count => active.Count;

    /// <summary>Them, in the order they were applied.</summary>
    public ReadOnlySpan<ActiveEffect> Active => CollectionsMarshal.AsSpan(active);

    /// <summary>Applies an effect, honouring its stacking policy and the target's immunities.</summary>
    /// <param name="template">What to apply.</param>
    /// <param name="instigator">Whoever is applying it, as the caller numbers them.</param>
    /// <param name="events">Where to report what happened, or null.</param>
    /// <returns>The instance, or <see cref="EffectHandle.None" /> when it was refused.</returns>
    public EffectHandle Apply(EffectTemplate template, ulong instigator = 0, ICollection<EffectEvent>? events = null) {
        ArgumentNullException.ThrowIfNull(template);

        if (IsImmuneTo(template)) {
            events?.Add(new(EffectEventKind.Refused, EffectHandle.None, template.Id, 0, instigator));

            return EffectHandle.None;
        }

        var existing = template.Stacking == EffectStacking.Independent ? -1 : IndexOf(template.Id, instigator);

        if (existing >= 0) {
            return Restack(existing, instigator, events);
        }

        var handle = new EffectHandle(next++);

        var effect = new ActiveEffect {
            Handle = handle,
            Template = template,
            Instigator = instigator,
            Source = ModifierSource.From(template.Id, handle.Value),
            Duration = template.Duration,
            Stacks = 1
        };

        active.Add(effect);
        Grant(effect);
        ApplyModifiers(effect);

        events?.Add(new(EffectEventKind.Applied, handle, template.Id, 1, instigator));

        return handle;
    }

    /// <summary>Takes an effect off.</summary>
    /// <param name="handle">Which one.</param>
    /// <param name="events">Where to report what happened, or null.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(EffectHandle handle, ICollection<EffectEvent>? events = null) {
        for (var index = 0; index < active.Count; index++) {
            if (active[index].Handle == handle) {
                End(index, EffectEventKind.Removed, events);

                return true;
            }
        }

        return false;
    }

    /// <summary>Takes off every instance of one effect, whoever applied it.</summary>
    /// <param name="definition">Which effect.</param>
    /// <param name="events">Where to report what happened, or null.</param>
    /// <returns>How many came off.</returns>
    public int RemoveByDefinition(DefId definition, ICollection<EffectEvent>? events = null) {
        var removed = 0;

        for (var index = active.Count - 1; index >= 0; index--) {
            if (active[index].Template.Id == definition) {
                End(index, EffectEventKind.Removed, events);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Takes everything off.</summary>
    /// <param name="events">Where to report what happened, or null.</param>
    public void Clear(ICollection<EffectEvent>? events = null) {
        for (var index = active.Count - 1; index >= 0; index--) {
            End(index, EffectEventKind.Removed, events);
        }
    }

    /// <summary>Advances every effect by one step, emitting periodic ticks and expiring what is done.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <param name="events">Where to report what happened, or null.</param>
    /// <remarks>
    ///     ⚠ <b>Ticks are counted from elapsed time, not accumulated from a remainder.</b> The obvious
    ///     implementation — add the delta to a counter, fire while the counter exceeds the period,
    ///     subtract — loses a tick to rounding roughly one run in ten, so a six-second bleed with a
    ///     two-second period does five ticks' damage in some casts and six in others. Counting
    ///     <c>floor(elapsed / period)</c> against how many have already been emitted cannot drift, and
    ///     expiry pays out any tick the last partial step rounded away.
    /// </remarks>
    public void Tick(float delta, ICollection<EffectEvent>? events = null) {
        if (delta <= 0f) {
            return;
        }

        for (var index = active.Count - 1; index >= 0; index--) {
            var span = CollectionsMarshal.AsSpan(active);
            ref var effect = ref span[index];
            var template = effect.Template;

            effect.Elapsed += delta;

            var finished = !template.IsInfinite && effect.Elapsed >= effect.Duration;

            if (finished) {
                effect.Elapsed = effect.Duration;
            }

            if (template.Period > 0f) {
                // At expiry, what is owed is what the whole duration was worth — which is the number
                // a designer wrote the effect against — rather than what a float accumulation happened
                // to reach.
                var due = finished
                    ? (int)MathF.Floor((effect.Duration / template.Period) + 0.0001f)
                    : (int)MathF.Floor(effect.Elapsed / template.Period);

                while (effect.TicksEmitted < due) {
                    effect.TicksEmitted++;
                    events?.Add(
                        new(
                            EffectEventKind.Period,
                            effect.Handle,
                            template.Id,
                            effect.Stacks,
                            effect.Instigator
                        )
                    );
                }
            }

            if (finished) {
                End(index, EffectEventKind.Expired, events);
            }
        }
    }

    /// <summary>Tells the set something happened, and ends whatever said it should end on that.</summary>
    /// <param name="gameplayEvent">The event's tag — <c>Event.Damaged</c>.</param>
    /// <param name="events">Where to report what happened, or null.</param>
    /// <returns>How many effects it ended.</returns>
    public int Notify(GameplayTag gameplayEvent, ICollection<EffectEvent>? events = null) {
        if (!gameplayEvent.IsSome) {
            return 0;
        }

        var cancelled = 0;

        for (var index = active.Count - 1; index >= 0; index--) {
            foreach (var range in active[index].Template.CancelOn) {
                if (!range.Contains(gameplayEvent)) {
                    continue;
                }

                End(index, EffectEventKind.Cancelled, events);
                cancelled++;

                break;
            }
        }

        return cancelled;
    }

    /// <summary>Whether any running effect stops the target doing something.</summary>
    /// <param name="action">The action's tag — <c>Ability.Cast.Fireball</c>.</param>
    /// <returns>Whether it is blocked.</returns>
    public bool Blocks(GameplayTag action) {
        if (!action.IsSome) {
            return false;
        }

        foreach (var effect in CollectionsMarshal.AsSpan(active)) {
            foreach (var range in effect.Template.BlockedTags) {
                if (range.Contains(action)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether any running effect makes the target immune to this one.</summary>
    /// <param name="template">The effect that wants to be applied.</param>
    /// <returns>Whether it would be refused.</returns>
    public bool IsImmuneTo(EffectTemplate template) {
        ArgumentNullException.ThrowIfNull(template);

        foreach (var effect in CollectionsMarshal.AsSpan(active)) {
            if (effect.Template.Immunities.Length > 0 && template.MatchedBy(effect.Template.Immunities)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds a running effect.</summary>
    /// <param name="handle">Which one.</param>
    /// <param name="effect">It, or the default.</param>
    /// <returns>Whether it is running.</returns>
    public bool TryGet(EffectHandle handle, out ActiveEffect effect) {
        foreach (var candidate in CollectionsMarshal.AsSpan(active)) {
            if (candidate.Handle == handle) {
                effect = candidate;

                return true;
            }
        }

        effect = default;

        return false;
    }

    /// <summary>How many stacks of an effect are running, summed over every instance of it.</summary>
    /// <param name="definition">Which effect.</param>
    /// <returns>The count, or zero.</returns>
    public int StacksOf(DefId definition) {
        var stacks = 0;

        foreach (var effect in CollectionsMarshal.AsSpan(active)) {
            if (effect.Template.Id == definition) {
                stacks += effect.Stacks;
            }
        }

        return stacks;
    }

    EffectHandle Restack(int index, ulong instigator, ICollection<EffectEvent>? events) {
        var span = CollectionsMarshal.AsSpan(active);
        ref var effect = ref span[index];
        var template = effect.Template;

        switch (template.Stacking) {
            case EffectStacking.None:
                events?.Add(new(EffectEventKind.Refused, effect.Handle, template.Id, effect.Stacks, instigator));

                return EffectHandle.None;

            case EffectStacking.Refresh:
                effect.Elapsed = 0f;
                effect.Duration = template.Duration;
                effect.TicksEmitted = 0;

                break;

            case EffectStacking.Extend:
                // The instance's duration grows; its clock does not move. Winding the clock back
                // instead would re-pay every period between the new position and the old one.
                effect.Duration += template.Duration;

                break;

            case EffectStacking.StackTo:
                effect.Elapsed = 0f;
                effect.Duration = template.Duration;
                effect.TicksEmitted = 0;

                if (effect.Stacks < template.MaximumStacks) {
                    var source = effect.Source;
                    effect.Stacks++;
                    Attributes.RemoveBySource(source);
                    ApplyModifiers(effect);
                }

                break;

            default:
                break;
        }

        events?.Add(new(EffectEventKind.Restacked, effect.Handle, template.Id, effect.Stacks, instigator));

        return effect.Handle;
    }

    void End(int index, EffectEventKind kind, ICollection<EffectEvent>? events) {
        var effect = active[index];

        active.RemoveAt(index);
        Attributes.RemoveBySource(effect.Source);
        Revoke(effect);

        events?.Add(new(kind, effect.Handle, effect.Template.Id, effect.Stacks, effect.Instigator));
    }

    void ApplyModifiers(in ActiveEffect effect) {
        foreach (ref readonly var modifier in effect.Template.Modifiers) {
            Attributes.Add(modifier with { Value = modifier.Value * effect.Stacks, Source = effect.Source });
        }
    }

    void Grant(in ActiveEffect effect) {
        foreach (var tag in effect.Template.GrantedTags) {
            Tags.Add(tag);
        }
    }

    void Revoke(in ActiveEffect effect) {
        foreach (var tag in effect.Template.GrantedTags) {
            Tags.Remove(tag);
        }
    }

    int IndexOf(DefId definition, ulong instigator) {
        var span = CollectionsMarshal.AsSpan(active);

        for (var index = 0; index < span.Length; index++) {
            if (span[index].Template.Id == definition && span[index].Instigator == instigator) {
                return index;
            }
        }

        return -1;
    }
}
