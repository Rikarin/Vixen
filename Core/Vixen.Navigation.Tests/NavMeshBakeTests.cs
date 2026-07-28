// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

public sealed class NavMeshBakeTests {
    static readonly NavMeshBuildSettings Settings = new() {
        CellSize = 0.3f,
        CellHeight = 0.2f,
        AgentRadius = 0.6f,
        AgentHeight = 2f,
        AgentMaxClimb = 0.9f
    };

    [Fact]
    public void AFlatFloorBakesIntoPolygons() {
        var geometry = new NavTestGeometry().Floor(0, 0, 10, 10);
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings);

        Assert.NotNull(tile);
        Assert.True(tile.Polys.Length > 0, "A ten-metre floor produced no polygons.");
    }

    [Fact]
    public void ThePolygonsAreCounterClockwiseInXz() {
        var geometry = new NavTestGeometry().Floor(0, 0, 10, 10);
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;

        Span<Vector3> corners = stackalloc Vector3[NavMesh.MaxVerticesPerPoly];

        for (var poly = 0; poly < tile.Polys.Length; poly++) {
            var count = tile.Polys[poly].VertexCount;

            for (var corner = 0; corner < count; corner++) {
                corners[corner] = tile.Vertices[tile.PolyVertices[tile.Polys[poly].FirstVertex + corner]];
            }

            Assert.True(NavGeometry.SignedArea2D(corners[..count]) > 0, $"Polygon {poly} is wound the wrong way.");
        }
    }

    [Fact]
    public void TheWalkableSurfaceIsErodedByTheAgentRadius() {
        var geometry = new NavTestGeometry().Floor(0, 0, 10, 10);
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;

        // Every vertex has to be at least a radius inside the floor, because that is the whole
        // promise erosion makes: a point on the mesh is a place a body of that width fits.
        foreach (var vertex in tile.Vertices) {
            Assert.True(vertex.X >= Settings.AgentRadius - Settings.CellSize, $"{vertex} is too close to the west edge.");
            Assert.True(vertex.X <= 10 - Settings.AgentRadius + Settings.CellSize, $"{vertex} is too close to the east edge.");
            Assert.True(vertex.Z >= Settings.AgentRadius - Settings.CellSize, $"{vertex} is too close to the south edge.");
            Assert.True(vertex.Z <= 10 - Settings.AgentRadius + Settings.CellSize, $"{vertex} is too close to the north edge.");
        }
    }

    [Fact]
    public void GeometryStandingOnTheFloorIsNotWalkableAroundItsBase() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 10, 10)
            .Box(new(4, 0, 4), new(6, 2, 6));

        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;
        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(tile);

        var query = new NavMeshQuery(mesh);
        var extents = new Vector3(0.4f, 2f, 0.4f);

        Assert.False(
            query.FindNearestPoly(new(5, 0, 5), extents, NavQueryFilter.Default, out _, out _),
            "The middle of a two-metre-tall box is not somewhere an agent can stand."
        );

        Assert.True(
            query.FindNearestPoly(new(1, 0, 1), extents, NavQueryFilter.Default, out _, out _),
            "The open floor away from the box is."
        );
    }

    [Fact]
    public void ASecondBakeOfTheSameGeometryProducesTheSameMesh() {
        var geometry = new NavTestGeometry()
            .Floor(0, 0, 12, 12)
            .Box(new(3, 0, 3), new(5, 2, 7));

        var first = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;
        var second = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;

        Assert.Equal(first.Vertices, second.Vertices);
        Assert.Equal(first.PolyVertices, second.PolyVertices);
        Assert.Equal(first.PolyNeighbours, second.PolyNeighbours);
    }

    [Fact]
    public void AdjacentPolygonsAgreeAboutBeingAdjacent() {
        var geometry = new NavTestGeometry().Floor(0, 0, 20, 20);
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings)!;

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

                var neighbourFirst = tile.Polys[neighbour].FirstVertex;
                var neighbourCount = tile.Polys[neighbour].VertexCount;

                for (var other = 0; other < neighbourCount; other++) {
                    var otherStart = tile.PolyVertices[neighbourFirst + other];
                    var otherEnd = tile.PolyVertices[neighbourFirst + ((other + 1) % neighbourCount)];

                    if (otherStart == end && otherEnd == start && tile.PolyNeighbours[neighbourFirst + other] == poly) {
                        matched = true;

                        break;
                    }
                }

                Assert.True(matched, $"Polygon {poly} claims {neighbour} across edge {edge}, which does not claim it back.");
            }
        }
    }
}
