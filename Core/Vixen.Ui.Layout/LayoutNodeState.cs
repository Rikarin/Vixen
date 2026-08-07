// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>What is true about a node that is not a style and not a result.</summary>
[Flags]
public enum LayoutNodeState : byte {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The slot holds a live node.</summary>
    Live = 1,

    /// <summary>Something changed under this node and it has to be laid out again.</summary>
    Dirty = 2,

    /// <summary>This node's result changed in the last pass and the consumer has not read it yet.</summary>
    HasNewLayout = 4,

    /// <summary>The node measures itself.</summary>
    HasMeasureFunction = 8,

    /// <summary>The node reports its own baseline.</summary>
    HasBaselineFunction = 16,

    /// <summary>The node is a reference for absolute children even though it is in flow.</summary>
    IsReferenceBaseline = 32,

    /// <summary>This node's order-modified child list no longer matches its children.</summary>
    /// <remarks>
    ///     ⚠ <b>Rebuilt between passes rather than on demand, because the arena moves.</b>
    ///     <c>ChildArena.Slice</c> hands out a span into one array that <c>Array.Resize</c>
    ///     relocates, and the algorithm holds a child span across the recursive call that lays each
    ///     child out. Sorting lazily inside <c>ChildIds</c> could therefore allocate a block, grow
    ///     the arena, and leave an ancestor's loop walking freed memory. So a mutation only sets
    ///     this flag, and <c>CalculateLayout</c> drains the queue before it descends.
    /// </remarks>
    ChildOrderStale = 64
}

/// <summary>Where a node sits in the tree.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LayoutLinks {
    /// <summary>The owner, or -1.</summary>
    public int Parent;

    /// <summary>Where this node's child ids start in the shared child arena.</summary>
    public int ChildOffset;

    /// <summary>How many children there are.</summary>
    public int ChildCount;

    /// <summary>How many the current block has room for.</summary>
    public int ChildCapacity;
}
