// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.Terrain;

/// <summary>The terrain node a frame document names: the ground and its vegetation, as one pass.</summary>
/// <remarks>
///     <para>
///         <b>One node for the whole stack, deliberately.</b> The surface, the grass scatter and the
///         grass draw are one decision about where in the frame the ground goes — after the opaque
///         pass, sharing its depth — and a document that could place the scatter without the draw
///         would be offering an order nothing can want. What it draws is the world's
///         <see cref="TerrainComponent" /> entities, not a terrain named here: which ground exists
///         is the scene's, and the document only says where in the frame it lands.
///     </para>
///     <para>
///         The defaults are <c>!StandardFrame</c>'s names, so the whole installation in a standard
///         frame is three lines of <c>extensions:</c> — <c>afterOpaque: [!Terrain {}]</c> — and a
///         hand-authored document names its own targets instead.
///     </para>
///     <para>
///         ⚠ <b>The nullable scalars are the quality waterfall's seam.</b> Null means "whatever the
///         factory was configured with", which is where a host folds its resolved quality tier;
///         a written value is this document deciding, which out-votes the tier the way a
///         <c>!StandardFrame</c>'s own knobs do. See <see cref="TerrainFactory.Vegetation" />.
///     </para>
/// </remarks>
[DataContract("Terrain")]
public sealed record TerrainNodeAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = "Terrain";

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The colour target the ground draws into — an existing one, loaded rather than cleared.</summary>
    public string Output { get; init; } = "SceneHdr";

    /// <summary>The depth target it tests and writes — the scene's own, so occlusion works both ways.</summary>
    public string Depth { get; init; } = "SceneDepth";

    /// <summary>The view whose camera places, culls and streams the ground.</summary>
    public string View { get; init; } = "Camera";

    /// <summary>The frame's split albedo plane. Its presence is what turns the split path on.</summary>
    /// <remarks>
    ///     No <c>lit:</c> or <c>split:</c> toggle, deliberately — availability is the signal. The
    ///     node lights with the frame exactly when the frame publishes its lighting (the cascade
    ///     constants, the atlas below, a scene camera), and writes the split planes exactly when
    ///     the document declares them; every one of those is a fact the frame already states, and a
    ///     toggle would be a second copy of it that can disagree. These three names exist for the
    ///     hand-authored document whose resources are called something else.
    /// </remarks>
    public string Albedo { get; init; } = "SceneAlbedo";

    /// <summary>And its normal plane, on the same terms.</summary>
    public string Normals { get; init; } = "SceneNormals";

    /// <summary>The cascade atlas the frame renders, which the lit ground samples.</summary>
    public string ShadowAtlas { get; init; } = "ShadowAtlas";

    /// <summary>Which pass's names the frame's lighting is published under.</summary>
    public string ScenePass { get; init; } = "ForwardPlus";

    /// <summary>Whether grass fields scatter and draw at all.</summary>
    /// <remarks>
    ///     A switch rather than a density of zero, for the reason the editor's grass panel has one:
    ///     zero density still dispatches the scatter for every resident cell to reject every
    ///     candidate, which reads to a profiler as grass being expensive when it is off.
    /// </remarks>
    public bool Grass { get; init; } = true;

    /// <summary>What fraction of each grass field to keep, 0…1, or null for the factory's tier.</summary>
    public float? GrassDensityScale { get; init; }

    /// <summary>A multiplier over every grass type's authored cull distances, or null for the tier.</summary>
    public float? GrassCullDistanceScale { get; init; }

    /// <summary>How many grass cells stay resident per field, or null for the tier.</summary>
    public int? GrassResidentCells { get; init; }

    /// <summary>How many blades one cell's run holds, or null for the tier.</summary>
    public int? GrassBladesPerCell { get; init; }

    /// <summary>How far level 0 reaches for a terrain whose component left it at zero, or null for the tier.</summary>
    public float? TerrainNearRange { get; init; }

    /// <summary>What fraction of each foliage volume to draw, 0…1, or null for the factory's tier.</summary>
    public float? FoliageDensityScale { get; init; }

    /// <summary>A multiplier over every foliage type's authored cull distances, or null for the tier.</summary>
    public float? FoliageCullDistanceScale { get; init; }

    /// <summary>How many foliage cells one volume's streamer keeps uploaded, or null for the tier.</summary>
    public int? FoliageCellBudget { get; init; }

    /// <summary>Megabytes of staged tile chains a terrain's streamer may hold, or null for the tier.</summary>
    public int? TerrainStreamingMegabytes { get; init; }
}

/// <summary>The vegetation numbers one quality tier resolves to, in the terrain stack's own vocabulary.</summary>
/// <remarks>
///     <para>
///         <b>The consumption end of doc 39's <c>VegetationQuality</c> group, without the reference
///         that would be a cycle.</b> The waterfall lives in <c>Vixen.Rendering.PostFx</c>, which
///         depends on this project's neighbours and must not be depended on back — so what crosses
///         the boundary is plain numbers, filled by whoever holds a resolved tier:
///         <see cref="TerrainFactory.Vegetation" /> is the slot, and the defaults here are the
///         engine table's High tier, so a host that fills nothing gets the frame the default tier
///         describes.
///     </para>
///     <para>
///         Every field here is read by <see cref="TerrainSceneRenderer" />, and every one a
///         <c>!Terrain</c> node can state is folded over this per parameter — see
///         <see cref="TerrainFactory.Create" />. <see cref="GrassBladesPerCell" /> is the single
///         entry with no counterpart in the waterfall's <c>VegetationQuality</c>: it is a dispatch
///         shape rather than a budget, so a document or the factory sets it and no tier does.
///     </para>
/// </remarks>
public sealed record TerrainVegetationQuality {
    /// <summary>What fraction of each grass field to keep, 0…1.</summary>
    public float GrassDensityScale { get; init; } = 1f;

    /// <summary>A multiplier over every grass type's authored cull distances.</summary>
    public float GrassCullDistanceScale { get; init; } = 1f;

    /// <summary>How many grass cells stay resident per field — the ring's size, which is the memory.</summary>
    public int GrassResidentCells { get; init; } = 256;

    /// <summary>How many blades one cell's run holds.</summary>
    public int GrassBladesPerCell { get; init; } = 4096;

    /// <summary>What fraction of the foliage to draw, 0…1.</summary>
    public float FoliageDensityScale { get; init; } = 1f;

    /// <summary>A multiplier over foliage cull distances.</summary>
    public float FoliageCullDistanceScale { get; init; } = 1f;

    /// <summary>How many foliage cells stay resident — the streamer's pool, which is the upload.</summary>
    public int FoliageCellBudget { get; init; } = 512;

    /// <summary>How far level 0 reaches for a terrain that does not say, in metres.</summary>
    public float TerrainNearRange { get; init; } = 64f;

    /// <summary>Megabytes of staged tile chains one terrain's streamer may hold.</summary>
    /// <remarks>
    ///     ⚠ <b>Bytes, unlike every other budget here, because a tile's size is knowable and a cell's
    ///     is not.</b> A mip chain is a fixed count of <c>ushort</c> samples once the tile size is
    ///     read, so <see cref="TerrainStreamer" /> divides this into slots; a foliage cell holds
    ///     whatever was painted into it, which is why its budget counts cells.
    /// </remarks>
    public int TerrainStreamingMegabytes { get; init; } = 64;
}
