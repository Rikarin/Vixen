// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Gameplay.Items;

namespace Vixen.Gameplay.Inventory;

/// <summary>Which container. A bag, an equipment set, a bank tab, a trade window's side.</summary>
/// <param name="Value">The hash of its name. Zero is <see cref="None" />.</param>
/// <remarks>
///     A hash of a name for <see cref="Symbol" />'s reason: a container id is exchanged between a
///     client and a realm and stored in a durable row, so it has to be the same number in every
///     process without a table to agree on. <c>bags/0</c>, <c>equipment</c>, <c>bank/3</c>.
/// </remarks>
public readonly record struct ContainerId(uint Value) {
    /// <summary>Not a container.</summary>
    public static ContainerId None => default;

    /// <summary>Whether this names one.</summary>
    public bool IsSome => Value != 0;

    /// <summary>The id a name hashes to.</summary>
    /// <param name="name">The container's name — <c>bags/0</c>.</param>
    /// <returns>Its id.</returns>
    public static ContainerId From(string? name) => new(Symbol.Intern(name).Id);

    /// <inheritdoc />
    public override string ToString() => Value == 0 ? "no container" : new Symbol(Value).ToString();
}

/// <summary>One slot of one container: the coordinate every operation is written in.</summary>
/// <param name="Container">Which container.</param>
/// <param name="Slot">Which slot in it, counting from zero.</param>
public readonly record struct SlotRef(ContainerId Container, int Slot) {
    /// <summary>Nowhere.</summary>
    public static SlotRef None => new(ContainerId.None, -1);

    /// <summary>Whether it names a slot at all.</summary>
    public bool IsSome => Container.IsSome && Slot >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        IsSome ? string.Create(CultureInfo.InvariantCulture, $"{Container}[{Slot}]") : "nowhere";
}

/// <summary>What a container will and will not take, and what it does to what it takes.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 § Inventory's claim, as data.</b> Bags, equipment slots, bank tabs, guild bank
///         tabs, mail attachments, trade windows, vendor buyback and loot windows are one container
///         type with different policies — so there is one set of stacking rules, one set of capacity
///         rules and one place a duplication bug could be.
///     </para>
/// </remarks>
public sealed record ContainerPolicy {
    /// <summary>Takes anything, stacks, keeps bound items, binds nothing, writable.</summary>
    public static ContainerPolicy Default { get; } = new();

    /// <summary>What may go in, as a tag query over the item's own tags.</summary>
    public GameplayTagQuery Accepts { get; init; } = GameplayTagQuery.Always;

    /// <summary>Whether two compatible instances merge into one slot.</summary>
    /// <remarks>
    ///     Off for an equipment set and a trade window, where a slot means one thing and merging
    ///     would make "which of these am I offering" unanswerable.
    /// </remarks>
    public bool AllowsStacking { get; init; } = true;

    /// <summary>Whether an already-bound item may be put in.</summary>
    /// <remarks>
    ///     Off for a trade window and a mail attachment, which is the whole mechanism binding exists
    ///     for. A bag has it on, or a player could not carry what they had bound.
    /// </remarks>
    public bool AllowsBound { get; init; } = true;

    /// <summary>Which binding trigger arriving here fires.</summary>
    /// <remarks>
    ///     ⚠ <b>The trigger rather than a boolean, because a bag and an equipment set fire different
    ///     ones.</b> A bag sets <see cref="ItemBinding.OnPickup" /> and an equipment set sets
    ///     <see cref="ItemBinding.OnEquip" />; an item binds when its own policy is the one this
    ///     container fires, and is untouched otherwise. A single "binds on insert" flag would bind a
    ///     bind-on-equip sword the moment it was looted, which is the difference between an item a
    ///     player can sell and one they cannot.
    /// </remarks>
    public ItemBinding BindsOn { get; init; } = ItemBinding.None;

    /// <summary>Whether nothing may change. A vendor's stock list, a loot window after it is taken.</summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>A fixed number of slots, a policy, and — for an equipment set — a tag per slot.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The slots array is not exposed for writing, and that is the point of the type.</b>
///         Everything that changes a container goes through
///         <see cref="ContainerSet.Apply(ContainerTransaction)" />, because a mutation that is not
///         part of a transaction is a mutation that cannot be rolled back — and a half-applied move
///         is exactly how an item ends up in two places or in none.
///     </para>
/// </remarks>
public sealed class Container {
    readonly ItemInstance[] slots;
    readonly GameplayTag[]? slotTags;

    /// <summary>Makes a container.</summary>
    /// <param name="id">What it is called.</param>
    /// <param name="capacity">How many slots.</param>
    /// <param name="policy">What it takes, or null for <see cref="ContainerPolicy.Default" />.</param>
    /// <param name="slotTags">
    ///     One tag per slot, for an equipment set — an item may only go in a slot whose tag is its
    ///     own. Null means every slot takes anything the policy allows.
    /// </param>
    public Container(ContainerId id, int capacity, ContainerPolicy? policy = null, GameplayTag[]? slotTags = null) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        if (slotTags is not null && slotTags.Length != capacity) {
            throw new ArgumentException(
                $"{id} has {capacity} slots and {slotTags.Length} slot tags. An equipment set needs one "
                + "tag per slot, because the tag is what says which slot an item goes in.",
                nameof(slotTags)
            );
        }

        Id = id;
        slots = new ItemInstance[capacity];
        Policy = policy ?? ContainerPolicy.Default;
        this.slotTags = slotTags;
    }

    /// <summary>What it is called.</summary>
    public ContainerId Id { get; }

    /// <summary>How many slots it has.</summary>
    public int Capacity => slots.Length;

    /// <summary>What it takes.</summary>
    public ContainerPolicy Policy { get; }

    /// <summary>Whether it is an equipment set — one tag per slot.</summary>
    public bool IsSlotted => slotTags is not null;

    /// <summary>What is in it.</summary>
    public ReadOnlySpan<ItemInstance> Slots => slots;

    /// <summary>What is in one slot.</summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>The instance, or <see cref="ItemInstance.Empty" />.</returns>
    public ItemInstance this[int slot] =>
        slot >= 0 && slot < slots.Length ? slots[slot] : ItemInstance.Empty;

    /// <summary>Which slot tag a slot demands.</summary>
    /// <param name="slot">Which slot.</param>
    /// <returns>The tag, or <see cref="GameplayTag.None" /> when the container is not slotted.</returns>
    public GameplayTag SlotTag(int slot) =>
        slotTags is not null && slot >= 0 && slot < slotTags.Length ? slotTags[slot] : GameplayTag.None;

    /// <summary>How many slots hold nothing.</summary>
    public int FreeSlots {
        get {
            var free = 0;

            foreach (ref readonly var instance in slots.AsSpan()) {
                if (!instance.IsSome) {
                    free++;
                }
            }

            return free;
        }
    }

    /// <summary>How many of an item it holds, summed over every slot.</summary>
    /// <param name="definition">Which item.</param>
    /// <returns>The count.</returns>
    public int CountOf(DefId definition) {
        var count = 0;

        foreach (ref readonly var instance in slots.AsSpan()) {
            if (instance.IsSome && instance.Definition == definition) {
                count += instance.Stack;
            }
        }

        return count;
    }

    /// <summary>How many items it holds in total, stacks summed.</summary>
    public int TotalItems {
        get {
            var count = 0;

            foreach (ref readonly var instance in slots.AsSpan()) {
                if (instance.IsSome) {
                    count += instance.Stack;
                }
            }

            return count;
        }
    }

    internal Span<ItemInstance> Mutable => slots;

    internal ItemInstance[] Snapshot() => [.. slots];

    internal void Restore(ItemInstance[] snapshot) => snapshot.CopyTo(slots, 0);
}
