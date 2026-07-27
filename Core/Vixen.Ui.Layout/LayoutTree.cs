// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Memory;

namespace Vixen.Ui.Layout;

/// <summary>
///     A tree of layout nodes held as parallel arrays, and the entry point to laying it out.
/// </summary>
/// <remarks>
///     <para>
///         Per ADR-006 this is Yoga's algorithm over a struct-of-arrays store rather than Yoga's
///         data model. The reference C# port is <c>class Node</c> with <c>List&lt;Node&gt;</c>
///         children and a <c>class Style</c> of boxed values — one heap object per node per style
///         per layout result, and a Blender-class UI has 10⁴–10⁵ nodes. Here a node is an
///         <see cref="int" />, and the whole store is five allocations that grow geometrically.
///     </para>
///     <para>
///         Children are held in a shared arena of ids rather than a linked list, which doc 09's
///         <c>LayoutLinks</c> sketch implies. The algorithm addresses children by index inside its
///         inner loops — a line is a range of them — and a linked list makes each of those a walk,
///         turning several O(n) passes into O(n²) on the widest nodes in the tree, which is exactly
///         where it would hurt.
///     </para>
/// </remarks>
public sealed partial class LayoutTree : IDisposable {
    const int InitialCapacity = 64;

    NativeArray<LayoutStyle> styles;
    NativeArray<LayoutResult> results;
    NativeArray<LayoutLinks> links;
    NativeArray<LayoutNodeState> flags;

    // Managed, and only allocated once something needs one. A tree of ten thousand rectangles with
    // no text in it should pay nothing for the fact that text exists.
    MeasureFunction?[]? measureFunctions;
    BaselineFunction?[]? baselineFunctions;
    object?[]? contexts;

    ChildArena children = new();
    int capacity;
    int nodeCount;
    int[] freeSlots = [];
    int freeCount;

    /// <summary>Creates an empty tree.</summary>
    public LayoutTree() => Grow(InitialCapacity);

    /// <summary>How many live nodes there are.</summary>
    public int NodeCount => nodeCount;

    /// <summary>
    ///     How many points a device-independent pixel is, for the rounding pass. Zero disables it.
    /// </summary>
    /// <remarks>
    ///     Rounding is not cosmetic. A node whose left edge lands on 10.5 and whose width is 9.5
    ///     ends at 20; rounding the two independently gives 11 and 10, which ends at 21 and leaves a
    ///     one-pixel gap against its neighbour. Rounding absolute edges rather than sizes is what
    ///     keeps adjacent boxes adjacent, and it is the reason the raw sizes are kept alongside the
    ///     rounded ones.
    /// </remarks>
    public float PointScaleFactor { get; set; } = 1f;

    /// <summary>Adds a node with the CSS initial style.</summary>
    /// <returns>Its id.</returns>
    public LayoutNodeId CreateNode() {
        int index;
        if (freeCount > 0) {
            index = freeSlots[--freeCount];
        } else {
            if (nodeCount == capacity) {
                Grow(capacity * 2);
            }

            index = nodeCount;
        }

        nodeCount++;
        styles[index] = LayoutStyle.Default;
        results[index] = default;
        results[index].ComputedFlexBasis = float.NaN;
        results[index].ComputedAutoMinMainSize = float.NaN;
        results[index].LastOwnerDirection = Direction.Inherit;
        links[index] = new LayoutLinks { Parent = -1, ChildOffset = -1 };
        flags[index] = LayoutNodeState.Live | LayoutNodeState.Dirty | LayoutNodeState.HasNewLayout;

        if (measureFunctions is not null) {
            measureFunctions[index] = null;
        }

        if (baselineFunctions is not null) {
            baselineFunctions[index] = null;
        }

        if (contexts is not null) {
            contexts[index] = null;
        }

        return new LayoutNodeId(index);
    }

    /// <summary>Removes a node and everything under it.</summary>
    /// <param name="node">The subtree root.</param>
    public void DestroyRecursive(LayoutNodeId node) {
        var index = Validate(node);
        var childIds = children.Slice(links[index].ChildOffset, links[index].ChildCount);
        for (var i = childIds.Length - 1; i >= 0; i--) {
            DestroyRecursive(new LayoutNodeId(childIds[i]));
        }

        Detach(index);
        children.Free(links[index].ChildOffset, links[index].ChildCapacity);
        links[index] = new LayoutLinks { Parent = -1, ChildOffset = -1 };
        flags[index] = LayoutNodeState.None;

        if (measureFunctions is not null) {
            measureFunctions[index] = null;
        }

        if (baselineFunctions is not null) {
            baselineFunctions[index] = null;
        }

        if (contexts is not null) {
            contexts[index] = null;
        }

        if (freeCount == freeSlots.Length) {
            Array.Resize(ref freeSlots, int.Max(8, freeSlots.Length * 2));
        }

        freeSlots[freeCount++] = index;
        nodeCount--;
    }

    /// <summary>Puts <paramref name="child" /> under <paramref name="parent" />.</summary>
    /// <param name="parent">The owner.</param>
    /// <param name="child">The node to insert. It must not already have a parent.</param>
    /// <param name="index">Where among the existing children it goes.</param>
    public void InsertChild(LayoutNodeId parent, LayoutNodeId child, int index) {
        var parentIndex = Validate(parent);
        var childIndex = Validate(child);

        if (links[childIndex].Parent >= 0) {
            throw new InvalidOperationException(
                $"{child} already belongs to node {links[childIndex].Parent}. Remove it first — a node with two "
                + "parents has two positions, and which one wins would depend on traversal order."
            );
        }

        ref var parentLinks = ref links[parentIndex];
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, parentLinks.ChildCount);

        if (parentLinks.ChildCount == parentLinks.ChildCapacity) {
            var grown = children.Grow(parentLinks.ChildOffset, parentLinks.ChildCount, parentLinks.ChildCapacity);
            parentLinks.ChildOffset = grown.Offset;
            parentLinks.ChildCapacity = grown.Capacity;
        }

        var slice = children.Slice(parentLinks.ChildOffset, parentLinks.ChildCapacity);
        for (var i = parentLinks.ChildCount; i > index; i--) {
            slice[i] = slice[i - 1];
        }

        slice[index] = childIndex;
        parentLinks.ChildCount++;
        links[childIndex].Parent = parentIndex;

        MarkDirtyAndPropagate(parentIndex);
    }

    /// <summary>Appends <paramref name="child" /> to <paramref name="parent" />.</summary>
    /// <param name="parent">The owner.</param>
    /// <param name="child">The node to append.</param>
    public void AddChild(LayoutNodeId parent, LayoutNodeId child) =>
        InsertChild(parent, child, links[Validate(parent)].ChildCount);

    /// <summary>Takes <paramref name="child" /> out of <paramref name="parent" />.</summary>
    /// <param name="parent">The owner.</param>
    /// <param name="child">The node to remove.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveChild(LayoutNodeId parent, LayoutNodeId child) {
        var parentIndex = Validate(parent);
        var childIndex = Validate(child);
        ref var parentLinks = ref links[parentIndex];
        var slice = children.Slice(parentLinks.ChildOffset, parentLinks.ChildCount);

        var position = slice.IndexOf(childIndex);
        if (position < 0) {
            return false;
        }

        var writable = children.Slice(parentLinks.ChildOffset, parentLinks.ChildCapacity);
        for (var i = position; i < parentLinks.ChildCount - 1; i++) {
            writable[i] = writable[i + 1];
        }

        parentLinks.ChildCount--;
        links[childIndex].Parent = -1;
        MarkDirtyAndPropagate(parentIndex);
        return true;
    }

    /// <summary>How many children a node has.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The count.</returns>
    public int GetChildCount(LayoutNodeId node) => links[Validate(node)].ChildCount;

    /// <summary>One child of a node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="index">Which child.</param>
    /// <returns>The child's id.</returns>
    public LayoutNodeId GetChild(LayoutNodeId node, int index) {
        var links = this.links[Validate(node)];
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, links.ChildCount);
        return new LayoutNodeId(children.Slice(links.ChildOffset, links.ChildCount)[index]);
    }

    /// <summary>A node's parent, or <see cref="LayoutNodeId.Invalid" />.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The parent.</returns>
    public LayoutNodeId GetParent(LayoutNodeId node) => new(links[Validate(node)].Parent);

    /// <summary>Whether the node needs laying out again.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether it is dirty.</returns>
    public bool IsDirty(LayoutNodeId node) => (flags[Validate(node)] & LayoutNodeState.Dirty) != 0;

    /// <summary>Marks a node as needing layout, and its ancestors with it.</summary>
    /// <param name="node">The node.</param>
    /// <remarks>
    ///     Only a node that measures itself may be dirtied directly: for anything else, "the content
    ///     changed" is expressed by changing a style, and a node whose style and children are both
    ///     unchanged cannot produce a different answer. Yoga refuses this for the same reason.
    /// </remarks>
    public void MarkDirty(LayoutNodeId node) {
        var index = Validate(node);
        if ((flags[index] & LayoutNodeState.HasMeasureFunction) == 0) {
            throw new InvalidOperationException(
                $"{node} has no measure function, so nothing about it can have changed without a style or a child "
                + "changing — and both of those already mark it. Dirtying it by hand would only cost a pass."
            );
        }

        MarkDirtyAndPropagate(index);
    }

    /// <summary>Whether this node's result changed in the last pass.</summary>
    /// <param name="node">The node.</param>
    /// <returns>Whether it is new.</returns>
    public bool HasNewLayout(LayoutNodeId node) => (flags[Validate(node)] & LayoutNodeState.HasNewLayout) != 0;

    /// <summary>Records that a node no longer needs laying out.</summary>
    /// <param name="node">The node.</param>
    /// <remarks>The layout pass calls this as it finishes each node; nothing else should.</remarks>
    internal void MarkClean(LayoutNodeId node) => flags[Validate(node)] &= ~LayoutNodeState.Dirty;

    /// <summary>Clears the "changed in the last pass" mark.</summary>
    /// <param name="node">The node.</param>
    public void ClearNewLayout(LayoutNodeId node) => flags[Validate(node)] &= ~LayoutNodeState.HasNewLayout;

    /// <summary>Attaches arbitrary data, handed back to the measure and baseline functions.</summary>
    /// <param name="node">The node.</param>
    /// <param name="context">The data.</param>
    public void SetContext(LayoutNodeId node, object? context) {
        var index = Validate(node);
        contexts ??= new object?[capacity];
        contexts[index] = context;
    }

    /// <summary>Whatever was attached with <see cref="SetContext" />.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The data, or null.</returns>
    public object? GetContext(LayoutNodeId node) => contexts?[Validate(node)];

    /// <summary>Makes a node measure itself instead of taking its size from its children.</summary>
    /// <param name="node">The node. It must have no children.</param>
    /// <param name="measure">The measure function, or null to remove it.</param>
    public void SetMeasureFunction(LayoutNodeId node, MeasureFunction? measure) {
        var index = Validate(node);
        if (measure is not null && links[index].ChildCount > 0) {
            throw new InvalidOperationException(
                $"{node} has children and cannot also measure itself: its size would be decided twice, by two "
                + "rules that do not have to agree."
            );
        }

        measureFunctions ??= new MeasureFunction?[capacity];
        measureFunctions[index] = measure;
        flags[index] = measure is null
            ? flags[index] & ~LayoutNodeState.HasMeasureFunction
            : flags[index] | LayoutNodeState.HasMeasureFunction;

        MarkDirtyAndPropagate(index);
    }

    /// <summary>Makes a node report its own baseline rather than taking a child's.</summary>
    /// <param name="node">The node.</param>
    /// <param name="baseline">The baseline function, or null to remove it.</param>
    public void SetBaselineFunction(LayoutNodeId node, BaselineFunction? baseline) {
        var index = Validate(node);
        baselineFunctions ??= new BaselineFunction?[capacity];
        baselineFunctions[index] = baseline;
        flags[index] = baseline is null
            ? flags[index] & ~LayoutNodeState.HasBaselineFunction
            : flags[index] | LayoutNodeState.HasBaselineFunction;

        MarkDirtyAndPropagate(index);
    }

    /// <summary>Releases the store.</summary>
    public void Dispose() {
        styles.Dispose();
        results.Dispose();
        links.Dispose();
        flags.Dispose();
        measureFunctions = null;
        baselineFunctions = null;
        contexts = null;
        children = new ChildArena();
        capacity = 0;
        nodeCount = 0;
    }

    internal int Validate(LayoutNodeId node) {
        if ((uint) node.Index >= (uint) capacity || (flags[node.Index] & LayoutNodeState.Live) == 0) {
            throw new ArgumentOutOfRangeException(nameof(node), $"{node} is not a live node in this tree.");
        }

        return node.Index;
    }

    internal Span<int> ChildIds(int index) => children.Slice(links[index].ChildOffset, links[index].ChildCount);

    internal MeasureFunction? MeasureFunctionOf(int index) => measureFunctions?[index];

    internal BaselineFunction? BaselineFunctionOf(int index) => baselineFunctions?[index];

    internal object? ContextOf(int index) => contexts?[index];

    void MarkDirtyAndPropagate(int index) {
        // Stopping at the first already-dirty ancestor is what makes marking a leaf cost O(1) in a
        // frame that has already touched something above it, rather than O(depth) every time.
        while (index >= 0 && (flags[index] & LayoutNodeState.Dirty) == 0) {
            flags[index] |= LayoutNodeState.Dirty;
            results[index].ComputedFlexBasis = float.NaN;
            index = links[index].Parent;
        }
    }

    void Detach(int index) {
        var parent = links[index].Parent;
        if (parent >= 0) {
            RemoveChild(new LayoutNodeId(parent), new LayoutNodeId(index));
        }
    }

    void Grow(int wanted) {
        var next = int.Max(InitialCapacity, wanted);
        Resize(ref styles, next);
        Resize(ref results, next);
        Resize(ref links, next);
        Resize(ref flags, next);

        if (measureFunctions is not null) {
            Array.Resize(ref measureFunctions, next);
        }

        if (baselineFunctions is not null) {
            Array.Resize(ref baselineFunctions, next);
        }

        if (contexts is not null) {
            Array.Resize(ref contexts, next);
        }

        capacity = next;

        static void Resize<T>(ref NativeArray<T> array, int length) where T : unmanaged {
            var grown = NativeArray<T>.Zeroed(length);
            if (!array.IsEmpty) {
                array.AsSpan().CopyTo(grown.AsSpan());
                array.Dispose();
            }

            array = grown;
        }
    }
}
