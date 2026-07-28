// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Baking;

/// <summary>Names an obstacle held by a <see cref="NavTileCache" />.</summary>
/// <remarks>
///     A slot and a generation, so that a handle to a removed obstacle names nothing rather than
///     naming whichever obstacle was added into its place — the same reason a
///     <see cref="NavPolyRef" /> carries a salt.
/// </remarks>
public readonly record struct NavObstacleHandle(int Index, int Generation) {
    /// <summary>The handle that names nothing.</summary>
    public static NavObstacleHandle Null => default;

    /// <summary>Whether this is <see cref="Null" />.</summary>
    public bool IsNull => Generation == 0;
}

/// <summary>
///     The voxelised level, kept, so that a crate can be dropped on it and the tiles under the crate
///     rebuilt without touching the geometry again.
/// </summary>
/// <remarks>
///     <para>
///         A bake is two halves. The first turns triangles into a walkable surface: rasterise, filter,
///         compact, resolve the neighbours. The second decides what shape that surface is: erode,
///         partition, trace, polygonise. <b>An obstacle changes only the second half</b> — the ground
///         under a crate is the same ground, it is just not walkable while the crate is there — so
///         this keeps the first half per tile and replays the second.
///     </para>
///     <para>
///         <b>What that costs is memory, and it is not small.</b> A compact heightfield is a span per
///         walkable surface per column, so an eighty-metre level at a 0.3 m cell size is on the order
///         of a megabyte. That is the trade being made: a level that wants dynamic obstacles keeps its
///         voxels resident, and one that does not should bake and drop them. Recast's tile cache
///         compresses its layers to avoid exactly this; compression is a thing to add when a project
///         has measured that it needs it, and adding it first would be inventing a requirement.
///     </para>
///     <para>
///         <b>Rebuilding is not free and is not meant to be hidden.</b> <see cref="Update" /> takes a
///         budget in tiles, and a caller that hands it one tile per frame gets an obstacle that
///         appears over several frames rather than a frame that stalls. Tiles are the unit because a
///         tile is the unit the mesh can swap: <see cref="NavMesh.AddTile" /> relinks the borders, and
///         a path already crossing the replaced tile is invalidated by its polygons' salt rather than
///         by anything having to hunt for it.
///     </para>
/// </remarks>
public sealed class NavTileCache {
    readonly NavMeshBuildSettings settings;
    readonly CachedTile[] tiles;
    readonly List<Obstacle> obstacles = [];
    readonly List<int> dirty = [];
    readonly List<NavAreaVolume> applied = [];

    NavTileCache(NavMeshParams parameters, NavMeshBuildSettings settings, CachedTile[] tiles, int columns, int rows) {
        Params = parameters;
        this.settings = settings;
        this.tiles = tiles;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>The tile grid, ready to be handed to a <see cref="NavMesh" />.</summary>
    public NavMeshParams Params { get; }

    /// <summary>How many tiles across the grid is.</summary>
    public int Columns { get; }

    /// <summary>How many tiles deep.</summary>
    public int Rows { get; }

    /// <summary>How many tiles are waiting to be rebuilt.</summary>
    public int PendingTiles => dirty.Count;

    /// <summary>Roughly how much memory the kept voxels occupy.</summary>
    /// <remarks>
    ///     The whole cost of this design, so it is a number a project can read rather than a warning
    ///     in a comment. Counted as the arrays actually held — the spans, the column index, the areas,
    ///     and the untouched copy of the areas every rebuild starts from — and not as anything the
    ///     allocator adds on top.
    /// </remarks>
    public long ResidentBytes {
        get {
            long total = 0;

            foreach (var tile in tiles) {
                if (tile.Surface is null) {
                    continue;
                }

                total += (long)tile.Surface.Spans.Length * 12;
                total += (long)tile.Surface.Cells.Length * 8;
                total += tile.Surface.Areas.Length + tile.Pristine.Length;
            }

            return total;
        }
    }

    /// <summary>How many obstacles are in place.</summary>
    public int ObstacleCount {
        get {
            var count = 0;

            foreach (var obstacle in obstacles) {
                if (obstacle.Shape is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Voxelises a level and keeps it.</summary>
    /// <param name="vertices">The geometry's vertices, in world space.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="bounds">The volume to cover.</param>
    /// <param name="settings">How finely, and for what agent.</param>
    /// <param name="tileSize">How wide a tile is, in voxels.</param>
    /// <param name="volumes">Authored areas, applied to every rebuild as well as to the first bake.</param>
    /// <param name="connections">Authored ways off the surface, each held by the tile its start is in.</param>
    /// <returns>The cache.</returns>
    /// <remarks>
    ///     Smaller tiles than a static bake would use. A tile is the unit of a rebuild, so its size is
    ///     the latency of dropping a crate — and unlike a static bake, the extra work each tile does
    ///     outside itself is paid once here rather than every time.
    /// </remarks>
    public static NavTileCache Build(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        BoundingBox bounds,
        NavMeshBuildSettings settings,
        int tileSize = 48,
        ReadOnlySpan<NavAreaVolume> volumes = default,
        ReadOnlySpan<NavOffMeshConnectionData> connections = default
    ) {
        settings.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 8);

        var extent = tileSize * settings.CellSize;
        var border = settings.WalkableRadiusInCells + 3;

        var columns = Math.Max(1, (int)MathF.Ceiling((bounds.Maximum.X - bounds.Minimum.X) / extent));
        var rows = Math.Max(1, (int)MathF.Ceiling((bounds.Maximum.Z - bounds.Minimum.Z) / extent));

        var cached = new CachedTile[columns * rows];

        for (var z = 0; z < rows; z++) {
            for (var x = 0; x < columns; x++) {
                var minimum = new Vector3(
                    bounds.Minimum.X + (x * extent),
                    bounds.Minimum.Y,
                    bounds.Minimum.Z + (z * extent)
                );

                var tile = new BoundingBox(minimum, new(minimum.X + extent, bounds.Maximum.Y, minimum.Z + extent));
                var placement = TilePlacement.For(tile, settings, x, z, border);

                var owned = new List<NavOffMeshConnectionData>();

                foreach (var connection in connections) {
                    if ((int)MathF.Floor((connection.Start.X - bounds.Minimum.X) / extent) == x &&
                        (int)MathF.Floor((connection.Start.Z - bounds.Minimum.Z) / extent) == z) {
                        owned.Add(connection);
                    }
                }

                var surface = vertices.IsEmpty || indices.Length < 3
                    ? null
                    : NavMeshBaker.BuildSurface(vertices, indices, in placement, settings);

                cached[x + (z * columns)] = new() {
                    Placement = placement,
                    Surface = surface,
                    // The areas as voxelisation left them, before anything decided a shape. Every
                    // rebuild starts from this copy, because erosion and carving both destroy it.
                    Pristine = surface is null ? [] : (byte[])surface.Areas.Clone(),
                    Volumes = volumes.ToArray(),
                    Connections = [.. owned]
                };
            }
        }

        return new(new(bounds.Minimum, extent, extent, settings.CellSize * 0.5f, settings.AgentMaxClimb), settings, cached, columns, rows);
    }

    /// <summary>Builds every tile and returns a mesh holding them.</summary>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     The starting state, with no obstacles in it. After this the mesh is the caller's and the
    ///     cache only ever touches it through <see cref="Update" />.
    /// </remarks>
    public NavMesh CreateNavMesh() {
        var mesh = new NavMesh(Params);

        foreach (var tile in tiles) {
            if (Rebuild(tile) is { } data) {
                mesh.AddTile(data);
            }
        }

        return mesh;
    }

    /// <summary>Adds an obstacle and marks the tiles it touches for rebuilding.</summary>
    /// <param name="shape">
    ///     The volume. An area of <see cref="NavArea.Null" /> carves it out of the surface; anything
    ///     else stamps a cost onto ground that stays walkable.
    /// </param>
    /// <returns>A handle, or <see cref="NavObstacleHandle.Null" /> if the shape is outside the cache.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape" /> is null.</exception>
    public NavObstacleHandle AddObstacle(NavAreaVolume shape) {
        ArgumentNullException.ThrowIfNull(shape);

        var touched = MarkDirty(shape);

        if (touched == 0) {
            return NavObstacleHandle.Null;
        }

        for (var index = 0; index < obstacles.Count; index++) {
            if (obstacles[index].Shape is null) {
                obstacles[index] = obstacles[index] with { Shape = shape, Generation = obstacles[index].Generation + 1 };

                return new(index, obstacles[index].Generation);
            }
        }

        obstacles.Add(new() { Shape = shape, Generation = 1 });

        return new(obstacles.Count - 1, 1);
    }

    /// <summary>Removes an obstacle and marks the tiles it touched for rebuilding.</summary>
    /// <param name="handle">The obstacle.</param>
    /// <returns><see langword="false" /> if the handle names nothing, which a stale one does.</returns>
    public bool RemoveObstacle(NavObstacleHandle handle) {
        if (handle.IsNull || handle.Index < 0 || handle.Index >= obstacles.Count) {
            return false;
        }

        var obstacle = obstacles[handle.Index];

        if (obstacle.Generation != handle.Generation || obstacle.Shape is null) {
            return false;
        }

        MarkDirty(obstacle.Shape);

        // The generation is not bumped here. It is bumped when the slot is reused, so that a handle
        // to this obstacle keeps naming this slot's history rather than becoming valid again if
        // nothing is ever added.
        obstacles[handle.Index] = obstacle with { Shape = null };

        return true;
    }

    /// <summary>Rebuilds up to a budget of the tiles waiting for it, into a live mesh.</summary>
    /// <param name="mesh">The mesh to swap the rebuilt tiles into.</param>
    /// <param name="maxTiles">How many to do. The default is one, which is the point of the budget.</param>
    /// <returns>How many were rebuilt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    /// <remarks>
    ///     A tile that comes out with no polygons — a crate over the whole of it — is removed rather
    ///     than replaced, which is the same thing the mesh does for a tile that never had any.
    /// </remarks>
    public int Update(NavMesh mesh, int maxTiles = 1) {
        ArgumentNullException.ThrowIfNull(mesh);

        var done = 0;

        while (done < maxTiles && dirty.Count > 0) {
            var index = dirty[0];
            dirty.RemoveAt(0);

            var tile = tiles[index];
            var data = Rebuild(tile);

            mesh.RemoveTile(tile.Placement.TileX, tile.Placement.TileZ);

            if (data is not null) {
                mesh.AddTile(data);
            }

            done++;
        }

        return done;
    }

    /// <summary>Puts every tile a volume's footprint reaches on the rebuild queue.</summary>
    /// <returns>How many tiles that was.</returns>
    int MarkDirty(NavAreaVolume shape) {
        var extent = Params.TileWidth;
        var touched = 0;

        // Widened by the agent radius, because the erosion around an obstacle reaches that far past
        // its own footprint — a crate against a tile border thins the mesh in the tile next door.
        var margin = settings.AgentRadius + settings.CellSize;

        var minimumX = (int)MathF.Floor((shape.Bounds.Minimum.X - margin - Params.Origin.X) / extent);
        var maximumX = (int)MathF.Floor((shape.Bounds.Maximum.X + margin - Params.Origin.X) / extent);
        var minimumZ = (int)MathF.Floor((shape.Bounds.Minimum.Z - margin - Params.Origin.Z) / Params.TileDepth);
        var maximumZ = (int)MathF.Floor((shape.Bounds.Maximum.Z + margin - Params.Origin.Z) / Params.TileDepth);

        for (var z = Math.Max(0, minimumZ); z <= Math.Min(Rows - 1, maximumZ); z++) {
            for (var x = Math.Max(0, minimumX); x <= Math.Min(Columns - 1, maximumX); x++) {
                var index = x + (z * Columns);
                touched++;

                if (!dirty.Contains(index)) {
                    dirty.Add(index);
                }
            }
        }

        return touched;
    }

    /// <summary>Replays the shaping half of the bake over a tile's kept surface.</summary>
    NavMeshTileData? Rebuild(CachedTile tile) {
        if (tile.Surface is null) {
            return null;
        }

        tile.Pristine.CopyTo(tile.Surface.Areas, 0);

        applied.Clear();
        applied.AddRange(tile.Volumes);

        foreach (var obstacle in obstacles) {
            if (obstacle.Shape is not null) {
                applied.Add(obstacle.Shape);
            }
        }

        var placement = tile.Placement;

        return NavMeshBaker.BuildTile(tile.Surface, in placement, settings, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(applied), tile.Connections);
    }

    /// <summary>One tile's kept voxels, and what it is always rebuilt with.</summary>
    sealed class CachedTile {
        public TilePlacement Placement { get; init; }

        public CompactHeightfield? Surface { get; init; }

        public byte[] Pristine { get; init; } = [];

        public NavAreaVolume[] Volumes { get; init; } = [];

        public NavOffMeshConnectionData[] Connections { get; init; } = [];
    }

    /// <summary>One obstacle slot. A null shape is a free slot that remembers what it used to be.</summary>
    readonly record struct Obstacle {
        public NavAreaVolume? Shape { get; init; }

        public int Generation { get; init; }
    }
}
