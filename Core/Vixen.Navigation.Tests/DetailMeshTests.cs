// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Navigation.Baking;
using Xunit;

namespace Vixen.Navigation.Tests;

/// <summary>
///     How closely the navmesh follows the ground under it.
/// </summary>
/// <remarks>
///     The detail mesh answers exactly one question — given a point over this polygon, how high is the
///     floor — so that is what is measured: the error between what the mesh says and what the geometry
///     is, over ground that is not flat. Everything else about it is a property that must <i>not</i>
///     change: the same polygons, the same adjacency, the same paths.
/// </remarks>
public sealed class DetailMeshTests {
    static readonly Vector3 Extents = new(1f, 4f, 1f);

    /// <summary>A gentle hill. Amplitude and period chosen so no part of it is too steep to walk.</summary>
    static float Hill(float x, float z) => 1.5f * MathF.Sin(x / 6f) * MathF.Sin(z / 6f);

    static NavTestGeometry Terrain() => new NavTestGeometry().Terrain(0, 0, 24, 24, 48, Hill);

    static NavMeshBuildSettings Settings(float sampleDistance) =>
        new() { AgentRadius = 0.6f, DetailSampleDistance = sampleDistance };

    static NavMesh Bake(float sampleDistance) {
        var geometry = Terrain();
        var mesh = new NavMesh(NavMeshParams.Single);

        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(sampleDistance))!);

        return mesh;
    }

    /// <summary>The error between what the mesh says the floor is and what the geometry says.</summary>
    static (float Mean, float Worst) Error(NavMesh mesh) {
        var query = new NavMeshQuery(mesh);
        var total = 0f;
        var worst = 0f;
        var samples = 0;

        for (var z = 4f; z < 20f; z += 0.7f) {
            for (var x = 4f; x < 20f; x += 0.7f) {
                var point = new Vector3(x, Hill(x, z), z);

                if (!query.FindNearestPoly(point, Extents, NavQueryFilter.Default, out var poly, out _) ||
                    !query.GetPolyHeight(poly, point, out var height)) {
                    continue;
                }

                var error = MathF.Abs(height - Hill(x, z));
                total += error;
                worst = MathF.Max(worst, error);
                samples++;
            }
        }

        Assert.True(samples > 100, $"Only {samples} of the hill was reachable, which is not enough to measure over.");

        return (total / samples, worst);
    }

    [Fact]
    public void SamplingTheGroundPutsTheSurfaceOnIt() {
        var without = Error(Bake(0f));
        var with = Error(Bake(1.8f));

        // The number this whole stage exists for. Flat polygons over a hill cut its humps off and lid
        // its dips, and the corners they interpolate between were taken as the highest of the four
        // spans meeting there — so the error is both large and biased upwards.
        Assert.True(
            with.Mean < without.Mean * 0.7f,
            $"Mean height error is {with.Mean:0.000} m with detail and {without.Mean:0.000} m without, "
            + "which is not the improvement the stage is for."
        );

        Assert.True(
            with.Worst < without.Worst,
            $"Worst height error is {with.Worst:0.000} m with detail and {without.Worst:0.000} m without."
        );
    }

    [Fact]
    public void DetailChangesNoPolygonAndNoPath() {
        var geometry = Terrain();

        var without = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(0f))!;
        var with = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(1.8f))!;

        // Detail is read after a path is found and never during one. If it moved a polygon or an
        // edge, it would be a partitioning change wearing a height pass's name.
        Assert.Equal(without.Polys.Length, with.Polys.Length);
        Assert.Equal(without.Vertices.Length, with.Vertices.Length);
        Assert.Equal(without.PolyNeighbours, with.PolyNeighbours);

        for (var index = 0; index < without.Vertices.Length; index++) {
            Assert.Equal(without.Vertices[index], with.Vertices[index]);
        }
    }

    [Fact]
    public void EveryDetailTriangleResolves() {
        var tile = NavMeshBaker.Bake(Terrain().Vertices, Terrain().Indices, Settings(1.8f))!;

        Assert.Equal(tile.Polys.Length, tile.Detail.Length);
        Assert.NotEmpty(tile.DetailVertices);

        for (var poly = 0; poly < tile.Polys.Length; poly++) {
            var detail = tile.Detail[poly];
            var corners = tile.Polys[poly].VertexCount;

            Assert.True(detail.TriangleCount >= corners - 2, $"Polygon {poly} has {corners} corners and {detail.TriangleCount} triangles.");

            for (var triangle = 0; triangle < detail.TriangleCount; triangle++) {
                for (var slot = 0; slot < 3; slot++) {
                    var index = tile.DetailTriangles[((detail.FirstTriangle + triangle) * 3) + slot];

                    if (index < corners) {
                        continue;
                    }

                    var added = detail.FirstVertex + index - corners;

                    Assert.InRange(added, detail.FirstVertex, detail.FirstVertex + detail.VertexCount - 1);
                }
            }
        }
    }

    [Fact]
    public void AFlatFloorNeedsNoDetailVertices() {
        var geometry = new NavTestGeometry().Floor(0, 0, 20, 20);
        var tile = NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, Settings(1.8f))!;

        // Sampling a flat floor finds a flat floor. The triangles are still there — the entry has to
        // exist for the query to find it — but nothing was worth adding a vertex for, which is the
        // right answer and is why the setting is worth turning off for a level built out of floors.
        Assert.Empty(tile.DetailVertices);
        Assert.NotEmpty(tile.DetailTriangles);
    }

    [Fact]
    public void AFlatFloorIsStillReportedOneCellHeightAboveItself() {
        var geometry = new NavTestGeometry().Floor(0, 0, 20, 20);
        var mesh = new NavMesh(NavMeshParams.Single);
        var settings = Settings(1.8f);

        mesh.AddTile(NavMeshBaker.Bake(geometry.Vertices, geometry.Indices, settings)!);

        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(10, 0, 10), Extents, NavQueryFilter.Default, out var poly, out _));
        Assert.True(query.GetPolyHeight(poly, new(10, 0, 10), out var height));

        // Written down deliberately, because it looks like a bug and is not one the detail mesh can
        // fix. A span is the voxel the surface passes through, and its walkable height is the top of
        // that voxel — biased upwards on purpose, since a surface reported below the true floor would
        // place an agent inside it. The detail pass samples those same spans, so it removes the
        // *variation* over uneven ground and leaves this constant exactly where it was. Removing it
        // needs a sub-voxel surface height on the span, which is a rasteriser change.
        Assert.Equal(settings.CellHeight, height, 0.01f);
    }

    [Fact]
    public void DetailSurvivesBeingWrittenAndReadBack() {
        var asset = NavMeshAsset.FromTile(NavMeshBaker.Bake(Terrain().Vertices, Terrain().Indices, Settings(1.8f))!);
        var mesh = asset.ToNavMesh();

        var query = new NavMeshQuery(mesh);
        var point = new Vector3(12, Hill(12, 12), 12);

        Assert.True(query.FindNearestPoly(point, Extents, NavQueryFilter.Default, out var poly, out _));
        Assert.True(query.GetPolyHeight(poly, point, out var height));

        Assert.True(
            MathF.Abs(height - Hill(12, 12)) < 0.25f,
            $"The rebuilt mesh puts the floor at {height:0.000} m where the hill is {Hill(12, 12):0.000} m."
        );
    }
}
