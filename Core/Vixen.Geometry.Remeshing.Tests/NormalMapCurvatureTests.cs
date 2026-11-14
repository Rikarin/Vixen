// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>A curvature map taken from the baked normals rather than the source's faces (Patina #148).</summary>
/// <remarks>
///     The scene has a closed form: a flat low sheet under a high sheet with a spherical cap of radius one standing
///     out of it, so the cap's mean curvature is one and the flat around it is zero. A dome has to read positive in
///     both directions, so a sign turned the wrong way along either axis cancels the other and reads near zero.
/// </remarks>
public class NormalMapCurvatureTests {
    const int Resolution = 64;
    const float Radius = 1f;

    static readonly BakeSettings Settings = new() {
        Resolution = Resolution,
        Gutter = 2,
        Units = BakeUnits.Absolute,
        SearchRadius = 0.6f,
        RearDistance = 0.1f,
        Maps = MeshMaps.Curvature,
        CurvatureSource = CurvatureSource.NormalMap
    };

    /// <summary>The cap reads its mean curvature, about one over its radius, and the flat around it reads none.</summary>
    [Fact]
    public void A_dome_on_the_high_mesh_reads_one_over_its_radius_and_the_flat_reads_nothing() {
        var maps = MapBaker.Bake(Domed(), Low(), Settings);

        var top = Around(maps, 0.5f, 0.5f);
        var flat = Around(maps, 0.1f, 0.12f);

        Assert.InRange(top, 0.75f / Radius, 1.25f / Radius);
        Assert.InRange(flat, -0.05f, 0.05f);
        Assert.True(maps.CurvatureRange > 0f, "The map has a scale.");
    }

    /// <summary>A hollow reads negative, as a crease does from the mesh.</summary>
    [Fact]
    public void A_hollow_reads_negative() {
        var maps = MapBaker.Bake(Domed(-1f), Low(), Settings with { RearDistance = 0.6f, SearchRadius = 0.1f });

        Assert.InRange(Around(maps, 0.5f, 0.5f), -1.25f / Radius, -0.75f / Radius);
    }

    /// <summary>
    ///     An object-space normal map bakes the same curvature: the normals are measured in the tangent frame for it,
    ///     whatever space the normal map is written in.
    /// </summary>
    [Fact]
    public void The_normal_maps_space_does_not_change_the_curvature() {
        var tangent = MapBaker.Bake(Domed(), Low(), Settings);
        var tilted = Tilted(Low());
        var objectSpace = MapBaker.Bake(Tilted(Domed()), tilted, Settings with { Space = BakeSpace.Object });

        Assert.Equal(Around(tangent, 0.5f, 0.5f), Around(objectSpace, 0.5f, 0.5f), 1);
    }

    /// <summary>The mean over a few texels round a place on the atlas, in UV.</summary>
    static float Around(BakedMaps maps, float u, float v) {
        var x = (int)(u * maps.Resolution);
        var y = (int)(v * maps.Resolution);
        var sum = 0f;
        var count = 0;

        for (var dy = -2; dy <= 2; dy++) {
            for (var dx = -2; dx <= 2; dx++) {
                var index = (y + dy) * maps.Resolution + x + dx;

                if (maps.Coverage[index]) {
                    sum += maps.Curvature![index];
                    count++;
                }
            }
        }

        Assert.True(count > 0, "Some texel there is covered.");

        return sum / count;
    }

    static EditMesh Low() => MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);

    /// <summary>A sheet two across with a cap of the radius rising out of its middle, or sinking for a negative side.</summary>
    static EditMesh Domed(float side = 1f) {
        var mesh = TransferFixtures.Grid(160, 2f, static _ => 0);
        const float Rim = 0.6f;
        var drop = MathF.Sqrt(Radius * Radius - Rim * Rim);

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            var point = mesh.Positions[vertex];
            var reach = point.X * point.X + point.Y * point.Y;
            var height = reach < Rim * Rim ? MathF.Sqrt(Radius * Radius - reach) - drop : 0f;

            mesh.MovePosition(vertex, new(point.X, point.Y, side * height));
        }

        return mesh;
    }

    /// <summary>The mesh turned a quarter about x, so object space is no longer the sheet's own frame.</summary>
    static EditMesh Tilted(EditMesh mesh) {
        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            var point = mesh.Positions[vertex];

            mesh.MovePosition(vertex, new(point.X, -point.Z, point.Y));
        }

        return mesh;
    }
}
