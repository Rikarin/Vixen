// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>
///     CSS Flexbox §5.4 <c>order</c>: the one part of the algorithm that is not Yoga's, because Yoga
///     does not have it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not one of the 534 ported fixtures exercises this file.</b> Yoga implements no
///         <c>order</c> property — its style surface goes from <c>flexWrap</c> to <c>overflow</c>
///         with nothing between — so <c>Vixen.YogaTestGen</c> emits no fixture that sets it and the
///         conformance suite is silent about every line here. That is the same blind spot the §4.5
///         automatic minimum size had, and it is closed the same way: hand-written cases whose
///         expected numbers come from the specification and from <c>web-platform-tests</c> rather
///         than from this implementation. See <c>OrderTests</c>.
///     </para>
///     <para>
///         <b>The whole property is one redirection.</b> The algorithm addresses children only
///         through <see cref="ChildIds" /> — a flex line is a <i>range</i> of that span — so sorting
///         what that returns is what makes <c>order</c> reach line breaking, the two free-space
///         passes, justification, cross-axis alignment and the baseline pick without any of them
///         knowing the property exists. Document order stays the truth of the store:
///         <see cref="GetChild" />, <see cref="InsertChild" /> and <see cref="RemoveChild" /> read
///         and write the unsorted block, because their indices are the document's.
///     </para>
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>Sets which ordinal group an item is laid out and painted in.</summary>
    /// <param name="node">The node.</param>
    /// <param name="order">The group. Negative is allowed; zero is the initial value.</param>
    /// <remarks>
    ///     The <i>parent</i> is what this invalidates, since <c>order</c> is a property an item
    ///     carries and its container reads. A node with no parent stores the value and nothing
    ///     happens, which is what CSS says too: <c>order</c> on a non-item has no effect.
    /// </remarks>
    public void SetOrder(LayoutNodeId node, int order) {
        var index = Validate(node);
        if (styles[index].Order == order) {
            return;
        }

        styles[index].Order = order;
        InvalidateChildOrder(links[index].Parent, order);
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Where a node's ordered child block lives, or -1 if it has none.</summary>
    /// <remarks>
    ///     Parallel to <c>links</c> and allocated only once some node in the tree actually sets
    ///     <c>order</c>, so a document that never uses the property carries no second block, no
    ///     queue and no scratch — one null check on the child-list read is the entire cost.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    struct OrderedChildren {
        public int Offset;
        public int Capacity;
    }

    /// <summary>Whether this node currently keeps an order-modified copy of its children.</summary>
    bool HasOrderedBlock(int index) => orderedChildren is not null && orderedChildren[index].Offset >= 0;

    /// <summary>Records that a parent's order-modified child list has to be rebuilt.</summary>
    /// <param name="parent">The container, or -1.</param>
    /// <param name="order">
    ///     The <c>order</c> that provoked this, when there is one. A mutation involving only
    ///     defaulted items cannot create a reordering, so a tree that never uses the property never
    ///     enters the queue at all.
    /// </param>
    void InvalidateChildOrder(int parent, int order) {
        if (parent < 0 || (order == 0 && !HasOrderedBlock(parent))) {
            return;
        }

        if ((flags[parent] & LayoutNodeState.ChildOrderStale) != 0) {
            return;
        }

        flags[parent] |= LayoutNodeState.ChildOrderStale;
        (reorderQueue ??= []).Add(parent);
    }

    /// <summary>Rebuilds every stale order-modified child list, before the pass descends.</summary>
    /// <remarks>
    ///     ⚠ <b>Here rather than lazily inside <see cref="ChildIds" />, and that is a correctness
    ///     rule rather than a preference.</b> Building a block can grow the arena, and growing the
    ///     arena moves the one array every outstanding child span points into — while the algorithm
    ///     is holding such a span across the recursive call that lays each child out. A lazy sort
    ///     would therefore leave an ancestor's loop iterating freed memory, intermittently and only
    ///     on the trees big enough to reallocate.
    /// </remarks>
    void FlushChildOrder() {
        if (reorderQueue is not { Count: > 0 } queue) {
            return;
        }

        foreach (var index in queue) {
            // The slot may have been destroyed, or destroyed and handed to a new node, since it was
            // queued. Both are caught here: `CreateNode` clears the flag it would have inherited.
            if ((flags[index] & (LayoutNodeState.Live | LayoutNodeState.ChildOrderStale))
                == (LayoutNodeState.Live | LayoutNodeState.ChildOrderStale)) {
                RebuildChildOrder(index);
            }
        }

        queue.Clear();
    }

    /// <summary>Sorts one node's children into order-modified document order.</summary>
    void RebuildChildOrder(int index) {
        flags[index] &= ~LayoutNodeState.ChildOrderStale;

        var count = links[index].ChildCount;
        if (count <= 1 || !AnyChildIsOrdered(index, count)) {
            // Every item defaulted, so order-modified document order *is* document order and the
            // block would be a copy of one that already exists. Handing it back is what makes
            // `order-0` on the last styled child cost nothing afterwards.
            ReleaseOrderedBlock(index);
            return;
        }

        // Allocated before either span is taken: this is the call that can move the arena.
        EnsureOrderedCapacity(index, count);

        if (orderKeys.Length < count) {
            Array.Resize(ref orderKeys, int.Max(count, orderKeys.Length * 2));
        }

        var target = children.Slice(orderedChildren![index].Offset, count);
        children.Slice(links[index].ChildOffset, count).CopyTo(target);

        var keys = orderKeys.AsSpan(0, count);
        for (var i = 0; i < count; i++) {
            // ⚠ <b>The document position is packed into the low half of the key, which is what makes
            // this stable.</b> `Span.Sort` is an introsort and introsort is not stable, so two items
            // with the same `order` would otherwise come out in whichever arrangement the
            // partitioning happened to leave them in — the classic bug in this property, and one
            // that hides until a list has enough equal-order items to trip the quicksort path.
            // Distinct keys mean the comparison never has a tie to resolve, so stability stops
            // depending on the algorithm at all.
            keys[i] = ((long) styles[target[i]].Order << 32) | (uint) i;
        }

        keys.Sort(target);
    }

    bool AnyChildIsOrdered(int index, int count) {
        foreach (var child in children.Slice(links[index].ChildOffset, count)) {
            if (styles[child].Order != 0) {
                return true;
            }
        }

        return false;
    }

    void EnsureOrderedCapacity(int index, int count) {
        if (orderedChildren is null) {
            orderedChildren = new OrderedChildren[capacity];
            ClearOrderedRange(orderedChildren, 0);
        }

        ref var ordered = ref orderedChildren[index];
        while (ordered.Capacity < count) {
            // A live count of zero: nothing in the old block is worth copying, because every id is
            // about to be written over from the document block.
            var grown = children.Grow(ordered.Offset, 0, ordered.Capacity);
            ordered.Offset = grown.Offset;
            ordered.Capacity = grown.Capacity;
        }
    }

    void ReleaseOrderedBlock(int index) {
        if (orderedChildren is null) {
            return;
        }

        ref var ordered = ref orderedChildren[index];
        children.Free(ordered.Offset, ordered.Capacity);
        ordered = new OrderedChildren { Offset = -1 };
    }

    /// <summary>Marks slots from <paramref name="from" /> up as having no ordered block.</summary>
    /// <remarks>Zero is a valid arena offset, so "none" has to be -1 and cannot be <c>default</c>.</remarks>
    static void ClearOrderedRange(OrderedChildren[] array, int from) {
        for (var i = from; i < array.Length; i++) {
            array[i] = new OrderedChildren { Offset = -1 };
        }
    }
}
