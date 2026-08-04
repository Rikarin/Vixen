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

/// <summary>What a scene says about a terrain's grass: which rule grows it, and how far.</summary>
/// <remarks>
///     <para>
///         <b>A second component rather than a field on <see cref="TerrainComponent" /></b>, because
///         grass is optional and a reference-typed field on the terrain would make every bare
///         mountain carry an empty string for a feature it does not use. The split also matches what
///         the two describe: the terrain component is a placement, and this is a <em>rule</em> —
///         [docs/plan/31 § D8] — nothing about a blade is in any file, and the whole field re-grows
///         when the rule changes.
///     </para>
///     <para>
///         ⚠ <b>One grass type per terrain entity, which is this increment's honest ceiling.</b> The
///         ECS holds one component of a type per entity, and a fixed inline array of references is a
///         scene format decision worth more thought than a rush. A level that wants two grasses on
///         one terrain waits on that decision; a level with two terrains carries one of these on
///         each.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct TerrainGrassComponent {
    /// <summary>Which <c>.vxgrass</c> asset says what the grass is like.</summary>
    /// <remarks>A name rather than a handle, on <see cref="TerrainComponent.Terrain" />'s terms.</remarks>
    public string Grass;

    /// <summary>How far from the camera cells are kept resident, in metres. Zero takes the default.</summary>
    /// <remarks>
    ///     Residency, not the cull distance — the two are different questions. The type's
    ///     <c>EndCullDistance</c> says where blades stop being drawn; this says where their cells
    ///     stop being <em>scattered</em>, which is memory rather than triangles. A range below the
    ///     type's cull distance is a field that fades out early and cannot be explained by any
    ///     number on the asset.
    /// </remarks>
    public float Range;

    /// <summary>A field grown with the usual settings.</summary>
    /// <param name="grass">Which grass asset.</param>
    /// <returns>The component.</returns>
    public static TerrainGrassComponent Of(string grass) => new() { Grass = grass, Range = 160f };
}
