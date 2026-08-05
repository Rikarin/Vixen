// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Gameplay.Items;

/// <summary>One copy of an item. Sixteen bytes, and that is the design rather than an outcome.</summary>
/// <remarks>
///     <para>
///         <b>A bank of ten thousand items is a real number.</b> Doc 28 § Items: an instance carrying
///         a materialised stat block is fifty times the memory for data that is a pure function of
///         the seed. So an instance is a definition, a stack count, a durability, a roll seed and a
///         bound state — and every affix, every stat and every tooltip line is recomputed from those
///         when somebody actually looks.
///     </para>
///     <para>
///         ⚠ <b>The seed is the item's identity as far as its affixes are concerned.</b> Two instances
///         with the same definition and the same seed roll identically, in every process, for ever —
///         which is what makes a trade window, a client's tooltip and a realm's damage calculation
///         agree without any of them sending a stat block.
///     </para>
///     <para>
///         ⚠ <b>What is <em>not</em> here is anything of variable size</b> — no socketed gems, no
///         custom name, no transmog override. Those are per-copy extras that most copies do not have,
///         and a side table kept by whatever owns the instance is where they belong; putting them
///         here would cost every one of the ten thousand.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ItemInstance {
    /// <summary>The empty slot. A definition of <see cref="DefId.None" /> is nothing at all.</summary>
    public static ItemInstance Empty => default;

    /// <summary>Which item this is a copy of.</summary>
    public DefId Definition { get; init; }

    /// <summary>What its affixes rolled from. Zero is an unrolled copy — a stack of ore.</summary>
    public uint Seed { get; init; }

    /// <summary>How many are in this stack. Never zero for a real instance.</summary>
    public ushort Stack { get; init; }

    /// <summary>How much wear is left. Meaningless when the definition is indestructible.</summary>
    public ushort Durability { get; init; }

    /// <summary>Whether it is still tradeable, and what will stop it being so.</summary>
    public ItemBinding Binding { get; init; }

    /// <summary>Whether this is a real item rather than an empty slot.</summary>
    public bool IsSome => Definition.IsSome && Stack > 0;

    /// <summary>Whether it can still change hands.</summary>
    public bool IsTradeable => Binding != ItemBinding.Bound;

    /// <summary>Makes a stack of something with no affixes to roll.</summary>
    /// <param name="definition">Which item.</param>
    /// <param name="stack">How many.</param>
    /// <returns>The instance.</returns>
    public static ItemInstance Of(DefId definition, int stack = 1) =>
        new() { Definition = definition, Stack = (ushort)Math.Clamp(stack, 0, ushort.MaxValue) };

    /// <summary>The same item with a different stack count.</summary>
    /// <param name="stack">How many. Zero produces <see cref="Empty" />.</param>
    /// <returns>The instance.</returns>
    public ItemInstance WithStack(int stack) =>
        stack <= 0 ? Empty : this with { Stack = (ushort)Math.Min(stack, ushort.MaxValue) };

    /// <summary>The same item, bound.</summary>
    /// <returns>The instance.</returns>
    public ItemInstance Bind() => Binding == ItemBinding.None ? this : this with { Binding = ItemBinding.Bound };

    /// <inheritdoc />
    public override string ToString() =>
        IsSome
            ? string.Create(CultureInfo.InvariantCulture, $"{Stack} × {Definition} (seed {Seed:x8})")
            : "empty";
}

/// <summary>One affix an instance rolled, and how well.</summary>
/// <param name="Affix">Which affix.</param>
/// <param name="Roll">
///     Where in its ranges it landed, as a fraction from zero to one. One roll per affix, not per
///     stat, so an affix with two stats rolls high on both or low on both.
/// </param>
public readonly record struct RolledAffix(DefId Affix, float Roll);
