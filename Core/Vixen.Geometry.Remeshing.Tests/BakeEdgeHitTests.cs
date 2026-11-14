// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>A ray through an edge two triangles share strikes one of them, at any scale (Patina #287).</summary>
/// <remarks>
///     ⚠ <b>Each triangle's test rounds on its own</b>, so a ray through the line they share could read as just outside
///     both, and the miss count of a bake moved with the model's scale. The fan below is skewed and off the origin so
///     that no coordinate is a round number the rounding would be kind to.
/// </remarks>
public sealed class BakeEdgeHitTests {
    /// <summary>A fan of triangles around a centre, every spoke an edge two of them share.</summary>
    static (Vector3[] Points, int[] Indices) Fan(float scale, int spokes) {
        var centre = new Vector3(0.3137f, -0.2718f, 0.1414f) * scale;
        List<Vector3> points = [centre];
        List<int> indices = [];

        for (var spoke = 0; spoke < spokes; spoke++) {
            var angle = spoke * MathF.Tau / spokes + 0.1234f;
            var reach = 0.7f + 0.29f * MathF.Sin(spoke * 2.3f);

            // Tilted out of the plane, so the fan is not flat and no edge lies along an axis.
            points.Add(
                centre
                + new Vector3(MathF.Cos(angle) * reach, MathF.Sin(angle) * reach, 0.17f * MathF.Cos(spoke * 1.7f))
                * scale
            );
        }

        for (var spoke = 0; spoke < spokes; spoke++) {
            indices.AddRange([0, 1 + spoke, 1 + (spoke + 1) % spokes]);
        }

        return ([..points], [..indices]);
    }

    /// <summary>Every ray aimed at a point of a shared spoke strikes the fan, by both queries.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1e-3f)]
    [InlineData(1e+3f)]
    [InlineData(37.7f)]
    public void A_ray_through_a_shared_edge_strikes_one_of_its_triangles(float scale) {
        const int Spokes = 23;
        const int Steps = 97;
        var (points, indices) = Fan(scale, Spokes);
        var tree = new BakeTree(points, indices);
        var missed = 0;
        var blocked = 0;
        var rays = 0;

        for (var spoke = 0; spoke < Spokes; spoke++) {
            for (var step = 1; step < Steps; step++) {
                var on = Vector3.Lerp(points[0], points[1 + spoke], step / (float)Steps);

                foreach (var lean in (Vector3[])
                         [new(0.013f, 0.021f, 1f), new(-0.41f, 0.29f, 0.83f), new(0.3f, -0.6f, -0.7f)]) {
                    var direction = Vector3.Normalize(lean) * scale;
                    var origin = on - direction * 0.5f;

                    rays++;
                    missed += tree.Raycast(origin, direction).Triangle < 0 ? 1 : 0;
                    blocked += tree.Occluded(origin, direction) ? 1 : 0;
                }
            }
        }

        Assert.Equal(0, missed);
        Assert.Equal(rays, blocked);
    }

    /// <summary>A hit on an edge comes back with weights that are a point of the triangle.</summary>
    [Fact]
    public void A_hit_on_an_edge_has_weights_on_the_triangle() {
        var (points, indices) = Fan(1f, 23);
        var tree = new BakeTree(points, indices);

        for (var spoke = 0; spoke < 23; spoke++) {
            var on = Vector3.Lerp(points[0], points[1 + spoke], 0.37f);
            var hit = tree.Raycast(on - new Vector3(0.1f, 0.2f, 0.5f), new(0.2f, 0.4f, 1f));

            Assert.True(hit.Triangle >= 0);
            Assert.InRange(hit.Barycentric.X, 0f, 1f);
            Assert.InRange(hit.Barycentric.Y, 0f, 1f);
            Assert.InRange(hit.Barycentric.Z, 0f, 1f);
            Assert.Equal(1f, hit.Barycentric.X + hit.Barycentric.Y + hit.Barycentric.Z, 5);
        }
    }

    /// <summary>A jittered sheet, turned off every axis and moved off the origin, laid out over most of the atlas.</summary>
    static EditMesh Sheet(float scale) {
        var sheet = TransferFixtures.Grid(9, 2f, static _ => 0, 0.3f);
        var turn = Matrix4x4.FromRotationX(0.61f) * Matrix4x4.FromRotationY(0.37f) * Matrix4x4.FromRotationZ(0.23f);
        var coordinates = new Vector2[sheet.CornerCount];

        for (var face = 0; face < sheet.FaceCount; face++) {
            var loop = sheet.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var point = sheet.Positions[loop[index]];

                coordinates[sheet.Faces[face].Start + index] = new(point.X * 0.45f + 0.5f, point.Y * 0.45f + 0.5f);
            }
        }

        for (var vertex = 0; vertex < sheet.PositionCount; vertex++) {
            var moved = Matrix4x4.TransformPosition(sheet.Positions[vertex], turn) + new Vector3(0.71f, -0.33f, 0.19f);

            sheet.MovePosition(vertex, moved * scale);
        }

        sheet.SetTexCoords(coordinates);

        return sheet;
    }

    /// <summary>
    ///     A mesh baked onto itself is struck at every texel a chart claims, its outline too, at any scale. A texel
    ///     past a triangle's edge is sampled on the edge, which is exactly where the hit test rounds either way: a
    ///     sheet's outline lost about one texel in a hundred.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1e-3f)]
    [InlineData(1e+3f)]
    [InlineData(37.7f)]
    public void A_mesh_baked_onto_itself_misses_nothing_on_its_outline(float scale) {
        foreach (var mesh in (EditMesh[])
                 [Sheet(scale), TransferFixtures.Scaled(HardEdgeBakeTests.HardBox(0.83f), scale)]) {
            var maps = MapBaker.Bake(
                mesh,
                new EditMesh(mesh),
                new() { Resolution = 96, Gutter = 0, Supersampling = 2, Maps = MeshMaps.Opacity }
            );

            Assert.True(maps.Covered > 0);
            Assert.Equal(0, maps.Missed);
        }
    }
}
