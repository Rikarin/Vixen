// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering;

/// <summary>
///     One draw: an object, seen from a view, in a stage.
/// </summary>
/// <remarks>
///     <para>
///         The same object produces a node per (view, stage) it appears in — a mesh in the camera's
///         opaque stage and in four shadow cascades is five nodes referring to one
///         <see cref="RenderObject" />. That is the point of the split: the object's data is
///         extracted once and the per-view work is a list of indices into it.
///     </para>
///     <para>
///         Sixteen bytes, and deliberately no more. This is the array a frame sorts, and sorting is
///         a memory-bandwidth problem long before it is a comparison problem — putting a material
///         reference or a bounding box in here would double the bytes moved for no gain, since the
///         sort only ever looks at the key.
///     </para>
/// </remarks>
public readonly record struct RenderNode(RenderObjectId Object, SortKey Key) {
    /// <inheritdoc />
    public override string ToString() => $"{Object} {Key}";
}

/// <summary>
///     What a stage sorts by, packed so the sort is one integer comparison.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Grouping in the high bits, depth in the low ones.</strong> That single decision is
///         what makes a front-to-back sort also a state-change-minimising sort: comparing the whole
///         64 bits orders by group first and by depth within a group, with no branch and no second
///         pass. Sorting by depth alone is the classic mistake — it makes a scene slower the better
///         it is culled, because the draw order stops correlating with pipeline state.
///     </para>
///     <para>
///         Depth is quantised to 32 bits, which at any real draw distance is far finer than the
///         depth buffer it feeds. Two objects landing on the same value sort by their id, which
///         costs nothing and makes the order deterministic — a frame that draws in a different order
///         run to run is one that cannot be compared against a golden image.
///     </para>
/// </remarks>
public readonly record struct SortKey(ulong Value) : IComparable<SortKey> {
    /// <summary>Builds a key that orders near objects before far ones, grouped first.</summary>
    /// <param name="group">The grouping value, in the high 32 bits.</param>
    /// <param name="distance">Distance from the view. Negative values are clamped to zero.</param>
    public static SortKey FrontToBack(uint group, float distance) =>
        new(((ulong)group << 32) | Quantise(distance));

    /// <summary>Builds a key that orders far objects before near ones.</summary>
    /// <remarks>
    ///     The group is <em>not</em> in the key, and that is not an oversight: blending is
    ///     order-dependent, so letting a grouping value reorder two overlapping transparent objects
    ///     would change the image to save a pipeline change.
    /// </remarks>
    public static SortKey BackToFront(float distance) => new(uint.MaxValue - Quantise(distance));

    /// <summary>Builds a key that orders by group alone, leaving depth out.</summary>
    public static SortKey ByGroup(uint group) => new((ulong)group << 32);

    /// <inheritdoc />
    public int CompareTo(SortKey other) => Value.CompareTo(other.Value);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    public static bool operator <(SortKey left, SortKey right) => left.Value < right.Value;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    public static bool operator >(SortKey left, SortKey right) => left.Value > right.Value;

    /// <summary>Whether <paramref name="left" /> sorts no later than <paramref name="right" />.</summary>
    public static bool operator <=(SortKey left, SortKey right) => left.Value <= right.Value;

    /// <summary>Whether <paramref name="left" /> sorts no earlier than <paramref name="right" />.</summary>
    public static bool operator >=(SortKey left, SortKey right) => left.Value >= right.Value;

    /// <summary>Distance as a monotonic 32-bit integer.</summary>
    /// <remarks>
    ///     A fixed point at 1/256 of a unit rather than the float's bits: reinterpreting a float is
    ///     monotonic only for positive values and needs a sign fix-up, and this is a distance, which
    ///     is never negative and never needs the exponent's dynamic range. Saturating rather than
    ///     wrapping, because an object 16 million units away should sort last, not first.
    /// </remarks>
    static uint Quantise(float distance) {
        if (float.IsNaN(distance) || distance <= 0f) {
            return 0;
        }

        var scaled = distance * 256f;
        return scaled >= uint.MaxValue ? uint.MaxValue : (uint)scaled;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Value:x16}";
}
