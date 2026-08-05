// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay;

/// <summary>The name of a stat — <c>Power</c>, <c>Health</c>, <c>MoveSpeed</c>, <c>CritChance</c>.</summary>
/// <remarks>
///     <para>
///         <b>Every stat in every gameplay library is one type</b>, which is doc 28 § Attributes'
///         whole point: a weapon's power, a character's health, a mount's speed, a crafting station's
///         quality bonus and a guild perk's reputation multiplier are the same kind of number, so
///         they share one modifier algebra, one replication path and one inspector instead of five.
///     </para>
///     <para>
///         The value is <see cref="Symbol" />'s hash of the name, so an id is derivable from a string
///         in any process without a table, and <see cref="ToString" /> can still say <c>Power</c>
///         because interning keeps the spelling for diagnostics. A collision is refused by
///         <see cref="AttributeLayoutBuilder" />, which is the one place two attribute names can meet.
///     </para>
/// </remarks>
/// <param name="Value">The hash of the name. Zero is <see cref="None" />.</param>
public readonly record struct AttributeId(uint Value) {
    /// <summary>Not a stat.</summary>
    public static AttributeId None => default;

    /// <summary>Whether this names one.</summary>
    public bool IsSome => Value != 0;

    /// <summary>The id a name hashes to.</summary>
    /// <param name="name">The stat's name — <c>Power</c>.</param>
    /// <returns>Its id.</returns>
    public static AttributeId From(string? name) => new(Symbol.Intern(name).Id);

    /// <summary>The interned name, for diagnostics.</summary>
    public Symbol Name => new(Value);

    /// <inheritdoc />
    public override string ToString() => Value == 0 ? "no attribute" : Name.ToString();
}

/// <summary>What a modifier does to a stat, and therefore which stage of the evaluation it lands in.</summary>
/// <remarks>
///     <para>
///         <b>Three buckets, and the fixed order between them is the feature</b> — doc 28
///         § Attributes. Every game that leaves the order open gets a balance team arguing about
///         whether two 50 % buffs are 100 % or 125 %, in different answers per ability, for ever.
///         Additive percentages sum; multiplicative ones compose; a designer picks a bucket and the
///         arithmetic is never in question again.
///     </para>
///     <para>
///         ⚠ <b>There is no <c>Override</c>, deliberately.</b> An operation that replaces the value
///         needs a rule for what happens when two of them are active, and every such rule
///         ("strongest wins", "last wins", "first wins") is exactly the argument the fixed order
///         exists to end. A polymorph that fixes movement speed is expressed as a large
///         <see cref="MultiplyPercent" /> and a clamp on the layout, which composes with everything
///         else instead of silently deleting it. Doc 28 G-Q6 is where this is recorded.
///     </para>
/// </remarks>
public enum ModifierOp {
    /// <summary>A flat amount, summed with the other flat amounts. <c>+120 Power</c>.</summary>
    Add,

    /// <summary>A percentage that sums with the other additive percentages. Two 50 % are 100 %.</summary>
    AddPercent,

    /// <summary>A percentage that composes with the other multiplicative ones. Two 50 % are 125 %.</summary>
    MultiplyPercent
}

/// <summary>Whatever granted a modifier, so that removing it is exact.</summary>
/// <remarks>
///     <b>Removal is by source and never by subtracting the value back off</b>, which is what stops a
///     stat drifting a fraction every time a buff cycles. Anything can be a source — an effect, a
///     piece of equipment, a talent, a guild perk — and the only requirement is that the same thing
///     produces the same value for as long as its modifiers are applied.
/// </remarks>
/// <param name="Value">The opaque handle. Zero is <see cref="None" />, which means "nothing owns this".</param>
public readonly record struct ModifierSource(ulong Value) {
    /// <summary>Nothing owns it. Removable only by <c>Clear</c>.</summary>
    public static ModifierSource None => default;

    /// <summary>Whether something owns it.</summary>
    public bool IsSome => Value != 0;

    /// <summary>A source naming one instance of one definition.</summary>
    /// <param name="definition">What granted it.</param>
    /// <param name="instance">Which of possibly several instances of that definition.</param>
    /// <returns>The source.</returns>
    /// <remarks>
    ///     The instance number is what keeps two stacks of the same buff apart. Without it, the second
    ///     application of a stacking effect removes the first's modifiers when it expires.
    /// </remarks>
    public static ModifierSource From(DefId definition, uint instance) =>
        new(((ulong)definition.Value << 32) | instance);

    /// <inheritdoc />
    public override string ToString() => Value == 0 ? "unowned" : $"source {Value:x16}";
}

/// <summary>One thing acting on one stat, from one source.</summary>
/// <param name="Attribute">Which stat.</param>
/// <param name="Op">Which bucket, and therefore which stage of the evaluation.</param>
/// <param name="Value">How much. A percentage is a fraction: <c>0.15f</c> is 15 %.</param>
/// <param name="Source">What granted it, so it can be taken away again exactly.</param>
public readonly record struct Modifier(
    AttributeId Attribute,
    ModifierOp Op,
    float Value,
    ModifierSource Source
);
