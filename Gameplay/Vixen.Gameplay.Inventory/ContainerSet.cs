// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;

namespace Vixen.Gameplay.Inventory;

/// <summary>Every container one owner has, and the one thing allowed to change them.</summary>
/// <remarks>
///     <para>
///         <b>A transaction is applied here or nowhere.</b> `Container` exposes its slots read-only
///         precisely so that this is the only writer: a mutation outside a transaction is one that
///         cannot be rolled back, and a half-applied move is how an item ends up in two places or in
///         none.
///     </para>
///     <para>
///         <b>The client's copy is optimistic and reconciled from the authoritative result.</b> Doc
///         28 § Inventory — the same pattern as prediction, one layer up. Nothing here is
///         client-specific: both ends run this code over their own set, and the realm's answer wins.
///     </para>
/// </remarks>
public sealed class ContainerSet {
    readonly Dictionary<uint, Container> containers = [];

    /// <summary>Makes a set over the items a build knows.</summary>
    /// <param name="library">Where item templates come from — stack sizes, slots, tags.</param>
    public ContainerSet(ItemLibrary library) {
        ArgumentNullException.ThrowIfNull(library);

        Library = library;
    }

    /// <summary>Where item templates come from.</summary>
    public ItemLibrary Library { get; }

    /// <summary>The containers, in the order they were added.</summary>
    public IReadOnlyCollection<Container> Containers => containers.Values;

    /// <summary>Adds a container to the set.</summary>
    /// <param name="container">The container.</param>
    /// <returns>The set, so additions chain.</returns>
    /// <exception cref="InvalidOperationException">Two containers share a name.</exception>
    public ContainerSet Add(Container container) {
        ArgumentNullException.ThrowIfNull(container);

        if (!containers.TryAdd(container.Id.Value, container)) {
            throw new InvalidOperationException(
                $"{container.Id} is in this set twice. A container id is a hash of its name, so this is "
                + "either the same container added twice or two names that collide."
            );
        }

        return this;
    }

    /// <summary>Finds a container.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Container? Find(ContainerId id) => containers.GetValueOrDefault(id.Value);

    /// <summary>Finds a container, and refuses to carry on without it.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It.</returns>
    /// <exception cref="KeyNotFoundException">This set has no such container.</exception>
    public Container Get(ContainerId id) =>
        Find(id) ?? throw new KeyNotFoundException($"This set has no container called {id}.");

    /// <summary>Every item in every container, stacks summed. What the conservation oracle counts.</summary>
    public int TotalItems {
        get {
            var total = 0;

            foreach (var container in containers.Values) {
                total += container.TotalItems;
            }

            return total;
        }
    }

    /// <summary>How many of one item the whole set holds.</summary>
    /// <param name="definition">Which item.</param>
    /// <returns>The count.</returns>
    public int CountOf(DefId definition) {
        var total = 0;

        foreach (var container in containers.Values) {
            total += container.CountOf(definition);
        }

        return total;
    }

    /// <summary>Applies a transaction, entirely or not at all.</summary>
    /// <param name="transaction">What to do.</param>
    /// <returns>What happened, and why not when it did not.</returns>
    public ContainerResult Apply(ContainerTransaction transaction) {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction.Count == 0) {
            return ContainerResult.Empty;
        }

        // Snapshot before the first step, not per step: a rollback has to undo everything, and a
        // per-step undo log is a second implementation of the same thing with its own bugs.
        var touched = new List<(Container Container, ItemInstance[] Snapshot)>();

        foreach (var step in transaction.Steps) {
            Touch(step.From.Container, touched);
            Touch(step.To.Container, touched);
            Touch(step.Container, touched);
        }

        var changes = new List<ContainerChange>();

        foreach (var step in transaction.Steps) {
            var failure = Run(step, changes, out var message);

            if (failure == ContainerFailure.None) {
                continue;
            }

            foreach (var (container, snapshot) in touched) {
                container.Restore(snapshot);
            }

            return new(failure, message, []);
        }

        return new(ContainerFailure.None, string.Empty, changes);
    }

    void Touch(ContainerId id, List<(Container, ItemInstance[])> touched) {
        if (!id.IsSome || Find(id) is not { } container) {
            return;
        }

        foreach (var (already, _) in touched) {
            if (ReferenceEquals(already, container)) {
                return;
            }
        }

        touched.Add((container, container.Snapshot()));
    }

    ContainerFailure Run(in ContainerTransaction.Step step, List<ContainerChange> changes, out string message) =>
        step.Kind switch {
            ContainerTransaction.StepKind.Move => Move(step, changes, out message),
            ContainerTransaction.StepKind.Swap => Swap(step, changes, out message),
            ContainerTransaction.StepKind.Insert => Insert(step, changes, out message),
            ContainerTransaction.StepKind.Add => AddAnywhere(step, changes, out message),
            ContainerTransaction.StepKind.Remove => Remove(step, changes, out message),
            _ => Fail(ContainerFailure.None, string.Empty, out message)
        };

    ContainerFailure Move(in ContainerTransaction.Step step, List<ContainerChange> changes, out string message) {
        message = string.Empty;

        // ⚠ A move onto itself is a no-op, and it has to be checked before anything else. Without
        // this the merge path writes the destination and then the source — which for one slot is two
        // writes to the same slot, the second of them `stack - count` — and the item is silently
        // destroyed. Dragging a stack onto itself is something a player does by accident several
        // times an hour; the conservation oracle is what found it.
        if (step.From == step.To) {
            return ContainerFailure.None;
        }

        if (Resolve(step.From, out var source, out var failure, out message) is not { } from
            || Resolve(step.To, out var destination, out failure, out message) is not { } to) {
            return failure;
        }

        if (source.IsReadOnly || destination.IsReadOnly) {
            return Fail(ContainerFailure.ReadOnly, "Nothing in there can be moved.", out message);
        }

        var moving = from[step.From.Slot];

        if (!moving.IsSome) {
            return Fail(ContainerFailure.SlotEmpty, $"{step.From} holds nothing.", out message);
        }

        var count = step.Count <= 0 ? moving.Stack : step.Count;

        if (count > moving.Stack) {
            return Fail(
                ContainerFailure.NotEnough,
                $"{step.From} holds {moving.Stack} and the move asked for {count}.",
                out message
            );
        }

        if (Library.Find(moving.Definition) is not { } template) {
            return Fail(ContainerFailure.UnknownItem, $"{moving.Definition} is not an item this build knows.", out message);
        }

        var arriving = Bound(moving, destination).WithStack(count);

        if (Refuses(destination, to, step.To.Slot, template, arriving, out failure, out message)) {
            return failure;
        }

        var occupant = to[step.To.Slot];

        if (!occupant.IsSome) {
            if (count > template.MaximumStack) {
                return Fail(
                    ContainerFailure.Full,
                    $"{count} is more than a slot holds of {template.Definition.DisplayName}.",
                    out message
                );
            }

            to.Mutable[step.To.Slot] = arriving;
            from.Mutable[step.From.Slot] = moving.WithStack(moving.Stack - count);
            changes.Add(new(step.From, step.To, arriving));

            return ContainerFailure.None;
        }

        if (!destination.AllowsStacking || !CanStack(occupant, arriving, template)) {
            return Fail(
                ContainerFailure.Occupied,
                $"{step.To} already holds something else. Swap them instead of moving onto it.",
                out message
            );
        }

        var space = template.MaximumStack - occupant.Stack;

        if (space < count) {
            return Fail(
                ContainerFailure.Full,
                $"{step.To} has room for {Math.Max(0, space)} more and the move asked for {count}.",
                out message
            );
        }

        to.Mutable[step.To.Slot] = occupant.WithStack(occupant.Stack + count);
        from.Mutable[step.From.Slot] = moving.WithStack(moving.Stack - count);
        changes.Add(new(step.From, step.To, arriving));

        return ContainerFailure.None;
    }

    ContainerFailure Swap(in ContainerTransaction.Step step, List<ContainerChange> changes, out string message) {
        if (Resolve(step.From, out var leftPolicy, out var failure, out message) is not { } left
            || Resolve(step.To, out var rightPolicy, out failure, out message) is not { } right) {
            return failure;
        }

        if (leftPolicy.IsReadOnly || rightPolicy.IsReadOnly) {
            return Fail(ContainerFailure.ReadOnly, "Nothing in there can be moved.", out message);
        }

        var here = Bound(left[step.From.Slot], rightPolicy);
        var there = Bound(right[step.To.Slot], leftPolicy);

        if (SwapRefuses(right, rightPolicy, step.To.Slot, here, out failure, out message)
            || SwapRefuses(left, leftPolicy, step.From.Slot, there, out failure, out message)) {
            return failure;
        }

        left.Mutable[step.From.Slot] = there;
        right.Mutable[step.To.Slot] = here;

        if (here.IsSome) {
            changes.Add(new(step.From, step.To, here));
        }

        if (there.IsSome) {
            changes.Add(new(step.To, step.From, there));
        }

        return ContainerFailure.None;
    }

    ContainerFailure Insert(in ContainerTransaction.Step step, List<ContainerChange> changes, out string message) {
        if (Resolve(step.To, out var policy, out var failure, out message) is not { } to) {
            return failure;
        }

        if (policy.IsReadOnly) {
            return Fail(ContainerFailure.ReadOnly, "Nothing can be put in there.", out message);
        }

        if (!step.Item.IsSome) {
            return ContainerFailure.None;
        }

        if (Library.Find(step.Item.Definition) is not { } template) {
            return Fail(
                ContainerFailure.UnknownItem,
                $"{step.Item.Definition} is not an item this build knows.",
                out message
            );
        }

        var arriving = Bound(step.Item, policy);

        if (Refuses(policy, to, step.To.Slot, template, arriving, out failure, out message)) {
            return failure;
        }

        var occupant = to[step.To.Slot];

        if (!occupant.IsSome) {
            if (arriving.Stack > template.MaximumStack) {
                return Fail(ContainerFailure.Full, "That is more than a slot holds.", out message);
            }

            to.Mutable[step.To.Slot] = arriving;
            changes.Add(new(SlotRef.None, step.To, arriving));

            return ContainerFailure.None;
        }

        if (!policy.AllowsStacking || !CanStack(occupant, arriving, template)) {
            return Fail(ContainerFailure.Occupied, $"{step.To} already holds something else.", out message);
        }

        if (template.MaximumStack - occupant.Stack < arriving.Stack) {
            return Fail(ContainerFailure.Full, $"{step.To} does not have room for that many.", out message);
        }

        to.Mutable[step.To.Slot] = occupant.WithStack(occupant.Stack + arriving.Stack);
        changes.Add(new(SlotRef.None, step.To, arriving));

        return ContainerFailure.None;
    }

    ContainerFailure AddAnywhere(in ContainerTransaction.Step step, List<ContainerChange> changes, out string message) {
        message = string.Empty;

        if (Find(step.Container) is not { } container) {
            return Fail(ContainerFailure.NoSuchContainer, $"There is no container called {step.Container}.", out message);
        }

        if (container.Policy.IsReadOnly) {
            return Fail(ContainerFailure.ReadOnly, "Nothing can be put in there.", out message);
        }

        if (!step.Item.IsSome) {
            return ContainerFailure.None;
        }

        if (Library.Find(step.Item.Definition) is not { } template) {
            return Fail(
                ContainerFailure.UnknownItem,
                $"{step.Item.Definition} is not an item this build knows.",
                out message
            );
        }

        var arriving = Bound(step.Item, container.Policy);
        var remaining = (int)arriving.Stack;

        // Existing stacks first, then empty slots. A player who loots five ore expects the stack in
        // their bag to grow rather than a second stack to appear beside it.
        if (container.Policy.AllowsStacking && template.IsStackable) {
            for (var slot = 0; slot < container.Capacity && remaining > 0; slot++) {
                var occupant = container[slot];

                if (!occupant.IsSome || !CanStack(occupant, arriving, template)) {
                    continue;
                }

                var space = template.MaximumStack - occupant.Stack;

                if (space <= 0) {
                    continue;
                }

                var placed = Math.Min(space, remaining);

                container.Mutable[slot] = occupant.WithStack(occupant.Stack + placed);
                changes.Add(new(SlotRef.None, new(container.Id, slot), arriving.WithStack(placed)));
                remaining -= placed;
            }
        }

        for (var slot = 0; slot < container.Capacity && remaining > 0; slot++) {
            if (container[slot].IsSome) {
                continue;
            }

            if (Refuses(container.Policy, container, slot, template, arriving, out var failure, out message)) {
                // A slotted container refusing this slot may still have one that takes it.
                if (failure == ContainerFailure.WrongSlot) {
                    continue;
                }

                return failure;
            }

            var placed = Math.Min(template.MaximumStack, remaining);

            container.Mutable[slot] = arriving.WithStack(placed);
            changes.Add(new(SlotRef.None, new(container.Id, slot), arriving.WithStack(placed)));
            remaining -= placed;
        }

        if (remaining > 0) {
            // Rolled back by the caller. All of it or none of it: putting 150 of 200 in and dropping
            // the rest is how "you looted it and it vanished" happens.
            return Fail(
                ContainerFailure.Full,
                $"{container.Id} has room for {arriving.Stack - remaining} of {arriving.Stack}.",
                out message
            );
        }

        return ContainerFailure.None;
    }

    ContainerFailure Remove(in ContainerTransaction.Step step, List<ContainerChange> changes, out string message) {
        if (Resolve(step.From, out var policy, out var failure, out message) is not { } from) {
            return failure;
        }

        if (policy.IsReadOnly) {
            return Fail(ContainerFailure.ReadOnly, "Nothing in there can be taken.", out message);
        }

        var going = from[step.From.Slot];

        if (!going.IsSome) {
            return Fail(ContainerFailure.SlotEmpty, $"{step.From} holds nothing.", out message);
        }

        var count = step.Count <= 0 ? going.Stack : step.Count;

        if (count > going.Stack) {
            return Fail(
                ContainerFailure.NotEnough,
                $"{step.From} holds {going.Stack} and the removal asked for {count}.",
                out message
            );
        }

        from.Mutable[step.From.Slot] = going.WithStack(going.Stack - count);
        changes.Add(new(step.From, SlotRef.None, going.WithStack(count)));

        return ContainerFailure.None;
    }

    Container? Resolve(SlotRef slot, out ContainerPolicy policy, out ContainerFailure failure, out string message) {
        policy = ContainerPolicy.Default;
        message = string.Empty;

        if (Find(slot.Container) is not { } container) {
            failure = Fail(ContainerFailure.NoSuchContainer, $"There is no container called {slot.Container}.", out message);

            return null;
        }

        if (slot.Slot < 0 || slot.Slot >= container.Capacity) {
            failure = Fail(
                ContainerFailure.NoSuchSlot,
                $"{slot.Container} has {container.Capacity} slots and this asked for {slot.Slot}.",
                out message
            );

            return null;
        }

        policy = container.Policy;
        failure = ContainerFailure.None;

        return container;
    }

    /// <summary>Whether a destination refuses an item. True means it did, and says why.</summary>
    static bool Refuses(
        ContainerPolicy policy,
        Container container,
        int slot,
        ItemTemplate template,
        in ItemInstance item,
        out ContainerFailure failure,
        out string message
    ) {
        message = string.Empty;
        failure = ContainerFailure.None;

        if (!policy.AllowsBound && item.Binding == ItemBinding.Bound) {
            failure = Fail(ContainerFailure.Bound, $"{template.Definition.DisplayName} is bound.", out message);

            return true;
        }

        if (!Matches(policy.Accepts, template)) {
            failure = Fail(
                ContainerFailure.Rejected,
                $"{container.Id} does not take {template.Definition.DisplayName}.",
                out message
            );

            return true;
        }

        var required = container.SlotTag(slot);

        if (container.IsSlotted && (!required.IsSome || required != template.Slot)) {
            failure = Fail(
                ContainerFailure.WrongSlot,
                $"{template.Definition.DisplayName} does not go in that slot.",
                out message
            );

            return true;
        }

        return false;
    }

    /// <summary>The swap form: an empty side is always acceptable, and an unknown item never is.</summary>
    bool SwapRefuses(
        Container container,
        ContainerPolicy policy,
        int slot,
        in ItemInstance item,
        out ContainerFailure failure,
        out string message
    ) {
        failure = ContainerFailure.None;
        message = string.Empty;

        if (!item.IsSome) {
            return false;
        }

        if (Library.Find(item.Definition) is not { } template) {
            failure = Fail(ContainerFailure.UnknownItem, $"{item.Definition} is not an item this build knows.", out message);

            return true;
        }

        return Refuses(policy, container, slot, template, item, out failure, out message);
    }

    /// <summary>
    ///     The item's own tags against a container's query. A span rather than a
    ///     <see cref="GameplayTagSet" />, because an item template already holds its tags and building
    ///     a set per validation would allocate per move.
    /// </summary>
    static bool Matches(GameplayTagQuery query, ItemTemplate item) {
        foreach (var range in query.None) {
            if (item.HasTagUnder(range)) {
                return false;
            }
        }

        foreach (var range in query.All) {
            if (!item.HasTagUnder(range)) {
                return false;
            }
        }

        if (query.Any.Length == 0) {
            return true;
        }

        foreach (var range in query.Any) {
            if (item.HasTagUnder(range)) {
                return true;
            }
        }

        return false;
    }

    static ItemInstance Bound(in ItemInstance item, ContainerPolicy policy) =>
        item.IsSome && policy.BindsOn != ItemBinding.None && item.Binding == policy.BindsOn ? item.Bind() : item;

    static bool CanStack(in ItemInstance occupant, in ItemInstance arriving, ItemTemplate template) =>
        template.IsStackable
        && occupant.Definition == arriving.Definition
        && occupant.Seed == arriving.Seed
        && occupant.Durability == arriving.Durability
        && occupant.Binding == arriving.Binding;

    static ContainerFailure Fail(ContainerFailure failure, string reason, out string message) {
        message = reason;

        return failure;
    }
}
