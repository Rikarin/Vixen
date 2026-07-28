// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     Watershed partitioning, and the thing it can do that the sweep cannot: produce a region with a
///     hole in it.
/// </summary>
/// <remarks>
///     The shape of the regions is a quality question and is measured rather than asserted — a test
///     that pinned the polygon count would fail on every improvement. What <i>is</i> asserted is the
///     correctness the hole introduces: an obstacle a region has grown round must still be an
///     obstacle, and the two partitioners must agree about where an agent can go.
/// </remarks>
public sealed class WatershedTests {
    /// <summary>Deliberately shallow. The slab making the doughnut's hole is itself walkable on top,
    ///     and a vertical extent that reached it would find floor a metre above the hole and call the
    ///     question answered.</summary>
    static readonly Vector3 Extents = new(1f, 0.4f, 1f);

    static NavMeshBuildSettings Settings(NavMeshPartitioning partitioning) =>
        new() { AgentRadius = 0.6f, Partitioning = partitioning };

    /// <summary>A floor with a slab a metre above the middle of it: walkable all the way round, not through.</summary>
    /// <remarks>
    ///     A hole in a region is not something a test can ask for directly — it depends on where the
    ///     water level happens to fall. This is the shape that produces one reliably: the unwalkable
    ///     middle is small and central, so the ring around it is a single ridge that floods as one
    ///     region and meets itself on the far side.
    /// </remarks>
    static NavTestGeometry Doughnut() => new NavTestGeometry()
        .Floor(0, 0, 10, 10)
        .Floor(3, 3, 7, 7, 1f);

    static NavMesh Bake(NavTestGeometry geometry, NavMeshPartitioning partitioning) {
        var mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(partitioning))!);

        return mesh;
    }

    [Fact]
    public void TheMiddleOfADoughnutIsNotFloor() {
        var query = new NavMeshQuery(Bake(Doughnut(), NavMeshPartitioning.Watershed));

        Assert.False(
            query.FindNearestPoly(new(5, 0, 5), Extents, NavQueryFilter.Default, out _, out _),
            "The floor under a one-metre ceiling is not walkable, whichever side of it the region grew round."
        );

        Assert.True(
            query.FindNearestPoly(new(1, 0, 1), Extents, NavQueryFilter.Default, out _, out _),
            "The ring around it is."
        );
    }

    [Fact]
    public void APathRoundADoughnutGoesRound() {
        var mesh = Bake(Doughnut(), NavMeshPartitioning.Watershed);
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(1, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint));
        Assert.True(query.FindNearestPoly(new(9, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint));

        Span<NavPolyRef> path = stackalloc NavPolyRef[64];
        var status = query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, path, out var count);

        Assert.Equal(NavPathStatus.Complete, status);

        Span<NavPathPoint> corners = stackalloc NavPathPoint[32];
        var cornerCount = query.FindStraightPath(startPoint, endPoint, path[..count], corners);

        // Straight across is four metres of ceiling. Every corner has to be clear of the middle, and
        // there has to be at least one of them or the path went through the hole.
        Assert.True(cornerCount > 2, "Crossing a doughnut means turning.");

        for (var index = 0; index < cornerCount; index++) {
            var position = corners[index].Position;

            Assert.False(
                position.X is > 3f and < 7f && position.Z is > 3f and < 7f,
                $"The path turned at {position}, which is under the slab."
            );
        }
    }

    [Fact]
    public void MergingAHoleCutsItsAreaOut() {
        // A ten-by-ten outline with a two-by-two hole, wound opposite as the tracer would leave them.
        var outer = Contour(0, 0, 10, 10, clockwise: false);
        var hole = Contour(4, 4, 6, 6, clockwise: true);
        var contours = new List<Contour> { outer, hole };

        ContourHoles.Merge(contours);

        var merged = Assert.Single(contours);

        // Both loops are closed, so the merged ring is the two outlines plus the two ends of the slit.
        Assert.Equal(outer.VertexCount + hole.VertexCount + 2, merged.VertexCount);

        // And the slit has no width, so what is enclosed is exactly the ring: a merge that had
        // concatenated the two rather than cutting one out of the other would measure a hundred.
        Assert.Equal(96, Math.Abs(Area(merged)));
    }

    [Fact]
    public void NoPolygonCoversAPillar() {
        var query = new NavMeshQuery(Bake(Level(), NavMeshPartitioning.Watershed));

        // Half a metre. A pillar is 1.5 m across and the surface is eroded by another 0.6 m, so the
        // nearest floor to a pillar's middle is 1.35 m away and this cannot reach it by accident.
        var extents = new Vector3(0.5f, 0.5f, 0.5f);

        for (var z = 8f; z < 36f; z += 8f) {
            for (var x = 8f; x < 36f; x += 8f) {
                Assert.False(
                    query.FindNearestPoly(new(x, 0, z), extents, NavQueryFilter.Default, out _, out _),
                    $"There is floor inside the pillar at ({x}, {z}). A region that grew round it has "
                    + "had its hole merged in, and merging a hole must not fill it."
                );
            }
        }
    }

    [Fact]
    public void WatershedFollowsAShapeTheSweepCannot() {
        // A ring of blocks approximating a circle: nothing about its boundary is axis-aligned, which
        // is the case the whole partitioner exists for. On an axis-aligned level the sweep wins, and
        // the README says so with the numbers.
        var geometry = new NavTestGeometry().Floor(0, 0, 40, 40);

        for (var angle = 0; angle < 48; angle++) {
            var radians = angle / 48f * MathF.Tau;
            var x = 20f + (10f * MathF.Cos(radians));
            var z = 20f + (10f * MathF.Sin(radians));

            geometry.Box(new(x - 0.7f, 0, z - 0.7f), new(x + 0.7f, 3, z + 0.7f));
        }

        var watershed = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(NavMeshPartitioning.Watershed))!;
        var monotone = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(NavMeshPartitioning.Monotone))!;

        Assert.True(
            watershed.Polys.Length < monotone.Polys.Length,
            $"Watershed produced {watershed.Polys.Length} polygons on a round obstacle and the sweep "
            + $"{monotone.Polys.Length}. Round is the shape a row sweep has no answer for."
        );
    }

    [Fact]
    public void TheDistanceFieldPeaksAwayFromTheWalls() {
        var geometry = new NavTestGeometry().Floor(0, 0, 20, 20);
        var settings = Settings(NavMeshPartitioning.Watershed);
        var field = Compact(geometry, settings);

        field.BuildDistanceField();

        var middle = field.Cells[(field.Width / 2) + (field.Depth / 2 * field.Width)];
        var edge = field.Cells[2 + (2 * field.Width)];

        Assert.True(middle.Count > 0 && edge.Count > 0);

        Assert.True(
            field.Distances[middle.Index] > field.Distances[edge.Index],
            "The middle of a room is further from a wall than the corner of it is, which is the whole "
            + "reason the flood starts there."
        );

        // Taken before the blur, so it is an upper bound on what is in the field rather than a value
        // in it. That is what the flood wants: a water level to start above.
        Assert.True(field.MaximumDistance >= field.Distances.Max());
    }

    /// <summary>The pillared room the rest of the suite measures against.</summary>
    static NavTestGeometry Level() {
        var geometry = new NavTestGeometry().Floor(0, 0, 40, 40);

        for (var z = 8f; z < 36f; z += 8f) {
            for (var x = 8f; x < 36f; x += 8f) {
                geometry.Box(new(x - 0.75f, 0, z - 0.75f), new(x + 0.75f, 3, z + 0.75f));
            }
        }

        return geometry;
    }

    static CompactHeightfield Compact(NavTestGeometry geometry, NavMeshBuildSettings settings) {
        var bounds = NavMeshBaker.Volume(geometry.Vertices, settings);
        var field = new Heightfield(bounds, settings.CellSize, settings.CellHeight);
        var areas = new byte[geometry.Indices.Length / 3];

        Heightfield.MarkWalkableTriangles(settings.AgentMaxSlope, geometry.Vertices, geometry.Indices, areas, settings.WalkableArea);
        field.RasterizeTriangles(geometry.Vertices, geometry.Indices, areas, settings.WalkableClimbInCells);
        field.FilterLowHangingWalkableObstacles(settings.WalkableClimbInCells);
        field.FilterLedgeSpans(settings.WalkableHeightInCells, settings.WalkableClimbInCells);
        field.FilterWalkableLowHeightSpans(settings.WalkableHeightInCells);

        var compact = CompactHeightfield.Build(field, settings.WalkableHeightInCells, settings.WalkableClimbInCells);
        compact.ErodeWalkableArea(settings.WalkableRadiusInCells);

        return compact;
    }

    static Contour Contour(int minimumX, int minimumZ, int maximumX, int maximumZ, bool clockwise) {
        int[] vertices = clockwise
            ? [minimumX, 0, minimumZ, 0, minimumX, 0, maximumZ, 0, maximumX, 0, maximumZ, 0, maximumX, 0, minimumZ, 0]
            : [minimumX, 0, minimumZ, 0, maximumX, 0, minimumZ, 0, maximumX, 0, maximumZ, 0, minimumX, 0, maximumZ, 0];

        return new() { Vertices = vertices, Region = 1, Area = NavArea.Walkable };
    }

    /// <summary>Twice the signed area of a contour, by the shoelace.</summary>
    static long Area(Contour contour) {
        var count = contour.VertexCount;
        long total = 0;

        for (int index = 0, previous = count - 1; index < count; previous = index++) {
            total += ((long)contour.Vertices[previous * 4] * contour.Vertices[(index * 4) + 2])
                - ((long)contour.Vertices[index * 4] * contour.Vertices[(previous * 4) + 2]);
        }

        return total / 2;
    }
}
