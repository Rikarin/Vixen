// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;

namespace Vixen.Gameplay.Inventory;

/// <summary>Why a transaction did not apply.</summary>
/// <remarks>
///     ⚠ <b>A reason rather than a boolean, because every one of these is something a player is told.</b>
///     "Your bag is full", "that is soulbound", "a ring does not go there" and "you do not have five
///     of those" are four different sentences, and a client that has to guess which one shows the
///     wrong message at the worst moment.
/// </remarks>
public enum ContainerFailure {
    /// <summary>It applied.</summary>
    None = 0,

    /// <summary>The set has no container by that name.</summary>
    NoSuchContainer,

    /// <summary>That container has no such slot.</summary>
    NoSuchSlot,

    /// <summary>There is nothing in the slot to move.</summary>
    SlotEmpty,

    /// <summary>There are fewer there than the operation asked for.</summary>
    NotEnough,

    /// <summary>The destination's policy does not take that kind of item.</summary>
    Rejected,

    /// <summary>An equipment slot that wants something else.</summary>
    WrongSlot,

    /// <summary>A bound item and a destination that does not take bound items.</summary>
    Bound,

    /// <summary>Nowhere for it to go.</summary>
    Full,

    /// <summary>The destination holds something else and the operation was not a swap.</summary>
    Occupied,

    /// <summary>Nothing may change in that container.</summary>
    ReadOnly,

    /// <summary>This build does not know the item being moved.</summary>
    UnknownItem
}

/// <summary>What one step of a transaction did.</summary>
/// <param name="From">Where it came from, or <see cref="SlotRef.None" /> for something created.</param>
/// <param name="To">Where it went, or <see cref="SlotRef.None" /> for something destroyed.</param>
/// <param name="Item">What moved, at the count that moved.</param>
public readonly record struct ContainerChange(SlotRef From, SlotRef To, ItemInstance Item);

/// <summary>What happened when a transaction was applied.</summary>
/// <remarks>
///     <b>The change list is what a ledger entry is written from.</b> Doc 28 § Inventory: a mutation
///     that crosses an ownership boundary is recorded in doc 27's ledger. The kernel does not know
///     what an owner is, so it reports what moved and the caller decides whether that crossed one.
/// </remarks>
public sealed class ContainerResult {
    internal ContainerResult(ContainerFailure failure, string message, IReadOnlyList<ContainerChange> changes) {
        Failure = failure;
        Message = message;
        Changes = changes;
    }

    /// <summary>Nothing was asked for, and nothing went wrong.</summary>
    public static ContainerResult Empty { get; } = new(ContainerFailure.None, string.Empty, []);

    /// <summary>Why it did not apply, or <see cref="ContainerFailure.None" />.</summary>
    public ContainerFailure Failure { get; }

    /// <summary>Whether it applied.</summary>
    public bool Applied => Failure == ContainerFailure.None;

    /// <summary>What went wrong, in a sentence, or the empty string.</summary>
    public string Message { get; }

    /// <summary>What moved, in order. Empty when it did not apply.</summary>
    public IReadOnlyList<ContainerChange> Changes { get; }
}

/// <summary>A set of moves that happen entirely or not at all.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 § Inventory: "every mutation is a transaction over a set of containers".</b>
///         Move, split, merge, swap and equip are not five operations — they are
///         <see cref="Move" /> with different counts and destinations, which is why there is one
///         validator and one place a duplication bug could be.
///     </para>
///     <para>
///         ⚠ <b>Atomicity is the whole reason the type exists, and it is not an optimisation to skip
///         it.</b> A two-step move — take from the bank, put in the bag — that fails on the second
///         step has destroyed an item; one that applies the second step first has duplicated it. The
///         transaction snapshots every container it touches before the first step and restores them
///         all if any step fails.
///     </para>
/// </remarks>
public sealed class ContainerTransaction {
    readonly List<Step> steps = [];

    /// <summary>How many steps it has.</summary>
    public int Count => steps.Count;

    /// <summary>Moves some or all of a stack to a named slot.</summary>
    /// <param name="from">Where from.</param>
    /// <param name="to">Where to.</param>
    /// <param name="count">How many, or zero for the whole stack.</param>
    /// <returns>The transaction, so steps chain.</returns>
    /// <remarks>
    ///     This is also <em>split</em> (a count below the stack, into an empty slot), <em>merge</em>
    ///     (into a slot holding a compatible stack) and <em>equip</em> (into an equipment slot).
    /// </remarks>
    public ContainerTransaction Move(SlotRef from, SlotRef to, int count = 0) {
        steps.Add(new(StepKind.Move, from, to, count, ItemInstance.Empty, ContainerId.None));

        return this;
    }

    /// <summary>Exchanges what is in two slots.</summary>
    /// <param name="left">One slot.</param>
    /// <param name="right">The other.</param>
    /// <returns>The transaction, so steps chain.</returns>
    public ContainerTransaction Swap(SlotRef left, SlotRef right) {
        steps.Add(new(StepKind.Swap, left, right, 0, ItemInstance.Empty, ContainerId.None));

        return this;
    }

    /// <summary>Puts an item into a named slot.</summary>
    /// <param name="to">Where.</param>
    /// <param name="item">What.</param>
    /// <returns>The transaction, so steps chain.</returns>
    /// <remarks>
    ///     ⚠ <b>This creates an item out of nothing</b>, which is what a drop, a craft and a vendor
    ///     purchase do — and which the conservation oracle therefore has to be told about rather than
    ///     count as a leak. Moving one that already exists is <see cref="Move" />.
    /// </remarks>
    public ContainerTransaction Insert(SlotRef to, ItemInstance item) {
        steps.Add(new(StepKind.Insert, SlotRef.None, to, 0, item, ContainerId.None));

        return this;
    }

    /// <summary>Puts an item anywhere in a container that will take it, filling stacks first.</summary>
    /// <param name="container">Which container.</param>
    /// <param name="item">What.</param>
    /// <returns>The transaction, so steps chain.</returns>
    /// <remarks>
    ///     ⚠ <b>All of it or none of it.</b> A stack of 200 ore that only 150 will fit does not put
    ///     150 in and drop 50 — it fails, and the caller decides. Partial success is how "you looted
    ///     it and it vanished" happens.
    /// </remarks>
    public ContainerTransaction Add(ContainerId container, ItemInstance item) {
        steps.Add(new(StepKind.Add, SlotRef.None, SlotRef.None, 0, item, container));

        return this;
    }

    /// <summary>Destroys some or all of a stack.</summary>
    /// <param name="from">Where from.</param>
    /// <param name="count">How many, or zero for the whole stack.</param>
    /// <returns>The transaction, so steps chain.</returns>
    public ContainerTransaction Remove(SlotRef from, int count = 0) {
        steps.Add(new(StepKind.Remove, from, SlotRef.None, count, ItemInstance.Empty, ContainerId.None));

        return this;
    }

    internal IReadOnlyList<Step> Steps => steps;

    internal enum StepKind {
        Move,
        Swap,
        Insert,
        Add,
        Remove
    }

    internal readonly record struct Step(
        StepKind Kind,
        SlotRef From,
        SlotRef To,
        int Count,
        ItemInstance Item,
        ContainerId Container
    );
}
