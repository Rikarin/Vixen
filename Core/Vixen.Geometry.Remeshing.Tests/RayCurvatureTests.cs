// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>A curvature map measured by secondary rays round where each ray lands (Patina #148).</summary>
/// <remarks>
///     Two scenes with a closed form. A spherical cap of radius one out of a flat sheet reads one on top and nothing
///     on the flat, or minus one sunk. A ridge with walls a cell wide, too steep for the mesh's own curvature to be
///     anything but its two rows of vertices, reads a bend about as tight as the sampling radius at its rim and the
///     same bend the other way at its foot.
/// </remarks>
public class RayCurvatureTests {
    const int Resolution = 64;
    const float Sampling = 0.05f;

    static readonly BakeSettings Settings = new() {
        Resolution = Resolution,
        Gutter = 2,
        Units = BakeUnits.Absolute,
        SearchRadius = 0.6f,
        RearDistance = 0.1f,
        Maps = MeshMaps.Curvature,
        CurvatureSource = CurvatureSource.Rays,
        CurvatureSampling = Sampling
    };

    /// <summary>The cap reads one over its radius, and the flat reads none.</summary>
    [Fact]
    public void A_dome_reads_one_over_its_radius_and_the_flat_reads_nothing() {
        var maps = MapBaker.Bake(Domed(), Low(), Settings);

        Assert.InRange(Around(maps, 0.5f, 0.5f), 0.8f, 1.2f);
        Assert.InRange(Around(maps, 0.1f, 0.12f), -0.05f, 0.05f);
        Assert.True(maps.CurvatureRange > 0f, "The map has a scale.");
    }

    /// <summary>A hollow reads negative, as a crease does from the mesh.</summary>
    [Fact]
    public void A_hollow_reads_negative() {
        var maps = MapBaker.Bake(Domed(-1f), Low(), Settings with { RearDistance = 0.6f, SearchRadius = 0.1f });

        Assert.InRange(Around(maps, 0.5f, 0.5f), -1.2f, -0.8f);
    }

    /// <summary>
    ///     A hard rim reads convex and the foot of its wall concave, each a bend near one over the sampling radius:
    ///     past the rim the rays down meet nothing, and at the foot the rays across meet the wall.
    /// </summary>
    [Fact]
    public void A_hard_edge_reads_a_bend_at_the_sampling_radius_and_its_foot_the_other_way() {
        var maps = MapBaker.Bake(Ridge(), Low(), Settings with { Resolution = 128 });

        // The ridge's wall crosses row 41 of 128: the rows within a radius past it are on its top, those before on
        // the floor.
        var rim = Enumerable.Range(41, 5).Max(row => Row(maps, row));
        var foot = Enumerable.Range(36, 5).Min(row => Row(maps, row));

        Assert.InRange(rim, 0.3f / Sampling, 2f / Sampling);
        Assert.InRange(foot, -2f / Sampling, -0.1f / Sampling);
        Assert.InRange(Row(maps, 64), -0.05f, 0.05f);
    }

    /// <summary>
    ///     Within its part, the floor reads no crease at the foot of a ridge that is another part (Patina #300): the rays
    ///     across pass where the wall stands, as the exploded bake has no wall there, and the ridge's own rim keeps its
    ///     bend. Without parts the setting reads the whole source, as before.
    /// </summary>
    [Fact]
    public void Within_its_part_the_floor_reads_no_crease_where_another_part_stands() {
        var ridge = Ridge();
        BakeParts parts = new([..Raised(ridge)], [..Enumerable.Repeat(0, Low().FaceCount)]);
        var settings = Settings with { Resolution = 128, Parts = parts };

        var whole = MapBaker.Bake(ridge, Low(), settings);
        var within = MapBaker.Bake(ridge, Low(), settings with { CurvatureWithinPart = true });
        var unmatched = MapBaker.Bake(ridge, Low(), Settings with { Resolution = 128, CurvatureWithinPart = true });

        Assert.InRange(Enumerable.Range(36, 5).Min(row => Row(whole, row)), -2f / Sampling, -0.1f / Sampling);
        Assert.All(Enumerable.Range(20, 19), row => Assert.InRange(Row(within, row), -0.05f, 0.05f));
        Assert.InRange(Enumerable.Range(36, 5).Min(row => Row(unmatched, row)), -2f / Sampling, -0.1f / Sampling);
    }

    /// <summary>Each face's part: 1 for one that rises off the floor, the ridge's walls and top, and 0 for the floor.</summary>
    static IEnumerable<int> Raised(EditMesh mesh) {
        foreach (var face in mesh.Faces) {
            var raised = false;

            for (var corner = face.Start; corner < face.Start + face.Count; corner++) {
                raised |= mesh.Positions[mesh.Corners[corner]].Z > 0f;
            }

            yield return raised ? 1 : 0;
        }
    }

    /// <summary>A radius of nothing measures nothing, and is refused rather than baked flat.</summary>
    [Fact]
    public void A_sampling_radius_of_nothing_is_refused() {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapBaker.Bake(Domed(), Low(), Settings with { CurvatureSampling = 0f })
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapBaker.Bake(Domed(), Low(), Settings with { CurvatureRays = 0 })
        );
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

    /// <summary>The mean over a row's covered texels, which a ridge running across the rows makes all alike.</summary>
    static float Row(BakedMaps maps, int row) {
        var sum = 0f;
        var count = 0;

        for (var x = 0; x < maps.Resolution; x++) {
            var index = row * maps.Resolution + x;

            if (maps.Coverage[index]) {
                sum += maps.Curvature![index];
                count++;
            }
        }

        Assert.True(count > 0, "Some texel in the row is covered.");

        return sum / count;
    }

    static EditMesh Low() => MeshMapTests.Unwrapped(TransferFixtures.Grid(4, 2f, static _ => 0), 0f);

    /// <summary>A sheet two across with a cap of radius one rising out of its middle, or sinking for a negative side.</summary>
    static EditMesh Domed(float side = 1f) {
        var mesh = TransferFixtures.Grid(160, 2f, static _ => 0);
        const float Rim = 0.6f;
        var drop = MathF.Sqrt(1f - Rim * Rim);

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            var point = mesh.Positions[vertex];
            var reach = point.X * point.X + point.Y * point.Y;
            var height = reach < Rim * Rim ? MathF.Sqrt(1f - reach) - drop : 0f;

            mesh.MovePosition(vertex, new(point.X, point.Y, side * height));
        }

        return mesh;
    }

    /// <summary>A sheet with a flat-topped ridge a fifth high across its middle, its walls one cell of 1/80 wide.</summary>
    static EditMesh Ridge() {
        var mesh = TransferFixtures.Grid(160, 2f, static _ => 0);

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            var point = mesh.Positions[vertex];
            var height = MathF.Abs(point.Y) <= 0.4001f ? 0.2f : 0f;

            mesh.MovePosition(vertex, new(point.X, point.Y, height));
        }

        return mesh;
    }
}
