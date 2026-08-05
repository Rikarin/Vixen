// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>One thing's gameplay state: its stats, its tags and the effects running on it.</summary>
/// <remarks>
///     <para>
///         <b>The three pieces always travel together, so they are one object.</b> An effect needs the
///         stats to modify and the tags to grant; a requirement needs the stats to compare and the
///         tags to test; a rule needs all three. Every gameplay library above the kernel takes one of
///         these rather than three parameters, which is what stops half of them taking them in a
///         different order.
///     </para>
///     <para>
///         <b>Not a component.</b> It holds three growable collections, and an archetype ECS stores
///         columns of fixed-size rows — so what goes on an entity is a handle into whatever owns
///         these, exactly as <c>AiAgent</c> holds a memory handle rather than a blackboard. Which
///         component that is belongs to the library that needs it first, not to the kernel.
///     </para>
/// </remarks>
public sealed class GameplaySubject : IRequirementContext {
    /// <summary>Makes one over a stat layout.</summary>
    /// <param name="layout">Which stats it has.</param>
    public GameplaySubject(AttributeLayout layout) {
        Attributes = new(layout);
        Tags = new();
        Effects = new(Attributes, Tags);
    }

    /// <summary>Its stats.</summary>
    public AttributeSet Attributes { get; }

    /// <summary>Its tags — granted by effects, by equipment, by state, by anything.</summary>
    public GameplayTagSet Tags { get; }

    /// <summary>The effects running on it.</summary>
    public EffectSet Effects { get; }

    /// <inheritdoc />
    GameplayTagSet? IRequirementContext.Tags => Tags;

    /// <inheritdoc />
    /// <remarks>
    ///     Stats only. A currency, a reputation or a quest counter is a number that lives in a
    ///     durable row rather than in an <see cref="AttributeSet" />, and the library that owns each
    ///     of those supplies its own context — which is why
    ///     <see cref="IRequirementContext.TryGetValue" /> is an interface method and not a lookup in
    ///     a dictionary the kernel keeps.
    /// </remarks>
    public bool TryGetValue(AttributeId subject, out float value) {
        if (Attributes.Layout.Declares(subject)) {
            value = Attributes.ValueOf(subject);

            return true;
        }

        value = 0f;

        return false;
    }

    /// <summary>Advances the effects and recomputes whatever they changed.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <param name="events">Where to report what the effects did, or null.</param>
    /// <returns>How many stats were recomputed.</returns>
    /// <remarks>
    ///     The order matters and is the reason this is one call: effects expire first, then the stats
    ///     they were holding up are recomputed. The other order shows a player one frame of a stat
    ///     that a buff which has already fallen off was still inflating.
    /// </remarks>
    public int Tick(float delta, ICollection<EffectEvent>? events = null) {
        Effects.Tick(delta, events);

        return Attributes.Recompute();
    }
}
