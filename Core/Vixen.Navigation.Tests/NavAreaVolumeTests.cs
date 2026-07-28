// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class NavAreaVolumeTests {
    const byte Water = 9;

    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static NavMesh Bake(params NavAreaVolume[] volumes) {
        var geometry = new NavTestGeometry().Floor(0, 0, 20, 20);
        var mesh = new NavMesh(NavMeshParams.Single);

        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings, volumes)!);

        return mesh;
    }

    [Fact]
    public void AVolumeStampsItsAreaOnTheSurfaceInsideIt() {
        var mesh = Bake(NavAreaVolume.Box(new(new(8, -1, 0), new(12, 3, 20)), Water));
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(10, 0, 10), Extents, NavQueryFilter.Default, out var inside, out _));
        Assert.True(mesh.TryGetPolyAttributes(inside, out var insideArea, out _));
        Assert.Equal(Water, insideArea);

        Assert.True(query.FindNearestPoly(new(3, 0, 10), Extents, NavQueryFilter.Default, out var outside, out _));
        Assert.True(mesh.TryGetPolyAttributes(outside, out var outsideArea, out _));
        Assert.Equal(NavArea.Walkable, outsideArea);
    }

    [Fact]
    public void APolygonIsNeverPartlyInsideAVolume() {
        // The boundary is a region boundary, because the area is stamped before the surface is
        // partitioned. If it were stamped afterwards, a polygon straddling x = 8 would have to be
        // called one thing or the other and the cost of crossing it would be a lie either way.
        var mesh = Bake(NavAreaVolume.Box(new(new(8, -1, 0), new(12, 3, 20)), Water));
        var query = new NavMeshQuery(mesh);

        Span<Vector3> corners = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];

        foreach (var tile in mesh.Tiles) {
            for (var index = 0; index < tile.PolyCount; index++) {
                var reference = NavMesh.ReferenceOf(tile, index);
                var count = mesh.GetPolyVertices(reference, corners);

                mesh.TryGetPolyAttributes(reference, out var area, out _);

                var minimum = float.MaxValue;
                var maximum = float.MinValue;

                for (var corner = 0; corner < count; corner++) {
                    minimum = MathF.Min(minimum, corners[corner].X);
                    maximum = MathF.Max(maximum, corners[corner].X);
                }

                // A polygon may touch the boundary; it may not span it. One cell of slack, because
                // the stamp is decided at column centres.
                if (area == Water) {
                    Assert.True(minimum >= 8f - Settings.CellSize, $"A water polygon reaches x = {minimum}.");
                    Assert.True(maximum <= 12f + Settings.CellSize, $"A water polygon reaches x = {maximum}.");
                } else {
                    Assert.True(
                        maximum <= 8f + Settings.CellSize || minimum >= 12f - Settings.CellSize,
                        $"A dry polygon spans x = {minimum} to {maximum}, which crosses the water."
                    );
                }
            }
        }
    }

    [Fact]
    public void AnExpensiveVolumeIsWalkedRound() {
        // Water across the middle of the room, but only two thirds of the way: crossing it is 4 m of
        // very expensive ground, and going round it is about 12 m of cheap ground.
        var mesh = Bake(NavAreaVolume.Box(new(new(8, -1, 0), new(12, 3, 14)), Water));
        var query = new NavMeshQuery(mesh);

        var filter = new NavQueryFilter();
        filter.SetAreaCost(Water, 50f);

        query.FindNearestPoly(new(4, 0, 7), Extents, filter, out var start, out var startPoint);
        query.FindNearestPoly(new(16, 0, 7), Extents, filter, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        var status = query.FindPath(start, end, startPoint, endPoint, filter, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[64];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);

        var wet = 0;

        foreach (var reference in corridor[..count]) {
            mesh.TryGetPolyAttributes(reference, out var area, out _);

            if (area == Water) {
                wet++;
            }
        }

        Assert.Equal(0, wet);
        Assert.True(cornerCount > 2, "Going round the water means turning.");
    }

    [Fact]
    public void AVolumeAboveTheSurfaceStampsNothing() {
        var mesh = Bake(NavAreaVolume.Box(new(new(8, 5, 0), new(12, 9, 20)), Water));
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(10, 0, 10), Extents, NavQueryFilter.Default, out var poly, out _));
        Assert.True(mesh.TryGetPolyAttributes(poly, out var area, out _));
        Assert.Equal(NavArea.Walkable, area);
    }

    [Fact]
    public void AConvexFootprintStampsItsOwnShape() {
        // A triangle covering the north-east quarter of the room.
        Span<Vector3> footprint = [new(10, 0, 10), new(19, 0, 10), new(19, 0, 19)];
        var mesh = Bake(NavAreaVolume.Convex(footprint, -1f, 3f, Water));
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(17, 0, 12), Extents, NavQueryFilter.Default, out var inside, out _));
        mesh.TryGetPolyAttributes(inside, out var insideArea, out _);
        Assert.Equal(Water, insideArea);

        // Inside the triangle's bounding box, outside the triangle.
        Assert.True(query.FindNearestPoly(new(11, 0, 18), Extents, NavQueryFilter.Default, out var outside, out _));
        mesh.TryGetPolyAttributes(outside, out var outsideArea, out _);
        Assert.Equal(NavArea.Walkable, outsideArea);
    }

    [Fact]
    public void AFootprintThatIsNotAPolygonIsRefused() {
        Span<Vector3> two = [Vector3.Zero, Vector3.UnitX];

        Assert.Throws<ArgumentException>(() => {
            Span<Vector3> footprint = [Vector3.Zero, Vector3.UnitX];

            return NavAreaVolume.Convex(footprint, 0f, 1f, Water);
        });

        Assert.Equal(2, two.Length);
    }
}
