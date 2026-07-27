// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering;

/// <summary>How a stage orders the work it collected.</summary>
public enum RenderSortMode {
    /// <summary>
    ///     Group first, then near to far — for opaque geometry.
    /// </summary>
    /// <remarks>
    ///     Group above depth, not the other way round: early-Z makes front-to-back worth having, but
    ///     a pipeline switch costs far more than a few overdrawn pixels. Sorting purely by depth is
    ///     the classic mistake that makes a scene slower the better it is culled.
    /// </remarks>
    FrontToBack,

    /// <summary>
    ///     Far to near, ignoring grouping — for anything blended.
    /// </summary>
    /// <remarks>
    ///     Grouping is not merely less important here, it is <em>wrong</em>: blending is
    ///     order-dependent, so reordering two overlapping transparent objects to save a pipeline
    ///     change changes the image.
    /// </remarks>
    BackToFront,

    /// <summary>Group only, leaving depth out of it — for UI and anything else already ordered.</summary>
    ByGroup
}

/// <summary>
///     One list of work with one ordering: "Opaque", "Transparent", "ShadowCaster", "GBuffer".
/// </summary>
/// <remarks>
///     A stage is deliberately not a pass. A pass is where things are drawn — a render-graph node
///     with attachments and barriers; a stage is <em>which</em> things and in what order. One stage
///     feeds several passes (an opaque stage draws into every shadow cascade), and one pass may draw
///     several stages, so binding the two together would mean a shadow map needing its own copy of
///     "the opaque list".
/// </remarks>
public sealed class RenderStage(string name, RenderSortMode sortMode = RenderSortMode.FrontToBack) {
    /// <summary>The stage's name, for logging and for the compositor asset to refer to it by.</summary>
    public string Name { get; } = name;

    /// <summary>How this stage's work is ordered.</summary>
    public RenderSortMode SortMode { get; } = sortMode;

    /// <summary>The stage's index, assigned when it is added to a <see cref="RenderSystem" />.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>This stage alone, as a mask.</summary>
    public RenderStageMask Mask => RenderStageMask.Of(Index);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A set of stages, as a bit per stage index.</summary>
/// <remarks>
///     <para>
///         A mask rather than a list, because the culling loop asks "does this object appear in any
///         stage this view wants" once per object per view, and that has to be one <c>and</c>.
///     </para>
///     <para>
///         64 stages is the ceiling. Stride's shipped compositors use fewer than ten; a project
///         needing more has a compositor problem rather than a bit-width problem, and finding out at
///         <see cref="RenderSystem.AddStage" /> is better than a silent wrap.
///     </para>
/// </remarks>
public readonly record struct RenderStageMask(ulong Bits) {
    /// <summary>How many distinct stages a mask can hold.</summary>
    public const int Capacity = 64;

    /// <summary>The empty set.</summary>
    public static RenderStageMask None => default;

    /// <summary>The set holding just one stage.</summary>
    public static RenderStageMask Of(int stageIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(stageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(stageIndex, Capacity);
        return new(1UL << stageIndex);
    }

    /// <summary>Whether the set is empty.</summary>
    public bool IsEmpty => Bits == 0;

    /// <summary>Whether a stage is in the set.</summary>
    public bool Contains(int stageIndex) => (Bits & (1UL << stageIndex)) != 0;

    /// <summary>Whether the two sets have any stage in common.</summary>
    public bool Intersects(RenderStageMask other) => (Bits & other.Bits) != 0;

    /// <summary>The union of two sets.</summary>
    public static RenderStageMask operator |(RenderStageMask left, RenderStageMask right) =>
        new(left.Bits | right.Bits);

    /// <summary>The stages both sets hold.</summary>
    public static RenderStageMask operator &(RenderStageMask left, RenderStageMask right) =>
        new(left.Bits & right.Bits);

    /// <summary>The union of two sets, for callers that cannot use the operator.</summary>
    public RenderStageMask Union(RenderStageMask other) => this | other;

    /// <summary>The stages both sets hold, for callers that cannot use the operator.</summary>
    public RenderStageMask Intersect(RenderStageMask other) => this & other;

    /// <summary>The stages in the set, ascending.</summary>
    public IEnumerable<int> Indices() {
        for (var i = 0; i < Capacity; i++) {
            if (Contains(i)) {
                yield return i;
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => IsEmpty ? "{}" : "{" + string.Join(", ", Indices()) + "}";
}
