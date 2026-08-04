// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay;

/// <summary>What happens to a stat's value after it has been clamped.</summary>
public enum AttributeRounding {
    /// <summary>Nothing. The value is whatever the arithmetic produced.</summary>
    None,

    /// <summary>To the nearest whole number, halves away from zero — what a damage number wants.</summary>
    Nearest,

    /// <summary>Towards negative infinity.</summary>
    Down,

    /// <summary>Towards positive infinity.</summary>
    Up
}

/// <summary>One stat's declaration: what it starts at, what it may not leave, and how it is rounded.</summary>
/// <param name="Attribute">Which stat.</param>
/// <param name="Default">What a freshly made <see cref="AttributeSet" /> has as its base.</param>
/// <param name="Minimum">The floor applied after the arithmetic.</param>
/// <param name="Maximum">The ceiling applied after the arithmetic.</param>
/// <param name="Rounding">What is done to the clamped result.</param>
/// <remarks>
///     <b>The clamp is on the stat and not on the modifier</b>, which is the arrangement that makes
///     "health cannot go below zero" and "crit chance cannot exceed one" true no matter what applied
///     them. A clamp per modifier would be a rule each caller has to remember, and the one that
///     forgets is the one that ships.
/// </remarks>
public readonly record struct AttributeSchema(
    AttributeId Attribute,
    float Default = 0f,
    float Minimum = float.NegativeInfinity,
    float Maximum = float.PositiveInfinity,
    AttributeRounding Rounding = AttributeRounding.None
);

/// <summary>The compiled stat table: names resolved to slots, slots to schemas.</summary>
/// <remarks>
///     <para>
///         Shared and immutable, the way <c>BlackboardLayout</c> is, and for the same reason: ten
///         thousand characters with the same stats should hold ten thousand small arrays and one
///         table, not ten thousand dictionaries of names.
///     </para>
///     <para>
///         A slot is an index into <see cref="AttributeSet" />'s arrays. It is layout-local and never
///         persisted or sent — <see cref="AttributeId" /> is the durable name, exactly as
///         <see cref="Symbol" /> is for a tag.
///     </para>
/// </remarks>
public sealed class AttributeLayout {
    readonly AttributeSchema[] schemas;
    readonly Dictionary<uint, int> slots;

    internal AttributeLayout(AttributeSchema[] schemas) {
        this.schemas = schemas;
        slots = new(schemas.Length);

        for (var index = 0; index < schemas.Length; index++) {
            slots[schemas[index].Attribute.Value] = index;
        }
    }

    /// <summary>A layout with no stats in it.</summary>
    public static AttributeLayout Empty { get; } = new AttributeLayoutBuilder().Build();

    /// <summary>How many stats it declares.</summary>
    public int Count => schemas.Length;

    /// <summary>The declarations, in the order they were added.</summary>
    public ReadOnlySpan<AttributeSchema> Schemas => schemas;

    /// <summary>Where a stat lives in an <see cref="AttributeSet" />'s arrays.</summary>
    /// <param name="attribute">The stat.</param>
    /// <returns>Its slot, or −1 when the layout does not declare it.</returns>
    public int SlotOf(AttributeId attribute) => slots.TryGetValue(attribute.Value, out var slot) ? slot : -1;

    /// <summary>Whether the layout declares a stat.</summary>
    /// <param name="attribute">The stat.</param>
    /// <returns>Whether it does.</returns>
    public bool Declares(AttributeId attribute) => slots.ContainsKey(attribute.Value);

    /// <summary>The declaration in a slot.</summary>
    /// <param name="slot">The slot, as <see cref="SlotOf" /> returned it.</param>
    /// <returns>The schema.</returns>
    public ref readonly AttributeSchema this[int slot] => ref schemas[slot];
}

/// <summary>Composes an <see cref="AttributeLayout" /> out of the stats a game declares.</summary>
public sealed class AttributeLayoutBuilder {
    readonly List<AttributeSchema> schemas = [];
    readonly Dictionary<uint, string> interned = [];

    /// <summary>How many stats have been declared.</summary>
    public int Count => schemas.Count;

    /// <summary>Declares a stat.</summary>
    /// <param name="name">Its name — <c>Power</c>.</param>
    /// <param name="default">What a fresh set starts at.</param>
    /// <param name="minimum">The floor.</param>
    /// <param name="maximum">The ceiling.</param>
    /// <param name="rounding">What happens after the clamp.</param>
    /// <returns>The builder, so declarations chain.</returns>
    /// <exception cref="InvalidOperationException">The stat is declared twice, or its name collides with another's.</exception>
    public AttributeLayoutBuilder Add(
        string name,
        float @default = 0f,
        float minimum = float.NegativeInfinity,
        float maximum = float.PositiveInfinity,
        AttributeRounding rounding = AttributeRounding.None
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var attribute = AttributeId.From(name);

        if (!interned.TryAdd(attribute.Value, name)) {
            throw new InvalidOperationException(
                string.Equals(interned[attribute.Value], name, StringComparison.Ordinal)
                    ? $"'{name}' is declared twice."
                    : $"'{name}' and '{interned[attribute.Value]}' hash to the same attribute id. Rename "
                    + "one — two stats nothing can tell apart are one stat with two names."
            );
        }

        if (minimum > maximum) {
            throw new InvalidOperationException(
                $"'{name}' has a minimum of {minimum} above its maximum of {maximum}, so no value "
                + "satisfies it."
            );
        }

        schemas.Add(new(attribute, @default, minimum, maximum, rounding));

        return this;
    }

    /// <summary>Produces the layout.</summary>
    /// <returns>The layout.</returns>
    public AttributeLayout Build() => new([.. schemas]);
}
