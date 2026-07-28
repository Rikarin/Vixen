// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Baking;

/// <summary>Where a tile sits and how much of what it voxelises actually belongs to it.</summary>
/// <param name="Volume">The volume voxelised, which is the tile's own plus the margin on every side.</param>
/// <param name="TileX">The tile's column in the mesh's tile grid.</param>
/// <param name="TileZ">The tile's row.</param>
/// <param name="Border">How many cells of margin were added, on each side.</param>
/// <param name="InnerWidth">How many cells across the tile proper is.</param>
/// <param name="InnerDepth">How many cells deep.</param>
internal readonly record struct TilePlacement(BoundingBox Volume, int TileX, int TileZ, int Border, int InnerWidth, int InnerDepth) {
    /// <summary>Works out the volume and the window for a tile covering some bounds.</summary>
    public static TilePlacement For(BoundingBox bounds, NavMeshBuildSettings settings, int tileX, int tileZ, int border) {
        var margin = border * settings.CellSize;

        var volume = new BoundingBox(
            new(bounds.Minimum.X - margin, bounds.Minimum.Y, bounds.Minimum.Z - margin),
            new(bounds.Maximum.X + margin, bounds.Maximum.Y, bounds.Maximum.Z + margin)
        );

        return new(
            volume,
            tileX,
            tileZ,
            border,
            (int)MathF.Round((bounds.Maximum.X - bounds.Minimum.X) / settings.CellSize),
            (int)MathF.Round((bounds.Maximum.Z - bounds.Minimum.Z) / settings.CellSize)
        );
    }
}

/// <summary>A tiled bake: the tiles, and the grid they were laid out on.</summary>
/// <param name="Params">The grid, ready to be handed to a <see cref="NavMesh" />.</param>
/// <param name="Tiles">The tiles that produced any polygons. Empty ones are not in the list.</param>
public readonly record struct NavMeshBakeResult(NavMeshParams Params, IReadOnlyList<NavMeshTileData> Tiles);

/// <summary>
///     Turns level geometry into a navmesh: rasterise, filter, erode, partition, trace, polygonise.
/// </summary>
/// <remarks>
///     <para>
///         The whole pipeline is here as a sequence of calls, and each stage is a type of its own in
///         this namespace, so a tool that wants to draw the voxels or the regions can run the stages
///         itself. What this adds is the order, the unit conversions, and the tiling.
///     </para>
///     <para>
///         <b>Baking is offline work.</b> A tile of a large level takes tens of milliseconds and
///         allocates freely — it is a content-build step and an editor operation, not something a
///         frame does. The runtime half of this assembly, everything a game actually calls per frame,
///         allocates nothing after its first use.
///     </para>
/// </remarks>
public static class NavMeshBaker {
    /// <summary>
    ///     How much geometry outside a tile the bake looks at, in voxels, beyond what the agent radius
    ///     demands. Recast's number, for Recast's reason: erosion and the region sweep both read a
    ///     couple of cells past the point they are deciding about.
    /// </summary>
    const int ExtraBorderCells = 3;

    /// <summary>Bakes one tile covering everything given to it.</summary>
    /// <param name="vertices">The geometry's vertices, in world space.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="bounds">The volume to voxelise.</param>
    /// <param name="settings">How finely, and for what agent.</param>
    /// <param name="volumes">Authored areas to stamp onto the surface — water, road, whatever costs.</param>
    /// <param name="connections">Authored ways off the surface — ladders, jumps, drops.</param>
    /// <returns>The tile, or <see langword="null" /> if nothing in the volume is walkable.</returns>
    /// <remarks>
    ///     The result belongs at tile (0, 0) of a mesh built with <see cref="NavMeshParams.Single" />.
    ///     For a level big enough that a rebake should not touch all of it, or one that streams,
    ///     <see cref="BakeTiles" /> is the same pipeline run per tile.
    /// </remarks>
    public static NavMeshTileData? Bake(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        BoundingBox bounds,
        NavMeshBuildSettings settings,
        ReadOnlySpan<NavAreaVolume> volumes = default,
        ReadOnlySpan<NavOffMeshConnectionData> connections = default
    ) {
        settings.Validate();

        return BakeTile(vertices, indices, bounds, settings, volumes, connections, 0, 0, 0);
    }

    /// <summary>Bakes one tile covering the geometry's own bounds.</summary>
    /// <param name="vertices">The geometry's vertices, in world space.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="settings">How finely, and for what agent.</param>
    /// <param name="volumes">Authored areas to stamp onto the surface.</param>
    /// <param name="connections">Authored ways off the surface — ladders, jumps.</param>
    /// <returns>The tile, or <see langword="null" /> if nothing is walkable.</returns>
    public static NavMeshTileData? Bake(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        NavMeshBuildSettings settings,
        ReadOnlySpan<NavAreaVolume> volumes = default,
        ReadOnlySpan<NavOffMeshConnectionData> connections = default
    ) =>
        Bake(vertices, indices, Volume(vertices, settings), settings, volumes, connections);

    /// <summary>Bakes a grid of tiles.</summary>
    /// <param name="vertices">The geometry's vertices, in world space.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="bounds">The volume to cover.</param>
    /// <param name="settings">How finely, and for what agent.</param>
    /// <param name="tileSize">How wide a tile is, in voxels. Recast's usual answer is 32 to 128.</param>
    /// <param name="volumes">Authored areas to stamp onto the surface. Applied to every tile they reach.</param>
    /// <param name="connections">
    ///     Authored ways off the surface. Each goes to the tile its start falls in, so that unloading a
    ///     tile takes its connections with it.
    /// </param>
    /// <returns>The tiles and the grid they belong on.</returns>
    /// <remarks>
    ///     <para>
    ///         Each tile is baked from the geometry a little way outside it as well — the agent radius
    ///         plus a few cells — so that erosion at the tile edge sees the wall on the other side of
    ///         it. Without that margin every tile border would grow a wall of its own and the tiles
    ///         would not connect.
    ///     </para>
    ///     <para>
    ///         The margin is then dropped rather than kept: the columns outside the tile proper are
    ///         marked unwalkable before the partition runs, so contours stop exactly on the tile edge
    ///         and the polygons of two neighbouring tiles meet there. Connecting them is
    ///         <see cref="NavMesh.AddTile" />'s job, and it matches partial edges, so the two tiles do
    ///         not have to agree about where the vertices along that edge went.
    ///     </para>
    /// </remarks>
    public static NavMeshBakeResult BakeTiles(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        BoundingBox bounds,
        NavMeshBuildSettings settings,
        int tileSize = 64,
        ReadOnlySpan<NavAreaVolume> volumes = default,
        ReadOnlySpan<NavOffMeshConnectionData> connections = default
    ) {
        settings.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 8);

        var extent = tileSize * settings.CellSize;
        var border = settings.WalkableRadiusInCells + ExtraBorderCells;

        var columns = Math.Max(1, (int)MathF.Ceiling((bounds.Maximum.X - bounds.Minimum.X) / extent));
        var rows = Math.Max(1, (int)MathF.Ceiling((bounds.Maximum.Z - bounds.Minimum.Z) / extent));

        var tiles = new List<NavMeshTileData>();

        for (var z = 0; z < rows; z++) {
            for (var x = 0; x < columns; x++) {
                var minimum = new Vector3(
                    bounds.Minimum.X + (x * extent),
                    bounds.Minimum.Y,
                    bounds.Minimum.Z + (z * extent)
                );

                var volume = new BoundingBox(minimum, new(minimum.X + extent, bounds.Maximum.Y, minimum.Z + extent));

                // A connection belongs to the tile its start falls in, so that unloading a tile takes
                // its connections with it rather than leaving them attached to nothing.
                var owned = new List<NavOffMeshConnectionData>();

                foreach (var connection in connections) {
                    var column = (int)MathF.Floor((connection.Start.X - bounds.Minimum.X) / extent);
                    var row = (int)MathF.Floor((connection.Start.Z - bounds.Minimum.Z) / extent);

                    if (column == x && row == z) {
                        owned.Add(connection);
                    }
                }

                if (BakeTile(vertices, indices, volume, settings, volumes, CollectionsMarshal.AsSpan(owned), x, z, border) is { } tile) {
                    tiles.Add(tile);
                }
            }
        }

        return new(new(bounds.Minimum, extent, extent, settings.CellSize * 0.5f, settings.AgentMaxClimb), tiles);
    }

    /// <summary>The volume the geometry occupies, with a cell of slack so nothing sits on the boundary.</summary>
    /// <param name="vertices">The geometry's vertices.</param>
    /// <param name="settings">Used for the size of the slack.</param>
    /// <returns>The volume.</returns>
    public static BoundingBox Volume(ReadOnlySpan<Vector3> vertices, NavMeshBuildSettings settings) {
        if (vertices.IsEmpty) {
            return BoundingBox.Empty;
        }

        var box = BoundingBox.FromPoints(vertices);
        var padding = new Vector3(settings.CellSize, settings.CellHeight, settings.CellSize);

        return new(box.Minimum - padding, box.Maximum + padding);
    }

    static NavMeshTileData? BakeTile(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        BoundingBox bounds,
        NavMeshBuildSettings settings,
        ReadOnlySpan<NavAreaVolume> volumes,
        ReadOnlySpan<NavOffMeshConnectionData> connections,
        int tileX,
        int tileZ,
        int border
    ) {
        if (vertices.IsEmpty || indices.Length < 3) {
            return null;
        }

        var placement = TilePlacement.For(bounds, settings, tileX, tileZ, border);
        var compact = BuildSurface(vertices, indices, in placement, settings);

        return BuildTile(compact, in placement, settings, volumes, connections);
    }

    /// <summary>Voxelises geometry into the walkable surface, before anything decides its shape.</summary>
    /// <remarks>
    ///     Split out because this is the expensive half and the half an obstacle does not change. The
    ///     surface an obstacle carves into is the same surface it was carved out of, so
    ///     <see cref="NavTileCache" /> keeps this and replays only what follows it.
    /// </remarks>
    internal static CompactHeightfield BuildSurface(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        ref readonly TilePlacement placement,
        NavMeshBuildSettings settings
    ) {
        var field = new Heightfield(placement.Volume, settings.CellSize, settings.CellHeight);
        var areas = new byte[indices.Length / 3];

        Heightfield.MarkWalkableTriangles(settings.AgentMaxSlope, vertices, indices, areas, settings.WalkableArea);
        field.RasterizeTriangles(vertices, indices, areas, settings.WalkableClimbInCells);

        field.FilterLowHangingWalkableObstacles(settings.WalkableClimbInCells);
        field.FilterLedgeSpans(settings.WalkableHeightInCells, settings.WalkableClimbInCells);
        field.FilterWalkableLowHeightSpans(settings.WalkableHeightInCells);

        return CompactHeightfield.Build(field, settings.WalkableHeightInCells, settings.WalkableClimbInCells);
    }

    /// <summary>Erodes, partitions, traces and polygonises an already-voxelised surface into a tile.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Erosion is inside this half, not before it</b>, and that is what lets an obstacle be
    ///         carved correctly. Erosion is the promise that a point on the mesh is a place the agent's
    ///         body fits, so anything that makes ground unwalkable has to be applied before it —
    ///         carving after eroding would leave the mesh touching the obstacle and the agent clipping
    ///         it.
    ///     </para>
    ///     <para>
    ///         Volumes that stamp a <i>cost</i> are applied after, for the opposite reason: an area
    ///         painted over ground the bake found unreachable should not resurrect it.
    ///     </para>
    ///     <para>
    ///         <paramref name="compact" />'s areas are rewritten. A caller replaying this has to
    ///         restore them first.
    ///     </para>
    /// </remarks>
    internal static NavMeshTileData? BuildTile(
        CompactHeightfield compact,
        ref readonly TilePlacement placement,
        NavMeshBuildSettings settings,
        ReadOnlySpan<NavAreaVolume> volumes,
        ReadOnlySpan<NavOffMeshConnectionData> connections
    ) {
        foreach (var volume in volumes) {
            if (volume.Area == NavArea.Null) {
                compact.MarkArea(volume);
            }
        }

        compact.ErodeWalkableArea(settings.WalkableRadiusInCells);

        foreach (var volume in volumes) {
            if (volume.Area != NavArea.Null) {
                compact.MarkArea(volume);
            }
        }

        DiscardBorder(compact, placement.Border, placement.InnerWidth, placement.InnerDepth);

        if (settings.Partitioning == NavMeshPartitioning.Monotone) {
            compact.BuildRegionsMonotone(settings.MinRegionArea, settings.MergeRegionArea);
        } else {
            compact.BuildRegionsWatershed(settings.MinRegionArea, settings.MergeRegionArea);
        }

        var contours = ContourSet.Build(compact, settings.MaxSimplificationError, settings.MaxEdgeLength / settings.CellSize);
        var mesh = PolyMesh.Build(contours, settings.MaxVerticesPerPoly);

        if (mesh.PolyCount == 0) {
            return null;
        }

        var detail = PolyMeshDetail.Build(
            mesh,
            compact,
            settings.DetailSampleDistance / settings.CellSize,
            settings.DetailSampleMaxError / settings.CellHeight,
            settings.WalkableHeightInCells
        );

        return ToTile(mesh, detail, placement.Volume, settings, connections, placement.TileX, placement.TileZ);
    }

    /// <summary>Marks the margin unwalkable, now that erosion has had the use of it.</summary>
    /// <remarks>
    ///     The window is counted from the margin rather than back from the field's far edge. The
    ///     field's width is a division rounded up, and a tile whose extent is a whole number of cells
    ///     can still come out a cell wider than that — at which point counting back from the edge
    ///     leaves the last column walkable, its contour half a cell past the tile boundary, and every
    ///     border edge in the wrong plane for the neighbouring tile to link to.
    /// </remarks>
    static void DiscardBorder(CompactHeightfield field, int border, int innerWidth, int innerDepth) {
        if (border <= 0) {
            return;
        }

        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                if (x >= border && z >= border && x < border + innerWidth && z < border + innerDepth) {
                    continue;
                }

                ref var cell = ref field.Cells[x + (z * field.Width)];

                for (var index = cell.Index; index < cell.Index + cell.Count; index++) {
                    field.Areas[index] = NavArea.Null;
                }
            }
        }
    }

    static NavMeshTileData ToTile(
        PolyMesh mesh,
        PolyMeshDetail detail,
        BoundingBox volume,
        NavMeshBuildSettings settings,
        ReadOnlySpan<NavOffMeshConnectionData> connections,
        int tileX,
        int tileZ
    ) {
        var vertices = new Vector3[mesh.VertexCount];

        for (var index = 0; index < vertices.Length; index++) {
            vertices[index] = new(
                volume.Minimum.X + (mesh.Vertices[index * 3] * settings.CellSize),
                volume.Minimum.Y + (mesh.Vertices[(index * 3) + 1] * settings.CellHeight),
                volume.Minimum.Z + (mesh.Vertices[(index * 3) + 2] * settings.CellSize)
            );
        }

        var detailVertices = new Vector3[detail.Vertices.Count];

        for (var index = 0; index < detailVertices.Length; index++) {
            detailVertices[index] = new(
                volume.Minimum.X + (detail.Vertices[index].X * settings.CellSize),
                volume.Minimum.Y + (detail.Vertices[index].Y * settings.CellHeight),
                volume.Minimum.Z + (detail.Vertices[index].Z * settings.CellSize)
            );
        }

        var details = new NavMeshDetailData[detail.Polys.Count];

        for (var index = 0; index < details.Length; index++) {
            var entry = detail.Polys[index];

            details[index] = new() {
                FirstVertex = entry.FirstVertex,
                VertexCount = entry.VertexCount,
                FirstTriangle = entry.FirstTriangle,
                TriangleCount = entry.TriangleCount
            };
        }

        var polys = new NavMeshPolyData[mesh.PolyCount];
        var polyVertices = new List<int>();
        var polyNeighbours = new List<int>();

        for (var poly = 0; poly < mesh.PolyCount; poly++) {
            var offset = poly * mesh.MaxVerticesPerPoly;
            var count = PolyMesh.CountVertices(mesh.Polys, offset, mesh.MaxVerticesPerPoly);

            polys[poly] = new() {
                FirstVertex = polyVertices.Count,
                VertexCount = (byte)count,
                Area = mesh.Areas[poly],
                Flags = settings.WalkableFlags
            };

            for (var slot = 0; slot < count; slot++) {
                polyVertices.Add(mesh.Polys[offset + slot]);
                polyNeighbours.Add(mesh.Neighbours[offset + slot]);
            }
        }

        return new(tileX, tileZ, vertices, polys, [.. polyVertices], [.. polyNeighbours]) {
            OffMeshConnections = connections.ToArray(),
            Detail = details,
            DetailVertices = detailVertices,
            DetailTriangles = [.. detail.Triangles]
        };
    }
}
