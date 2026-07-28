// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class HeightfieldTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(1f, 4f, 1f);

    static Heightfield Rasterize(NavTestGeometry geometry, float slope = 45f) {
        var bounds = NavMeshBaker.Volume(geometry.Vertices, Settings);
        var field = new Heightfield(bounds, Settings.CellSize, Settings.CellHeight);
        var areas = new byte[geometry.Indices.Length / 3];

        Heightfield.MarkWalkableTriangles(slope, geometry.Vertices, geometry.Indices, areas, NavArea.Walkable);
        field.RasterizeTriangles(geometry.Vertices, geometry.Indices, areas, Settings.WalkableClimbInCells);

        return field;
    }

    [Fact]
    public void AFlatFloorRasterizesIntoOneSpanPerColumn() {
        var field = Rasterize(new NavTestGeometry().Floor(0, 0, 3, 3));

        Assert.True(field.SpanCount > 0, "A three-metre floor rasterized into nothing.");
        Assert.True(field.SpanCount <= field.Width * field.Depth, "There are more spans than columns to hold one each.");

        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                var spans = 0;

                for (var index = field.First(x, z); index >= 0; index = field.Span(index).Next) {
                    spans++;
                }

                Assert.True(spans <= 1, $"Column ({x}, {z}) holds {spans} spans, and a flat floor is one surface.");
            }
        }
    }

    [Fact]
    public void GroundSteeperThanTheAgentCanStandOnIsNotWalkable() {
        // A ramp climbing 10 metres over 4 — about 68 degrees.
        var geometry = new NavTestGeometry();
        geometry.Wall(new(0, 0, 0), new(4, 10, 4));

        var field = Rasterize(geometry);
        var walkable = 0;

        for (var z = 0; z < field.Depth; z++) {
            for (var x = 0; x < field.Width; x++) {
                for (var index = field.First(x, z); index >= 0; index = field.Span(index).Next) {
                    if (field.Span(index).Area != NavArea.Null) {
                        walkable++;
                    }
                }
            }
        }

        Assert.Equal(0, walkable);
    }

    [Fact]
    public void GroundUnderALowCeilingIsNotWalkable() {
        // A floor with a slab a metre above it: standing room for nobody two metres tall.
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 10, 10)
            .Floor(3, 3, 7, 7, 1f);

        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;
        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(tile);
        var query = new NavMeshQuery(mesh);

        Assert.True(
            query.FindNearestPoly(new(5, 0, 5), new(0.2f, 0.5f, 0.2f), NavQueryFilter.Default, out _, out _)
                is var found && !found,
            "The floor under a one-metre ceiling is not somewhere a two-metre agent can stand."
        );

        Assert.True(
            query.FindNearestPoly(new(1, 0, 1), Extents, NavQueryFilter.Default, out _, out _),
            "The open floor beside it still is."
        );
    }

    [Fact]
    public void AStepTheAgentCanClimbConnectsTheTwoLevels() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 10, 5)
            .Floor(0, 5, 10, 10, 0.4f)
            .Wall(new(0, 0, 5), new(10, 0.4f, 5));

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(5, 0.4f, 8), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Assert.False(start.IsNull);
        Assert.False(end.IsNull);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out _);

        Assert.Equal(NavPathStatus.Complete, status);
    }

    [Fact]
    public void AStepTooTallToClimbDoesNot() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 10, 5)
            .Floor(0, 5, 10, 10, 2.5f)
            .Wall(new(0, 0, 5), new(10, 2.5f, 5));

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(5, 2.5f, 8), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Assert.False(start.IsNull);
        Assert.False(end.IsNull);
        Assert.NotEqual(start, end);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out _);

        Assert.Equal(NavPathStatus.Partial, status);
    }
}
