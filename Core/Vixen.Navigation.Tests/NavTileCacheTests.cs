// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     Dropping something on the level and taking it away again.
/// </summary>
/// <remarks>
///     The properties that matter are that an obstacle actually blocks — including the agent's own
///     width, which is the part a naive carve gets wrong — and that removing one puts the level back
///     exactly as it was. The rest is bookkeeping: which tiles a shape touches, and that the rebuild
///     budget is respected rather than politely ignored.
/// </remarks>
public sealed class NavTileCacheTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    /// <summary>Two rooms joined by a corridor, so there is exactly one way across and it can be blocked.</summary>
    static NavTestGeometry Level() => new NavTestGeometry()
        .Floor(0, 0, 30, 30)
        .Box(new(0, 0, 13), new(12, 3, 17))
        .Box(new(18, 0, 13), new(30, 3, 17));

    static BoundingBox Bounds() => NavMeshBaker.Volume(Level().Vertices, Settings);

    static NavTileCache Cache() {
        var geometry = Level();

        return NavTileCache.Build(geometry.Vertices, geometry.Indices, Bounds(), Settings, tileSize: 32);
    }

    static bool CanCross(NavMesh mesh) {
        var query = new NavMeshQuery(mesh);

        if (!query.FindNearestPoly(new(15, 0, 4), Extents, NavQueryFilter.Default, out var start, out var startPoint) ||
            !query.FindNearestPoly(new(15, 0, 26), Extents, NavQueryFilter.Default, out var end, out var endPoint)) {
            return false;
        }

        Span<NavPolyRef> path = stackalloc NavPolyRef[256];

        return query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, path, out _) == NavPathStatus.Complete;
    }

    static void Drain(NavTileCache cache, NavMesh mesh) {
        while (cache.PendingTiles > 0) {
            cache.Update(mesh, 4);
        }
    }

    [Fact]
    public void ACachedLevelStartsOutTheSameAsABakedOne() {
        var geometry = Level();
        var baked = NavMeshBaker.BakeTiles(geometry.Vertices, geometry.Indices, Bounds(), Settings, 32);
        var cached = Cache().CreateNavMesh();

        var polys = 0;

        foreach (var tile in cached.Tiles) {
            polys += tile.SurfacePolyCount;
        }

        var expected = 0;

        foreach (var tile in baked.Tiles) {
            expected += tile.Polys.Length;
        }

        // The cache is the same pipeline with a pause in the middle, so it had better be the same
        // mesh. If this ever diverges, one of the two halves has grown a dependency on the other.
        Assert.Equal(expected, polys);
        Assert.True(CanCross(cached), "The corridor is open before anything is put in it.");
    }

    [Fact]
    public void ACrateInTheCorridorClosesIt() {
        var cache = Cache();
        var mesh = cache.CreateNavMesh();

        Assert.True(CanCross(mesh));

        var handle = cache.AddObstacle(NavAreaVolume.Cylinder(new(15, 0, 15), 2.5f, 2f, NavArea.Null));

        Assert.False(handle.IsNull);
        Assert.True(cache.PendingTiles > 0, "Adding an obstacle marked no tile for rebuilding.");

        Drain(cache, mesh);

        Assert.False(CanCross(mesh), "The corridor is three metres wide and a five-metre crate is in it.");

        Assert.True(cache.RemoveObstacle(handle));
        Drain(cache, mesh);

        Assert.True(CanCross(mesh), "Taking the crate away puts the corridor back.");
    }

    [Fact]
    public void AnObstacleIsErodedByTheAgentRadiusLikeEverythingElse() {
        var cache = Cache();
        var mesh = cache.CreateNavMesh();

        cache.AddObstacle(NavAreaVolume.Cylinder(new(8, 0, 8), 2f, 2f, NavArea.Null));
        Drain(cache, mesh);

        var query = new NavMeshQuery(mesh);

        // The whole promise erosion makes is that a point on the mesh is a place the agent's body
        // fits. Carving after eroding would leave the mesh flush against the crate and the agent
        // standing half inside it, so the carve happens first and this is what says so.
        //
        // Tested at 2.2 m rather than at the 2.6 m erosion asks for, because contour simplification
        // may then move a wall back by up to MaxSimplificationError — 1.3 voxels, 0.39 m here — and
        // does so for every wall in every bake, static or not. Measured, the nearest floor around
        // this crate ranges from 2.30 m to 2.80 m.
        for (var angle = 0; angle < 16; angle++) {
            var radians = angle / 16f * MathF.Tau;
            var point = new Vector3(8f + (2.2f * MathF.Cos(radians)), 0, 8f + (2.2f * MathF.Sin(radians)));

            Assert.False(
                query.FindNearestPoly(point, new(0.05f, 1f, 0.05f), NavQueryFilter.Default, out _, out _),
                $"There is floor at {point}, which is inside a two-metre crate at (8, 8) once the agent's own radius is counted."
            );
        }
    }

    [Fact]
    public void TheKeptVoxelsAreCountedRatherThanHandWaved() {
        var cache = Cache();

        // The whole cost of the design, so it is readable rather than a warning in a comment. A
        // thirty-metre level at a 0.3 m cell size is under a megabyte; an eighty-metre one is 2.2 MB.
        Assert.True(cache.ResidentBytes > 0, "A voxelised level holds no voxels.");
        Assert.True(
            cache.ResidentBytes < 4L * 1024 * 1024,
            $"A thirty-metre level is holding {cache.ResidentBytes / 1024 / 1024} MB of voxels, which is more than the shape of the data allows."
        );
    }

    [Fact]
    public void RebuildingRespectsItsBudget() {
        var cache = Cache();
        var mesh = cache.CreateNavMesh();

        // Big enough to touch every tile of the level, so there is certainly more than one to do.
        cache.AddObstacle(NavAreaVolume.Box(new(new(2, -1, 2), new(28, 3, 28)), NavArea.Null));

        var pending = cache.PendingTiles;
        Assert.True(pending > 1, $"A level-sized obstacle dirtied {pending} tiles.");

        Assert.Equal(1, cache.Update(mesh));
        Assert.Equal(pending - 1, cache.PendingTiles);

        Drain(cache, mesh);
        Assert.Equal(0, cache.PendingTiles);
    }

    [Fact]
    public void AnObstacleOutsideTheLevelIsRefusedRatherThanQueued() {
        var cache = Cache();

        Assert.True(cache.AddObstacle(NavAreaVolume.Cylinder(new(500, 0, 500), 1f, 2f, NavArea.Null)).IsNull);
        Assert.Equal(0, cache.PendingTiles);
        Assert.Equal(0, cache.ObstacleCount);
    }

    [Fact]
    public void AStaleHandleRemovesNothing() {
        var cache = Cache();
        var mesh = cache.CreateNavMesh();

        var first = cache.AddObstacle(NavAreaVolume.Cylinder(new(8, 0, 8), 1f, 2f, NavArea.Null));
        Assert.True(cache.RemoveObstacle(first));
        Drain(cache, mesh);

        // The slot is free and the next obstacle takes it, at a new generation. The old handle names
        // that slot's history, not its contents.
        var second = cache.AddObstacle(NavAreaVolume.Cylinder(new(20, 0, 8), 1f, 2f, NavArea.Null));

        Assert.Equal(first.Index, second.Index);
        Assert.False(cache.RemoveObstacle(first));
        Assert.Equal(1, cache.ObstacleCount);
    }

    [Fact]
    public void AVolumeWithAnAreaStampsACostRatherThanCarving() {
        const byte Water = 8;

        var cache = Cache();
        var mesh = cache.CreateNavMesh();

        cache.AddObstacle(NavAreaVolume.Box(new(new(2, -1, 2), new(10, 3, 10)), Water));
        Drain(cache, mesh);

        var query = new NavMeshQuery(mesh);

        Assert.True(
            query.FindNearestPoly(new(6, 0, 6), Extents, NavQueryFilter.Default, out var poly, out _),
            "Painting an area is not the same as removing the ground."
        );

        Assert.True(mesh.TryGetPolyAttributes(poly, out var area, out _));
        Assert.Equal(Water, area);
    }
}
