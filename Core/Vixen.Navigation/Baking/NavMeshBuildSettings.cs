// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Navigation.Baking;

/// <summary>How the walkable surface is cut into the regions that become polygons.</summary>
/// <remarks>
///     Both produce correct navmeshes. The difference is the <i>shape</i> of the polygons, which costs
///     nothing to get wrong and something to get right: fewer, fatter polygons are fewer search nodes
///     and fewer corners for the funnel to consider.
/// </remarks>
public enum NavMeshPartitioning {
    /// <summary>Regions grow out from the ridges of a distance field. The better answer, and the default.</summary>
    /// <remarks>
    ///     Follows the shape of the space: a corridor is one region however it is angled, a room is one
    ///     region. It is the slower of the two — a distance field, a blur, and a flood per water level
    ///     — and it can produce a region with a hole in it, which is why the contour stage has a
    ///     hole-merging pass at all.
    /// </remarks>
    Watershed,

    /// <summary>Regions grow from runs of connected cells swept row by row. Faster, striped.</summary>
    /// <remarks>
    ///     A run joins the region above it only when it is the only run in its row touching that
    ///     region, which is what makes a region with a hole impossible — and also what makes the
    ///     regions long and thin wherever the geometry disagrees with the sweep direction. Worth having
    ///     for a bake that is being run per frame, or for a tile small enough that the shapes cannot go
    ///     far wrong.
    /// </remarks>
    Monotone
}

/// <summary>
///     What the bake is being asked for: how finely to voxelise, and what the agent that will walk
///     the result can do.
/// </summary>
/// <remarks>
///     <para>
///         <b>A navmesh is baked for one agent.</b> Radius, height and step height are not properties
///         of the level, they are properties of who is walking it, and they are baked in: the surface
///         is eroded by the radius so that a path never passes closer to a wall than the agent's
///         body, and anything with less headroom than the height is not surface at all. Two agent
///         sizes are two navmeshes, which is what every engine that does this does.
///     </para>
///     <para>
///         <b>Cell size is the quality dial and the cost dial at once.</b> Halving it quadruples the
///         voxel count and the bake time, and buys a surface that follows the geometry twice as
///         closely. A third of the agent radius is the usual starting point.
///     </para>
/// </remarks>
public readonly record struct NavMeshBuildSettings {
    /// <summary>The default settings: a human-sized agent on a 0.3 m grid.</summary>
    public NavMeshBuildSettings() { }

    /// <summary>The width and depth of one voxel column, in world units.</summary>
    public float CellSize { get; init; } = 0.3f;

    /// <summary>The height of one voxel, in world units.</summary>
    /// <remarks>
    ///     Usually finer than <see cref="CellSize" />, because it is what decides whether a step is a
    ///     step or a wall, and half the cell size is the convention.
    /// </remarks>
    public float CellHeight { get; init; } = 0.2f;

    /// <summary>How tall the agent is. Anything with less headroom is not walkable.</summary>
    public float AgentHeight { get; init; } = 2f;

    /// <summary>How wide the agent is. The walkable surface is eroded by this.</summary>
    public float AgentRadius { get; init; } = 0.6f;

    /// <summary>The tallest step the agent can walk up without jumping.</summary>
    public float AgentMaxClimb { get; init; } = 0.9f;

    /// <summary>The steepest ground the agent can stand on, in degrees.</summary>
    public float AgentMaxSlope { get; init; } = 45f;

    /// <summary>
    ///     The smallest region to keep, in voxel columns. Anything smaller is discarded rather than
    ///     turned into polygons.
    /// </summary>
    /// <remarks>
    ///     This is what removes the navigable island on top of a crate and the sliver of floor behind
    ///     a pillar. Both are surfaces an agent could stand on and neither is one it can reach, and a
    ///     path that ends on one is a path that ends in an agent standing still.
    /// </remarks>
    public int MinRegionArea { get; init; } = 8;

    /// <summary>
    ///     The size below which a region is absorbed into a neighbour, in voxel columns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A different question from <see cref="MinRegionArea" />: that one is "not worth keeping
    ///         at all", this one is "not worth keeping <i>apart</i>". A region under this size is
    ///         merged into the smallest neighbour that will take it, which produces fewer and larger
    ///         polygons without moving the walkable surface at all.
    ///     </para>
    ///     <para>
    ///         Twenty is Recast's default and it does almost nothing here, which is worth knowing
    ///         rather than quietly changing: the monotone sweep produces regions that are long rather
    ///         than small, so few of them are under any modest threshold. Turning it up to 2 000 on a
    ///         pillared eighty-metre level takes 109 regions to 20 and 401 polygons to 310 — 23 %
    ///         fewer — with the path across it identical to the centimetre and no measured
    ///         improvement in search time. Fewer polygons is worth having for its own sake; it is not
    ///         worth having by default without a reason.
    ///     </para>
    /// </remarks>
    public int MergeRegionArea { get; init; } = 20;

    /// <summary>How the walkable surface is cut into regions.</summary>
    public NavMeshPartitioning Partitioning { get; init; } = NavMeshPartitioning.Watershed;

    /// <summary>How far a simplified contour may stray from the voxel outline, in voxels.</summary>
    public float MaxSimplificationError { get; init; } = 1.3f;

    /// <summary>The longest edge a contour may have along a wall, in world units. Zero disables the split.</summary>
    /// <remarks>
    ///     Long edges are cheaper but make worse polygons: a fifty-metre wall becomes one enormous
    ///     triangle whose interior is nowhere near it, and every path that crosses it is pulled to its
    ///     ends. Splitting is a quality-for-size trade with no correctness in it either way.
    /// </remarks>
    public float MaxEdgeLength { get; init; } = 12f;

    /// <summary>How many vertices a polygon may have, up to <see cref="NavMesh.MaxVerticesPerPoly" />.</summary>
    public int MaxVerticesPerPoly { get; init; } = 6;

    /// <summary>The area id given to every triangle the slope test accepted.</summary>
    public byte WalkableArea { get; init; } = NavArea.Walkable;

    /// <summary>The flags given to every polygon produced.</summary>
    public NavPolyFlags WalkableFlags { get; init; } = NavPolyFlags.Walk;

    /// <summary>The agent's height in voxels, rounded up.</summary>
    public int WalkableHeightInCells => (int)MathF.Ceiling(AgentHeight / CellHeight);

    /// <summary>The agent's step height in voxels, rounded down.</summary>
    public int WalkableClimbInCells => (int)MathF.Floor(AgentMaxClimb / CellHeight);

    /// <summary>The agent's radius in voxels, rounded up.</summary>
    public int WalkableRadiusInCells => (int)MathF.Ceiling(AgentRadius / CellSize);

    /// <summary>Throws if a setting cannot produce a mesh.</summary>
    /// <exception cref="ArgumentException">Something is zero, negative, or out of range.</exception>
    public void Validate() {
        Require(CellSize > 0, "CellSize has to be positive.");
        Require(CellHeight > 0, "CellHeight has to be positive.");
        Require(AgentHeight > 0, "AgentHeight has to be positive.");
        Require(AgentRadius >= 0, "AgentRadius cannot be negative.");
        Require(AgentMaxClimb >= 0, "AgentMaxClimb cannot be negative.");
        Require(AgentMaxSlope is > 0 and <= 90, "AgentMaxSlope has to be between 0 and 90 degrees.");
        Require(MinRegionArea >= 0, "MinRegionArea cannot be negative.");
        Require(MergeRegionArea >= 0, "MergeRegionArea cannot be negative.");
        Require(MaxSimplificationError >= 0, "MaxSimplificationError cannot be negative.");
        Require(MaxEdgeLength >= 0, "MaxEdgeLength cannot be negative.");
        Require(MaxVerticesPerPoly is >= 3 and <= NavMesh.MaxVerticesPerPoly, $"MaxVerticesPerPoly has to be between 3 and {NavMesh.MaxVerticesPerPoly}.");
        Require(WalkableArea != NavArea.Null, "WalkableArea cannot be the null area, which is what unwalkable means.");
    }

    static void Require(bool condition, string message) {
        if (!condition) {
            throw new ArgumentException(message);
        }
    }
}
