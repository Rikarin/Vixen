// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     A connection that reaches further than the tile next door.
/// </summary>
/// <remarks>
///     A border edge reaches one tile, so relinking used to visit four neighbours and stop. A
///     connection reaches as far as it was authored to, and the tile its far end lands in has no way
///     of knowing which tile declared it — so a long jump attached at the near end and dangled at the
///     far one. These are the cases that has to survive: the far tile loading late, the far tile
///     unloading, and the whole thing being a zip line across most of a level.
/// </remarks>
public sealed class CrossTileConnectionTests {
    const int TileSize = 32;

    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    /// <summary>Two islands with forty metres of nothing between them.</summary>
    /// <remarks>
    ///     Forty metres is four tiles at this tile size, so the two ends of the connection are nowhere
    ///     near each other in the grid — which is the whole point. Nothing is baked in between, so the
    ///     connection is the only way across and a path that gets there proves it was linked.
    /// </remarks>
    static NavTestGeometry Level() => new NavTestGeometry()
        .Floor(0, 0, 10, 10)
        .Floor(50, 0, 60, 10);

    static NavOffMeshConnectionData Zipline() => new() {
        Start = new(5, 0, 5),
        End = new(55, 0, 5),
        Radius = 2f,
        Bidirectional = true,
        Area = NavArea.Walkable,
        Flags = NavPolyFlags.Walk | NavPolyFlags.Jump,
        UserId = 7
    };

    static NavMeshBakeResult Bake() {
        var geometry = Level();

        return NavMeshBaker.BakeTiles(
            geometry.Vertices,
            geometry.Indices,
            NavMeshBaker.Volume(geometry.Vertices, Settings),
            Settings,
            TileSize,
            [],
            [Zipline()]
        );
    }

    static bool CanCross(NavMesh mesh) {
        var query = new NavMeshQuery(mesh);

        if (!query.FindNearestPoly(new(2, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint) ||
            !query.FindNearestPoly(new(58, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint)) {
            return false;
        }

        Span<NavPolyRef> path = stackalloc NavPolyRef[256];

        return query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, path, out _) == NavPathStatus.Complete;
    }

    [Fact]
    public void TheTwoEndsAreFourTilesApart() {
        var result = Bake();
        var mesh = new NavMesh(result.Params);
        var connection = Zipline();

        var (startX, startZ) = mesh.TileCoordinates(connection.Start);
        var (endX, endZ) = mesh.TileCoordinates(connection.End);

        // If this ever stops being true the rest of the file is testing nothing, because four
        // neighbours would be enough.
        Assert.True(
            Math.Abs(startX - endX) + Math.Abs(startZ - endZ) > 1,
            $"The connection runs from tile ({startX}, {startZ}) to ({endX}, {endZ}), which is close enough that a side walk would find it."
        );
    }

    [Fact]
    public void AConnectionAcrossFourTilesIsLinkedAtBothEnds() {
        var result = Bake();
        var mesh = new NavMesh(result.Params);

        foreach (var tile in result.Tiles) {
            mesh.AddTile(tile);
        }

        Assert.True(CanCross(mesh), "The zip line is the only way across forty metres of nothing, and it was not linked.");
    }

    [Fact]
    public void TheFarTileLoadingLastStillFinishesTheConnection() {
        var result = Bake();
        var mesh = new NavMesh(result.Params);
        var connection = Zipline();
        var (endX, endZ) = mesh.TileCoordinates(connection.End);

        NavMeshTileData? far = null;

        // Everything except the tile the far end lands in — the streaming order that used to lose it,
        // because by the time the far tile arrived nothing revisited the tile that declared the jump.
        foreach (var tile in result.Tiles) {
            if (tile.X == endX && tile.Z == endZ) {
                far = tile;

                continue;
            }

            mesh.AddTile(tile);
        }

        Assert.NotNull(far);
        Assert.False(CanCross(mesh), "There is nothing at the far end yet, so there is nowhere to arrive.");

        mesh.AddTile(far);

        Assert.True(CanCross(mesh), "The far end has loaded, so the jump has somewhere to land.");
    }

    [Fact]
    public void UnloadingTheFarTileTakesTheConnectionWithIt() {
        var result = Bake();
        var mesh = new NavMesh(result.Params);
        var connection = Zipline();
        var (endX, endZ) = mesh.TileCoordinates(connection.End);

        foreach (var tile in result.Tiles) {
            mesh.AddTile(tile);
        }

        Assert.True(CanCross(mesh));
        Assert.True(mesh.RemoveTile(endX, endZ));

        // The tile that declared the jump is four tiles away and nothing about it changed. It still
        // has to stop offering a way to somewhere that is no longer loaded.
        Assert.False(CanCross(mesh), "The far end has been unloaded and the jump still claims to reach it.");
    }

    [Fact]
    public void ALevelWithNoConnectionsPaysNothingForThis() {
        var geometry = new NavTestGeometry().Floor(0, 0, 30, 30);

        var result = NavMeshBaker.BakeTiles(
            geometry.Vertices,
            geometry.Indices,
            NavMeshBaker.Volume(geometry.Vertices, Settings),
            Settings,
            TileSize
        );

        var mesh = new NavMesh(result.Params);

        foreach (var tile in result.Tiles) {
            mesh.AddTile(tile);
        }

        // The relink walks the tiles that declare connections, and on a level where none does that
        // walk is over an empty list. Asserted through behaviour rather than through a counter: the
        // ordinary tiled mesh still works exactly as it did.
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint));
        Assert.True(query.FindNearestPoly(new(28, 0, 28), Extents, NavQueryFilter.Default, out var end, out var endPoint));

        Span<NavPolyRef> path = stackalloc NavPolyRef[256];

        Assert.Equal(
            NavPathStatus.Complete,
            query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, path, out _)
        );
    }
}
