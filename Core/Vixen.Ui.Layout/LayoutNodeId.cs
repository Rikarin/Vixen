// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ui.Layout;

/// <summary>A node's index into the layout store.</summary>
/// <remarks>
///     A dense <see cref="int" /> and nothing else, so that a hundred thousand of them are a
///     four-hundred-kilobyte array rather than a hundred thousand references. It is not a
///     <c>Handle&lt;T&gt;</c>: the layout store is internal to the UI framework, an id never leaves
///     the element that owns it, and identity that never escapes is the case
///     <c>Vixen.Core.Collections</c>' README describes a free list for rather than a handle pool.
/// </remarks>
/// <param name="Index">The slot.</param>
public readonly record struct LayoutNodeId(int Index) {
    /// <summary>The id no node has.</summary>
    public static readonly LayoutNodeId Invalid = new(-1);

    /// <summary>Whether this refers to a slot at all.</summary>
    public bool IsValid => Index >= 0;

    /// <inheritdoc />
    public override string ToString() =>
        IsValid ? "node " + Index.ToString(CultureInfo.InvariantCulture) : "no node";
}

/// <summary>A measured size.</summary>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct LayoutSize(float Width, float Height);

/// <summary>What a measure function is being asked.</summary>
/// <param name="Node">Which node.</param>
/// <param name="Context">Whatever was attached to it with <see cref="LayoutTree.SetContext" />.</param>
/// <param name="AvailableWidth">The width on offer, or NaN.</param>
/// <param name="WidthMode">What the width means.</param>
/// <param name="AvailableHeight">The height on offer, or NaN.</param>
/// <param name="HeightMode">What the height means.</param>
public readonly record struct MeasureRequest(
    LayoutNodeId Node,
    object? Context,
    float AvailableWidth,
    MeasureMode WidthMode,
    float AvailableHeight,
    MeasureMode HeightMode
);

/// <summary>Measures a leaf whose size comes from its content rather than from its style.</summary>
/// <param name="request">What is being asked.</param>
/// <returns>How big the content is.</returns>
/// <remarks>
///     Text is the reason this exists and text is what makes it expensive, which is why the answer
///     is cached per node against the question that produced it. A measure function must be a pure
///     function of its request: one that answers differently for the same question makes the
///     algorithm oscillate, and the depth guard turns that into an exception rather than a hang.
/// </remarks>
public delegate LayoutSize MeasureFunction(in MeasureRequest request);

/// <summary>Reports where a node's text baseline sits.</summary>
/// <param name="node">The node.</param>
/// <param name="width">Its width.</param>
/// <param name="height">Its height.</param>
/// <param name="context">Whatever was attached to it.</param>
/// <returns>The distance from the node's top edge to its baseline.</returns>
public delegate float BaselineFunction(LayoutNodeId node, float width, float height, object? context);
