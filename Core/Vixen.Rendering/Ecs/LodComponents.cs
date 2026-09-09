// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Rendering.Ecs;

/// <summary>The switch points of one LOD group, on the entity its levels hang from.</summary>
/// <remarks>
///     <para>
///         <b>A group is a parent and its levels are its children</b>, which is the one identity the
///         scene already has. <c>LodRenderFeature</c> chooses one level per group per view, so the
///         group has to be per authored object rather than per set of thresholds — two trees sharing
///         a threshold list are two groups, and interning by the numbers would lock them to one
///         level between them.
///     </para>
///     <para>
///         <b>The levels are the children carrying <see cref="LodLevel" /></b>, in no particular
///         order: which level a child is is the number it carries, not its place in the list. A child
///         with no <see cref="LodLevel" /> is not part of the group and is drawn whatever the group
///         is showing — a collider, a light or a socket hanging off the same parent.
///     </para>
///     <para>
///         ⚠ <b>An array on a component, which most of them do not have.</b> The count is the
///         group's and a component is one size for every entity in a chunk;
///         <c>BlendShapeWeights</c> holds an array for the same reason and pays the same price —
///         this is a <em>managed</em> component, so a system reads one entity at a time rather than
///         through <c>Chunk.ReadValues</c>.
///     </para>
///     <para>
///         ⚠ <b>Null and empty both mean "one level", and neither is a mistake.</b> Four levels take
///         three thresholds, so no thresholds is a group of one — which is what a zeroed column
///         reads as, and drawing the only level is the right answer for it.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct LodGroupComponent {
    /// <summary>
    ///     The screen-height fraction below which each level gives way to the next, descending.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Descending, and <c>LodRenderFeature.Add</c> throws on a list that is not.</b> The
    ///     numbers are a fraction of the viewport's height, so they mean the same thing at every
    ///     resolution and every field of view — see <c>RenderView.ScreenHeightScale</c>.
    /// </remarks>
    public float[]? Thresholds;
}

/// <summary>Which level of its parent's group an entity draws.</summary>
/// <remarks>
///     <para>
///         Zero is the most detailed level, which is also what a zeroed column reads as — so an
///         entity that gained the component and was never given a number is the finest level rather
///         than an invalid one.
///     </para>
///     <para>
///         ⚠ <b>Read every frame, unlike <c>MeshRenderable.CastsShadows</c>.</b> The membership it
///         produces is per render object and the render object outlives no <c>Resettle</c>, so the
///         extraction restates it rather than stamping it once — which is also what makes an author
///         dragging a level between groups take effect in the viewport.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct LodLevel {
    /// <summary>Which level this entity is — 0 is the most detailed.</summary>
    public int Level;
}
