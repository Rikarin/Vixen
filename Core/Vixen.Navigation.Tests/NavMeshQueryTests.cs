// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class NavMeshQueryTests {
    static readonly NavMeshBuildSettings Settings = new() { AgentRadius = 0.6f };
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static (NavMesh Mesh, NavMeshQuery Query) OpenFloor(float size = 20f) {
        var geometry = new NavTestGeometry().Floor(0, 0, size, size);
        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return (mesh, new(mesh));
    }

    /// <summary>A room split in two by a wall, with a gap in the middle of it.</summary>
    static (NavMesh Mesh, NavMeshQuery Query) DividedRoom() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 20, 20)
            .Box(new(0, 0, 9.5f), new(8, 2, 10.5f))
            .Box(new(12, 0, 9.5f), new(20, 2, 10.5f));

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        return (mesh, new(mesh));
    }

    [Fact]
    public void TheNearestPolygonToAPointOnTheFloorIsUnderIt() {
        var (_, query) = OpenFloor();

        Assert.True(query.FindNearestPoly(new(5, 1, 5), Extents, NavQueryFilter.Default, out var poly, out var point));
        Assert.False(poly.IsNull);
        Assert.True(MathF.Abs(point.X - 5) < 0.01f && MathF.Abs(point.Z - 5) < 0.01f, $"{point}");
        Assert.True(MathF.Abs(point.Y) < 0.3f, $"The floor is at zero, and the nearest point on it is at {point.Y}.");
    }

    [Fact]
    public void APathAcrossAnOpenFloorIsAStraightLine() {
        var (_, query) = OpenFloor();

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(17, 0, 17), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[64];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);

        Assert.Equal(startPoint, corners[0].Position);
        Assert.Equal(endPoint, corners[cornerCount - 1].Position);

        // Not necessarily two corners: the corridor comes out of a search over polygon edges, and the
        // straight line between the ends need not lie inside it. What the funnel promises is the
        // shortest path *within the corridor*, which across an open floor is very nearly the straight
        // line — a few per cent is the room the polygon layout is allowed.
        var length = 0f;

        for (var index = 1; index < cornerCount; index++) {
            length += Vector3.Distance(corners[index - 1].Position, corners[index].Position);
        }

        var direct = Vector3.Distance(startPoint, endPoint);
        Assert.True(length < direct * 1.05f, $"The path across an open floor is {length}, against a straight line of {direct}.");
    }

    [Fact]
    public void APathAroundAWallGoesThroughTheGapInIt() {
        var (_, query) = DividedRoom();

        query.FindNearestPoly(new(3, 0, 3), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(3, 0, 17), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[64];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);

        Assert.True(cornerCount > 2, "A path around a wall has to turn at least once.");

        var length = 0f;

        for (var index = 1; index < cornerCount; index++) {
            length += Vector3.Distance(corners[index - 1].Position, corners[index].Position);
        }

        Assert.True(length > Vector3.Distance(startPoint, endPoint), "Going around cannot be shorter than going through.");

        // Every corner has to be on the walkable side of the wall — the gap is at x in [8, 12], and
        // a corner at the wall's depth outside it would mean the funnel cut through the geometry.
        foreach (var corner in corners[..cornerCount]) {
            if (corner.Position.Z is > 9f and < 11f) {
                Assert.True(corner.Position.X is > 7f and < 13f, $"A corner at {corner.Position} is inside the wall.");
            }
        }
    }

    [Fact]
    public void EveryCornerOfAPathIsOnTheMesh() {
        var (_, query) = DividedRoom();

        query.FindNearestPoly(new(3, 0, 3), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(17, 0, 17), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[64];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);

        foreach (var corner in corners[..cornerCount]) {
            Assert.True(
                query.FindNearestPoly(corner.Position, new(0.05f, 0.5f, 0.05f), NavQueryFilter.Default, out _, out _),
                $"The corner at {corner.Position} is not on the mesh."
            );
        }
    }

    [Fact]
    public void ARaycastAcrossOpenFloorHitsNothing() {
        var (_, query) = OpenFloor();

        query.FindNearestPoly(new(3, 0, 3), Extents, NavQueryFilter.Default, out var start, out var startPoint);

        Assert.True(query.Raycast(start, startPoint, new(15, 0, 15), NavQueryFilter.Default, out var hit));
        Assert.False(hit.Hit);
        Assert.Equal(1f, hit.Distance, 3);
    }

    [Fact]
    public void ARaycastIntoAWallStopsAtIt() {
        var (_, query) = DividedRoom();

        query.FindNearestPoly(new(3, 0, 3), Extents, NavQueryFilter.Default, out var start, out var startPoint);

        Assert.True(query.Raycast(start, startPoint, new(3, 0, 17), NavQueryFilter.Default, out var hit));
        Assert.True(hit.Hit, "A wall stands between the two points.");
        Assert.True(hit.Distance < 1f);
        Assert.True(hit.Position.Z < 9.5f, $"The wall starts at z = 9.5 and the ray stopped at {hit.Position}.");
        Assert.True(hit.Normal.Z < -0.5f, $"The wall the ray hit faces back the way it came, not {hit.Normal}.");
    }

    [Fact]
    public void WalkingIntoAWallSlidesAlongItRatherThanThroughIt() {
        var (_, query) = DividedRoom();

        query.FindNearestPoly(new(3, 0, 8), Extents, NavQueryFilter.Default, out var start, out var startPoint);

        Assert.True(query.MoveAlongSurface(start, startPoint, new(3, 0, 12), NavQueryFilter.Default, out var position, out var poly));
        Assert.False(poly.IsNull);
        Assert.True(position.Z < 9.5f, $"The agent ended up at {position}, which is inside the wall.");

        Assert.True(
            query.FindNearestPoly(position, new(0.05f, 0.5f, 0.05f), NavQueryFilter.Default, out _, out _),
            $"The agent ended up at {position}, which is not on the mesh."
        );
    }

    [Fact]
    public void AFilterThatExcludesEverythingFindsNothing() {
        var (_, query) = OpenFloor();
        var filter = new NavQueryFilter { IncludeFlags = NavPolyFlags.Swim };

        Assert.False(query.FindNearestPoly(new(5, 0, 5), Extents, filter, out _, out _));
    }

    [Fact]
    public void ADisabledPolygonIsNotCrossed() {
        // The gap in the wall, closed by flags rather than by geometry — the cheap half of dynamic
        // navigation, and the thing a door is.
        var (mesh, query) = DividedRoom();
        var closed = 0;

        foreach (var tile in mesh.Tiles) {
            for (var index = 0; index < tile.PolyCount; index++) {
                var reference = NavMesh.ReferenceOf(tile, index);
                query.ClosestPointOnPoly(reference, new(10, 0, 10), out var closest, out var over);

                if (over && MathF.Abs(closest.Z - 10) < 1.5f) {
                    mesh.SetPolyFlags(reference, NavPolyFlags.Disabled);
                    closed++;
                }
            }
        }

        Assert.True(closed > 0, "Nothing was closed, so the test would pass for the wrong reason.");

        query.FindNearestPoly(new(3, 0, 3), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(3, 0, 17), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Partial, status);

        foreach (var reference in corridor[..count]) {
            Assert.True(mesh.TryGetPolyAttributes(reference, out _, out var flags));
            Assert.False(flags.HasFlag(NavPolyFlags.Disabled), "The path goes through a polygon the filter excludes.");
        }
    }

    [Fact]
    public void AnExpensiveAreaIsWalkedRoundRatherThanThrough() {
        // A band of costly ground across the middle of the room, from the south wall to the north.
        // Going round it is longer in distance and cheaper in cost, which is the whole point of an
        // area cost, so the path should leave the band alone.
        const byte Swamp = 10;

        var geometry = new NavTestGeometry().Floor(0, 0, 20, 20);
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;

        var marked = 0;

        for (var poly = 0; poly < tile.Polys.Length; poly++) {
            var centre = Vector3.Zero;
            var corners = tile.Polys[poly].VertexCount;

            for (var corner = 0; corner < corners; corner++) {
                centre += tile.Vertices[tile.PolyVertices[tile.Polys[poly].FirstVertex + corner]];
            }

            centre /= corners;

            if (centre.Z is > 8f and < 12f) {
                tile.Polys[poly].Area = Swamp;
                marked++;
            }
        }

        Assert.True(marked > 0, "No polygon was marked, so the test would pass for the wrong reason.");

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(tile);
        var query = new NavMeshQuery(mesh);

        var filter = new NavQueryFilter();
        filter.SetAreaCost(Swamp, 40f);

        query.FindNearestPoly(new(10, 0, 2), Extents, filter, out var start, out var startPoint);
        query.FindNearestPoly(new(10, 0, 18), Extents, filter, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        var status = query.FindPath(start, end, startPoint, endPoint, filter, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        var swampy = 0;

        foreach (var reference in corridor[..count]) {
            mesh.TryGetPolyAttributes(reference, out var area, out _);

            if (area == Swamp) {
                swampy++;
            }
        }

        // The band spans the room, so some of it has to be crossed; what the cost buys is crossing as
        // little of it as the layout allows, rather than walking its length.
        Span<NavPolyRef> cheapest = stackalloc NavPolyRef[512];
        query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, cheapest, out var plainCount);

        var plainSwampy = 0;

        foreach (var reference in cheapest[..plainCount]) {
            mesh.TryGetPolyAttributes(reference, out var area, out _);

            if (area == Swamp) {
                plainSwampy++;
            }
        }

        Assert.True(
            swampy <= plainSwampy,
            $"The costed path crosses {swampy} expensive polygons and the uncosted one crosses {plainSwampy}."
        );
    }

    [Fact]
    public void APathToAnUnreachableRoomIsPartialRatherThanNothing() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 10, 10)
            .Floor(20, 0, 30, 10);

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(25, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Assert.False(start.IsNull);
        Assert.False(end.IsNull);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Partial, status);
        Assert.True(count >= 1);
        Assert.Equal(start, corridor[0]);
    }

    [Fact]
    public void APathAcrossTilesCrossesTheirBorder() {
        var geometry = new NavTestGeometry().Floor(0, 0, 40, 40);
        var bounds = NavMeshBaker.Volume(geometry.Vertices, Settings);
        var baked = NavMeshBaker.BakeTiles(geometry.Vertices, geometry.Indices, bounds, Settings, 32);

        Assert.True(baked.Tiles.Count > 1, "A forty-metre floor on ten-metre tiles is more than one tile.");

        var mesh = new NavMesh(baked.Params);

        foreach (var tile in baked.Tiles) {
            mesh.AddTile(tile);
        }

        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(37, 0, 37), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[1024];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        var tilesCrossed = new HashSet<int>();

        foreach (var reference in corridor[..count]) {
            tilesCrossed.Add(reference.Tile);
        }

        Assert.True(tilesCrossed.Count > 1, "A path from one corner of the level to the other stayed inside one tile.");
    }

    [Fact]
    public void AReferenceIntoAnUnloadedTileStopsResolving() {
        var geometry = new NavTestGeometry().Floor(0, 0, 10, 10);
        var mesh = new NavMesh(NavMeshParams.Single);
        var tile = mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);
        var reference = NavMesh.ReferenceOf(tile, 0);

        Assert.True(mesh.IsValid(reference));

        mesh.RemoveTile(0, 0);

        Assert.False(mesh.IsValid(reference));

        // The slot is reused, and the salt is what stops the old reference from naming its occupant.
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!);

        Assert.False(mesh.IsValid(reference));
    }
}
