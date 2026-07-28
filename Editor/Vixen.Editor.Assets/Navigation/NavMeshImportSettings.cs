// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;
using Vixen.Navigation.Baking;

namespace Vixen.Editor.Assets.Navigation;

/// <summary>How a navmesh asset is baked.</summary>
/// <remarks>
///     <para>
///         The bake parameters are <i>settings</i> rather than content, which is the split the
///         pipeline already makes everywhere else: the <c>.vxnavmesh</c> file says which geometry the
///         mesh is for, and the <c>.meta</c> beside it says how finely and for what agent — so a
///         project can bake the same level coarser on a phone through the per-target overrides the
///         meta format already has, without a second asset.
///     </para>
///     <para>
///         Every value is also a cache key input, because the settings hash is part of the artefact
///         key. Turning the cell size down re-imports; changing nothing does not.
///     </para>
/// </remarks>
[DataContract("NavMeshImporter")]
public sealed record NavMeshImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>The width and depth of one voxel column, in world units.</summary>
    public float CellSize { get; init; } = 0.3f;

    /// <summary>The height of one voxel.</summary>
    public float CellHeight { get; init; } = 0.2f;

    /// <summary>How tall the agent is.</summary>
    public float AgentHeight { get; init; } = 2f;

    /// <summary>How wide the agent is.</summary>
    public float AgentRadius { get; init; } = 0.6f;

    /// <summary>The tallest step it can walk up.</summary>
    public float AgentMaxClimb { get; init; } = 0.9f;

    /// <summary>The steepest ground it can stand on, in degrees.</summary>
    public float AgentMaxSlope { get; init; } = 45f;

    /// <summary>The smallest region to keep, in voxel columns.</summary>
    public int MinRegionArea { get; init; } = 8;

    /// <summary>How far a simplified contour may stray from the voxel outline, in voxels.</summary>
    public float MaxSimplificationError { get; init; } = 1.3f;

    /// <summary>The longest wall edge to leave unsplit, in world units.</summary>
    public float MaxEdgeLength { get; init; } = 12f;

    /// <summary>
    ///     How wide a tile is, in voxels, or zero for one tile covering everything.
    /// </summary>
    /// <remarks>
    ///     Zero is the right default for a level small enough to rebake whole, which is most of them
    ///     while a project is being built. Tiles are what make a rebake local and streaming possible,
    ///     and they cost a little more total bake time because every tile also voxelises the geometry
    ///     just outside itself.
    /// </remarks>
    public int TileSize { get; init; }

    /// <summary>These settings as the baker's.</summary>
    internal NavMeshBuildSettings ToBuildSettings() => new() {
        CellSize = CellSize,
        CellHeight = CellHeight,
        AgentHeight = AgentHeight,
        AgentRadius = AgentRadius,
        AgentMaxClimb = AgentMaxClimb,
        AgentMaxSlope = AgentMaxSlope,
        MinRegionArea = MinRegionArea,
        MaxSimplificationError = MaxSimplificationError,
        MaxEdgeLength = MaxEdgeLength
    };
}
