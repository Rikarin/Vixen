// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     What merging regions is allowed to change, and what it is not.
/// </summary>
/// <remarks>
///     The risk of this stage is not that it merges too little — it is that a merged region has a
///     more complicated outline than either of its parts, and every downstream invariant is about that
///     outline. So the tests are the same properties the bake is checked against everywhere else, run
///     at a merge threshold high enough to merge everything the rules allow.
/// </remarks>
public sealed class RegionMergeTests {
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    static NavMeshBuildSettings Settings(int merge) => new() { AgentRadius = 0.6f, MergeRegionArea = merge };

    /// <summary>A room with pillars in it, which is what gives the sweep something to cut around.</summary>
    static NavTestGeometry Level() {
        var geometry = new NavTestGeometry().Floor(0, 0, 40, 40);

        for (var z = 8f; z < 36f; z += 8f) {
            for (var x = 8f; x < 36f; x += 8f) {
                geometry.Box(new(x - 0.75f, 0, z - 0.75f), new(x + 0.75f, 3, z + 0.75f));
            }
        }

        return geometry;
    }

    [Fact]
    public void MergingProducesFewerPolygonsAndTheSamePath() {
        var geometry = Level();

        var apart = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(0))!;
        var merged = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(4_000))!;

        Assert.True(
            merged.Polys.Length < apart.Polys.Length,
            $"Merging left {merged.Polys.Length} polygons against {apart.Polys.Length} — it did nothing."
        );

        Assert.Equal(Walk(apart), Walk(merged), 0.2f);
    }

    [Fact]
    public void AMergedMeshIsStillConvexAndCounterClockwise() {
        var geometry = Level();
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(4_000))!;

        Span<Vector3> corners = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];

        for (var poly = 0; poly < tile.Polys.Length; poly++) {
            var count = tile.Polys[poly].VertexCount;

            for (var corner = 0; corner < count; corner++) {
                corners[corner] = tile.Vertices[tile.PolyVertices[tile.Polys[poly].FirstVertex + corner]];
            }

            var shape = corners[..count];

            Assert.True(NavGeometry.SignedArea2D(shape) > 0, $"Polygon {poly} is wound the wrong way after merging.");

            // Convex: every corner turns the same way. This is the property the funnel and the segment
            // clip are built on, and the one a careless merge would break.
            for (var corner = 0; corner < count; corner++) {
                var a = shape[corner];
                var b = shape[(corner + 1) % count];
                var c = shape[(corner + 2) % count];

                Assert.True(NavGeometry.Side2D(a, b, c) >= -1e-3f, $"Polygon {poly} turns back on itself at corner {corner}.");
            }
        }
    }

    [Fact]
    public void AMergedMeshStillAgreesWithItselfAboutAdjacency() {
        var geometry = Level();
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(4_000))!;

        for (var poly = 0; poly < tile.Polys.Length; poly++) {
            var first = tile.Polys[poly].FirstVertex;
            var count = tile.Polys[poly].VertexCount;

            for (var edge = 0; edge < count; edge++) {
                var neighbour = tile.PolyNeighbours[first + edge];

                if (neighbour < 0) {
                    continue;
                }

                var start = tile.PolyVertices[first + edge];
                var end = tile.PolyVertices[first + ((edge + 1) % count)];
                var matched = false;

                var otherFirst = tile.Polys[neighbour].FirstVertex;
                var otherCount = tile.Polys[neighbour].VertexCount;

                for (var other = 0; other < otherCount; other++) {
                    if (tile.PolyVertices[otherFirst + other] == end &&
                        tile.PolyVertices[otherFirst + ((other + 1) % otherCount)] == start &&
                        tile.PolyNeighbours[otherFirst + other] == poly) {
                        matched = true;

                        break;
                    }
                }

                Assert.True(matched, $"Polygon {poly} claims {neighbour} across edge {edge}, which does not claim it back.");
            }
        }
    }

    [Fact]
    public void MergingIsDeterministic() {
        var geometry = Level();

        var first = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(4_000))!;
        var second = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(4_000))!;

        Assert.Equal(first.Vertices, second.Vertices);
        Assert.Equal(first.PolyVertices, second.PolyVertices);
        Assert.Equal(first.PolyNeighbours, second.PolyNeighbours);
    }

    [Fact]
    public void AnIslandNothingCanReachIsStillRemoved() {
        // A separate floor far from the main one, smaller than the minimum region area even before
        // erosion takes a bite out of it.
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 20, 20)
            .Floor(40, 0, 41, 41.5f);

        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(20))!);

        var query = new NavMeshQuery(mesh);

        Assert.False(
            query.FindNearestPoly(new(40.5f, 0, 40.5f), Extents, NavQueryFilter.Default, out _, out _),
            "A one-metre island survived, which is a place a path can end and an agent can never be."
        );
    }

    /// <summary>How long the path across the level is, which merging must not change.</summary>
    static float Walk(NavMeshTileData data) {
        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(data);

        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(2, 0, 2), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(38, 0, 38), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[512];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[64];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, corridor[..count], corners);

        var length = 0f;

        for (var index = 1; index < cornerCount; index++) {
            length += Vector3.Distance(corners[index - 1].Position, corners[index].Position);
        }

        return length;
    }
}
