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

    // ⚠ The same bargain for `order`, which is rarer than text. A node that has a child with a
    // non-zero `order` gets a second arena block holding its children in order-modified document
    // order; every other node in the tree keeps `null` here and pays one null check per child-list
    // read. `ChildIds` is the single seam the whole algorithm addresses children through, so
    // redirecting it is what makes the property reach line breaking, distribution, justification,
    // cross-axis alignment and the baseline pick without touching any of them.
    OrderedChildren[]? orderedChildren;
    List<int>? reorderQueue;
    long[] orderKeys = [];

    // ⚠ The same bargain again for `grid-template-areas` and for a placement written as an area's
    // name. Both are reference-typed and neither fits in `LayoutStyle`, but unlike a track list they
    // are also not worth an arena: an area template is one object per grid *container*, and a
    // document has a handful of those beside a hundred thousand boxes. So these are two lazily
    // allocated managed arrays, exactly as `measureFunctions` is, and a tree with no named area in
    // it allocates neither. See GridAreaTemplate.
    GridAreaTemplate?[]? gridAreas;
    GridPlacementNames[]? placementNames;

    ChildArena children = new();

    // ⚠ The second arena, and the only thing grid asked of the store that block did not. A track
    // list is variable-length and `LayoutStyle` is a fixed-size unmanaged struct, so the four grid
    // template properties are handles into here. A tree with no grid in it never touches it.
    readonly TrackArena tracks = new();

    // ⚠ The third arena, and the only one on the *output* side. A non-atomic inline box that crosses
    // a line produces one box per line, which is the first time in this store that a node's geometry
    // has not been a fixed four floats at a known offset. A tree with no such box never touches it.
    readonly FragmentArena fragments = new();

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
        results[index].GridAreaWidth = float.NaN;
        results[index].MinContentSizes[0] = float.NaN;
        results[index].MinContentSizes[1] = float.NaN;
        results[index].LastOwnerDirection = Direction.Inherit;

        // ⚠ `default` above zeroed this, and zero is a perfectly valid arena offset. -1 is the "no
        // block" sentinel every other handle in this store uses; a reused slot that kept the zero
        // would read whichever fragments the previous occupant left behind.
        results[index].FragmentOffset = -1;

        links[index] = new LayoutLinks { Parent = -1, ChildOffset = -1 };
        flags[index] = LayoutNodeState.Live | LayoutNodeState.Dirty | LayoutNodeState.HasNewLayout;

        // A reused slot must not inherit the previous occupant's sorted block, and clearing the
        // stale flag here is what lets `FlushChildOrder` skip a queue entry whose node is gone.
        if (orderedChildren is not null) {
            orderedChildren[index] = new OrderedChildren { Offset = -1 };
        }

        if (measureFunctions is not null) {
            measureFunctions[index] = null;
        }

        if (baselineFunctions is not null) {
            baselineFunctions[index] = null;
        }

        if (contexts is not null) {
            contexts[index] = null;
        }

        ClearGridNames(index);

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
        ReleaseGridTemplates(index);
        ClearGridNames(index);
        ReleaseOrderedBlock(index);
        ReleaseFragments(index);
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
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="index" /> is negative or past the end of <paramref name="parent" />'s
    ///     child list.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>The index is a position in <i>this</i> store's child list, which is not necessarily
    ///     the same list a caller is reading positions off.</b> Every caller here holds a node in at
    ///     least one other tree as well, and those trees are free to contain nodes this one does
    ///     not — <c>UiDocument.CreateSurface</c> is one that does, and it took two callers with it.
    ///     So the range failure below says what the count actually is rather than leaving the caller
    ///     to find out that the two lists were different lengths.
    /// </remarks>
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

        if (index > parentLinks.ChildCount) {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"{parent} has {parentLinks.ChildCount} children here, so {index} is past the end of its child "
                + "list. An index taken from another tree of the same nodes has to be converted first: the two "
                + "lists are only the same length while neither holds a node the other does not."
            );
        }

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

        // The sorted block is a copy of a child list that just changed, and it is one entry short.
        InvalidateChildOrder(parentIndex, styles[childIndex].Order);
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

        // Unconditional in effect: a parent with no sorted block has nothing to invalidate, and one
        // that has a block has it whatever the departing child's own `order` was.
        InvalidateChildOrder(parentIndex, styles[childIndex].Order);
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

    /// <summary>Marks a subtree as needing layout whatever its styles say.</summary>
    /// <param name="node">The root of the subtree.</param>
    /// <remarks>
    ///     ⚠ <b>The escape hatch <see cref="MarkDirty" /> deliberately is not, and it exists for one
    ///     reason: <see cref="PointScaleFactor" />.</b> Everything else that can change a node's
    ///     result is a style or a child, and both mark it — which is what makes the refusal in
    ///     <c>MarkDirty</c> right. The pixel grid is neither. A window dragged onto a display with a
    ///     different scale changes no declaration anywhere, so nothing is dirty, so
    ///     <see cref="CalculateLayout" /> answers from the cache and the rounding pass — which is
    ///     what reads the grid — never runs. The window then keeps the previous display's half-pixel
    ///     seams for as long as nothing else about it changes.
    ///
    ///     Every node in the subtree rather than only its root, because the cache is per node: an
    ///     ancestor that recomputes still serves its children's sizes from theirs.
    /// </remarks>
    public void Invalidate(LayoutNodeId node) {
        var index = Validate(node);

        MarkSubtreeDirty(index);
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
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Each of the four is cleared as well as freed, and that is not tidiness.</b>
    ///         <see cref="NativeArray{T}" /> is a <c>readonly struct</c>, so its <c>Dispose</c>
    ///         cannot null its own pointer — the field goes on holding the freed address and
    ///         <c>IsEmpty</c> goes on answering <see langword="false" />. Left that way, the next
    ///         <see cref="CreateNode" /> finds <c>capacity</c> at nought, calls <c>Grow(0)</c>, and
    ///         <c>Grow</c>'s <c>Resize</c> tests exactly that property: it copies out of memory that
    ///         is no longer ours and hands the same address back to the allocator a second time.
    ///         macOS libmalloc aborts on the double free immediately and without a managed
    ///         exception, so the run ends in <c>SIGABRT</c> with no test name on it — and, under
    ///         xunit, only after the runner's 60-second crash-detection timeout, which reads exactly
    ///         like a deadlock. Assigning <c>default</c> makes a disposed store grow a fresh set
    ///         instead, which is also what makes disposing twice free nothing twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>freeCount</c> for the same reason, one step further in.</b> A tree that
    ///         removed a node before it was disposed has slots on the free list, and
    ///         <see cref="CreateNode" /> takes one of those <i>without</i> growing — so it would
    ///         write through the stale pointer before <c>Grow</c> ever got a chance to replace it.
    ///         Clearing the arrays alone would leave that path live.
    ///     </para>
    /// </remarks>
    public void Dispose() {
        styles.Dispose();
        results.Dispose();
        links.Dispose();
        flags.Dispose();
        styles = default;
        results = default;
        links = default;
        flags = default;
        freeSlots = [];
        freeCount = 0;
        measureFunctions = null;
        baselineFunctions = null;
        contexts = null;
        orderedChildren = null;
        reorderQueue = null;
        orderKeys = [];
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

    /// <summary>A node's children in order-modified document order — what the algorithm walks.</summary>
    /// <remarks>
    ///     ⚠ <b>Sorted, unlike <see cref="DocumentChildIds" />.</b> With no <c>order</c> anywhere in
    ///     the tree the two are the same span and this costs one null check. See
    ///     <c>LayoutTree.Order.cs</c> for why the sorted block is built between passes rather than
    ///     here.
    /// </remarks>
    internal Span<int> ChildIds(int index) {
        if (orderedChildren is not null) {
            var offset = orderedChildren[index].Offset;
            if (offset >= 0) {
                return children.Slice(offset, links[index].ChildCount);
            }
        }

        return children.Slice(links[index].ChildOffset, links[index].ChildCount);
    }

    /// <summary>A node's children as the document declares them, whatever their <c>order</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>What anything running outside a layout pass has to use.</b> The order-modified block
    ///     is only guaranteed to agree with the child list after <c>FlushChildOrder</c>, so a
    ///     traversal reachable from a public mutator — <see cref="Invalidate" />,
    ///     <see cref="DestroyRecursive" /> — could otherwise walk a stale copy naming a node that
    ///     has since been removed.
    /// </remarks>
    internal Span<int> DocumentChildIds(int index) =>
        children.Slice(links[index].ChildOffset, links[index].ChildCount);

    internal MeasureFunction? MeasureFunctionOf(int index) => measureFunctions?[index];

    internal BaselineFunction? BaselineFunctionOf(int index) => baselineFunctions?[index];

    internal object? ContextOf(int index) => contexts?[index];

    void MarkSubtreeDirty(int index) {
        flags[index] |= LayoutNodeState.Dirty;
        results[index].ComputedFlexBasis = float.NaN;
        results[index].MinContentSizes[0] = float.NaN;
        results[index].MinContentSizes[1] = float.NaN;

        // Document order: this is reachable from `Invalidate`, which a caller may run before the
        // next pass has rebuilt the sorted blocks.
        foreach (var child in DocumentChildIds(index)) {
            MarkSubtreeDirty(child);
        }
    }

    void MarkDirtyAndPropagate(int index) {
        // Stopping at the first already-dirty ancestor is what makes marking a leaf cost O(1) in a
        // frame that has already touched something above it, rather than O(depth) every time.
        while (index >= 0 && (flags[index] & LayoutNodeState.Dirty) == 0) {
            flags[index] |= LayoutNodeState.Dirty;
            results[index].ComputedFlexBasis = float.NaN;
            results[index].MinContentSizes[0] = float.NaN;
            results[index].MinContentSizes[1] = float.NaN;
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

        if (orderedChildren is not null) {
            var previous = orderedChildren.Length;
            Array.Resize(ref orderedChildren, next);
            ClearOrderedRange(orderedChildren, previous);
        }

        if (gridAreas is not null) {
            Array.Resize(ref gridAreas, next);
        }

        if (placementNames is not null) {
            Array.Resize(ref placementNames, next);
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
