// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     What a scene says about a terrain: which one, and how to draw it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Nine tenths of a terrain is in the asset, not here</b> — [docs/plan/31 § D2]. A
///         heightfield is tens of megabytes of binary and a <c>.vxscene</c> is the file two people
///         touch every day, so the scene names the terrain and carries the handful of numbers that
///         are a placement rather than a shape.
///     </para>
///     <para>
///         ⚠ <b>No transform of its own beyond the entity's.</b> A terrain's samples are its space —
///         <c>TerrainDescription.MetresPerQuad</c> is the scale and the sample grid is the origin —
///         so a rotated terrain would make every tool that maps a world position to a sample carry an
///         inverse. Placing one somewhere is the entity's <c>LocalTransform</c>, and rotating it is
///         not offered.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct TerrainComponent {
    /// <summary>Which terrain asset this entity draws.</summary>
    /// <remarks>
    ///     A name rather than a handle, for the reason every other asset reference in a scene is one:
    ///     a handle names a slot in a world that issued it, and a scene file is read by a world that
    ///     has not run yet.
    /// </remarks>
    public string Terrain;

    /// <summary>How far level 0 reaches, in metres.</summary>
    /// <remarks>
    ///     <para>
    ///         The one LOD number a scene is likely to want per terrain — a valley floor an artist
    ///         walks wants a nearer range than a mountain range seen from a helicopter. The rest of
    ///         <see cref="Vixen.Terrain.TerrainLodRanges" /> is a project setting.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero — what a zeroed component holds — makes every level's range degenerate.</b>
    ///         A field initializer would not help: the ECS stores components in chunks and a chunk's
    ///         column is zeroed memory, not constructed values. <see cref="Of" /> is the usable
    ///         component, and the editor's add-component menu reaches it the way it reaches
    ///         <c>Camera.Perspective</c> — through <c>ComponentsView.Initial</c>.
    ///     </para>
    /// </remarks>
    public float NearRange;

    /// <summary>Whether to bias every level coarser, for a scalability setting.</summary>
    /// <remarks>
    ///     Positive is coarser. A bias rather than a multiplier because it is applied to the level a
    ///     node was selected at, so one step is exactly one halving of the vertex count — which is
    ///     the granularity the quadtree has and the only one that costs nothing to honour.
    /// </remarks>
    public int LodBias;

    /// <summary>Whether the terrain casts shadows.</summary>
    /// <remarks>
    ///     ⚠ On by <see cref="Of" /> and off in a zeroed component — a ground that throws no shadow
    ///     reads as a lighting bug, so anything constructing this by hand rather than through
    ///     <see cref="Of" /> should say so deliberately.
    /// </remarks>
    public bool CastShadows;

    /// <summary>A terrain drawn with the usual settings.</summary>
    /// <param name="terrain">Which terrain asset.</param>
    /// <returns>The component.</returns>
    public static TerrainComponent Of(string terrain) =>
        new() { Terrain = terrain, NearRange = 64f, LodBias = 0, CastShadows = true };
}
