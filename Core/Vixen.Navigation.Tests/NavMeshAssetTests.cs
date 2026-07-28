// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class NavMeshAssetTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static NavTestGeometry Level() =>
        new NavTestGeometry()
            .Floor(0, 0, 30, 30)
            .Box(new(9, 0, 9), new(12, 2, 21))
            .Box(new(18, 0, 9), new(21, 2, 21));

    [Fact]
    public void ATileSurvivesBeingWrittenAndReadBack() {
        var geometry = Level();
        var asset = NavMeshAsset.FromTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        var loaded = Serializer.Read<NavMeshAsset>(Serializer.ToBytes(asset));

        Assert.NotNull(loaded);
        Assert.Equal(asset.Params, loaded.Params);
        Assert.Equal(asset.Tiles.Length, loaded.Tiles.Length);

        var original = asset.Tiles[0];
        var copy = loaded.Tiles[0];

        Assert.Equal(original.X, copy.X);
        Assert.Equal(original.Z, copy.Z);
        Assert.Equal(original.Vertices, copy.Vertices);
        Assert.Equal(original.PolyVertices, copy.PolyVertices);
        Assert.Equal(original.PolyNeighbours, copy.PolyNeighbours);
        Assert.Equal(original.Polys.Length, copy.Polys.Length);

        for (var index = 0; index < original.Polys.Length; index++) {
            Assert.Equal(original.Polys[index].FirstVertex, copy.Polys[index].FirstVertex);
            Assert.Equal(original.Polys[index].VertexCount, copy.Polys[index].VertexCount);
            Assert.Equal(original.Polys[index].Area, copy.Polys[index].Area);
            Assert.Equal(original.Polys[index].Flags, copy.Polys[index].Flags);
        }
    }

    [Fact]
    public void ThePathFoundOnALoadedMeshIsThePathFoundOnTheBakedOne() {
        var geometry = Level();
        var asset = NavMeshAsset.FromTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);
        var loaded = Serializer.Read<NavMeshAsset>(Serializer.ToBytes(asset));

        Span<NavPathPoint> baked = stackalloc NavPathPoint[64];
        Span<NavPathPoint> read = stackalloc NavPathPoint[64];

        var bakedCount = Walk(asset.ToNavMesh(), baked);
        var readCount = Walk(loaded.ToNavMesh(), read);

        Assert.True(bakedCount > 2, "The route round two walls turns more than once, or the test is measuring nothing.");
        Assert.Equal(bakedCount, readCount);

        for (var index = 0; index < bakedCount; index++) {
            Assert.Equal(baked[index].Position, read[index].Position);
        }
    }

    [Fact]
    public void ATiledBakeSurvivesTheRoundTripWithItsGrid() {
        var geometry = Level();
        var bounds = NavMeshBaker.Volume(geometry.Vertices, Settings);
        var asset = NavMeshAsset.FromBake(NavMeshBaker.BakeTiles(geometry.Vertices, geometry.Indices, bounds, Settings, 32));

        Assert.True(asset.Tiles.Length > 1);

        var loaded = Serializer.Read<NavMeshAsset>(Serializer.ToBytes(asset));

        Assert.Equal(asset.Params, loaded.Params);
        Assert.Equal(asset.PolyCount, loaded.PolyCount);

        var mesh = loaded.ToNavMesh();
        Assert.Equal(asset.Tiles.Length, mesh.TileCount);

        // The tiles have to still find each other, which is the part of the load that is not a copy:
        // links are rebuilt from the tile grid rather than stored.
        var query = new NavMeshQuery(mesh);
        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(28, 0, 28), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        var tilesCrossed = new HashSet<int>();

        foreach (var reference in corridor[..count]) {
            tilesCrossed.Add(reference.Tile);
        }

        Assert.True(tilesCrossed.Count > 1);
    }

    [Fact]
    public void TwoBakesOfTheSameLevelSerializeToTheSameBytes() {
        var geometry = Level();
        var bounds = NavMeshBaker.Volume(geometry.Vertices, Settings);

        var first = NavMeshAsset.FromBake(NavMeshBaker.BakeTiles(geometry.Vertices, geometry.Indices, bounds, Settings, 32));
        var second = NavMeshAsset.FromBake(NavMeshBaker.BakeTiles(geometry.Vertices, geometry.Indices, bounds, Settings, 32));

        // What makes a content build's determinism check able to say anything about a navmesh.
        Assert.Equal(Serializer.ToBytes(first), Serializer.ToBytes(second));
    }

    [Fact]
    public void ATileWhoseArraysDisagreeIsRefusedRatherThanLoaded() {
        var broken = new NavMeshTileData {
            Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ],
            Polys = [new() { FirstVertex = 0, VertexCount = 3, Area = NavArea.Walkable, Flags = NavPolyFlags.Walk }],
            PolyVertices = [0, 1, 2],
            PolyNeighbours = [-1]
        };

        var mesh = new NavMesh(NavMeshParams.Single);

        Assert.Throws<ArgumentException>(() => mesh.AddTile(broken));
    }

    static int Walk(NavMesh mesh, Span<NavPathPoint> corners) {
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 15), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(28, 0, 15), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        return query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);
    }
}
