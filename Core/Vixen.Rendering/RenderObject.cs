// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>
///     A dense index identifying one renderable for the length of a frame.
/// </summary>
/// <remarks>
///     <para>
///         Dense on purpose: every per-object array in the renderer is indexed by this, so a feature's
///         data for object <em>i</em> is at slot <em>i</em> of its own array. That is what makes
///         "adding skinning" a matter of registering a parallel array rather than of changing the
///         mesh feature — see <see cref="RenderDataHolder" />.
///     </para>
///     <para>
///         An index rather than a pointer or a reference, because the arrays it addresses are
///         reallocated as the object count grows and because a frame's work lists are built by jobs
///         that must not chase managed references.
///     </para>
/// </remarks>
public readonly record struct RenderObjectId(int Index) {
    /// <summary>The id no object has.</summary>
    public static RenderObjectId Invalid => new(-1);

    /// <summary>Whether this identifies an object at all.</summary>
    public bool IsValid => Index >= 0;

    /// <inheritdoc />
    public override string ToString() => IsValid ? $"#{Index}" : "#invalid";
}

/// <summary>
///     Everything the renderer needs about one object before any feature looks at it: where it is,
///     how big it is, which stages want it, and which feature owns it.
/// </summary>
/// <remarks>
///     <para>
///         A value type in a flat array rather than an object, and the reason is the culling loop:
///         it reads bounds and a stage mask for every object in the scene, and doing that through
///         managed references is a cache miss per object at exactly the point where there is nothing
///         else to hide it behind.
///     </para>
///     <para>
///         What is <em>not</em> here is as deliberate: no material, no mesh, no bone palette, no
///         transform beyond the bounds. Those belong to the feature that understands them, in an
///         array that feature registered. Putting them here would make every object pay for every
///         feature's data, which is precisely the design Stride's <c>RenderDataHolder</c> avoids.
///     </para>
/// </remarks>
public struct RenderObject {
    /// <summary>The world-space bounds culling tests, and the centre distance sorts by.</summary>
    public BoundingSphere Bounds;

    /// <summary>Which stages this object appears in.</summary>
    public RenderStageMask Stages;

    /// <summary>The root feature that extracted and will draw this object.</summary>
    public int FeatureIndex;

    /// <summary>
    ///     A per-object grouping value the sort key puts above depth — pipeline state, in practice.
    /// </summary>
    /// <remarks>
    ///     Opaque geometry sorts by this first and by depth second, because a pipeline change costs
    ///     far more than drawing slightly out of front-to-back order. The renderer never interprets
    ///     it; the feature that fills it decides what "the same batch" means.
    /// </remarks>
    public uint SortGroup;

    /// <summary>Whether this slot holds a live object.</summary>
    /// <remarks>
    ///     A flag rather than compaction, because the id is what every feature's parallel array is
    ///     indexed by: compacting on removal would move objects and invalidate every one of them at
    ///     once. Slots are reused instead — see <see cref="RenderObjectStore" />.
    /// </remarks>
    public bool IsAlive;
}
