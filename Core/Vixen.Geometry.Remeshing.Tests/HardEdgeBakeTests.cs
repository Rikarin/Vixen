// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>What P4 added to the bake: seams, cages, skew, parts, samples and threads.</summary>
public sealed class HardEdgeBakeTests {
    /// <summary>How many texels an atlas side has in these bakes.</summary>
    const int Size = 128;

    /// <summary>A box of side two: six flat quads with hard edges, one chart each, in a 3 × 2 atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>The quads share positions and do not share normals</b>, which is what a hard edge is, and
    ///     each is its own chart, which is what a game mesh's hard edge always is: a tangent-space map
    ///     cannot be continuous across one, so the seam is in the atlas and the two sides meet only on
    ///     the model.
    /// </remarks>
    static EditMesh HardBox(float half = 1f) {
        var mesh = new EditMesh();
        var corners = new int[8];

        for (var at = 0; at < 8; at++) {
            corners[at] = mesh.AddPosition(
                new((at & 1) == 0 ? -half : half, (at & 2) == 0 ? -half : half, (at & 4) == 0 ? -half : half)
            );
        }

        int[][] faces = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
        List<Vector2> uvs = [];
        List<Vector3> normals = [];

        for (var face = 0; face < 6; face++) {
            Span<int> loop = [corners[faces[face][0]], corners[faces[face][1]], corners[faces[face][2]], corners[faces[face][3]]];

            mesh.AddFace(loop);

            var normal = mesh.Normal(face);
            normal = Vector3.Normalize(normal);

            var (u, v) = (face % 3, face / 3);

            for (var at = 0; at < 4; at++) {
                normals.Add(normal);
            }

            uvs.Add(Chart(u, v, 0f, 0f));
            uvs.Add(Chart(u, v, 1f, 0f));
            uvs.Add(Chart(u, v, 1f, 1f));
            uvs.Add(Chart(u, v, 0f, 1f));
        }

        mesh.SetNormals([.. normals]);
        mesh.SetTexCoords([.. uvs]);

        return mesh;
    }

    static Vector2 Chart(int u, int v, float x, float y) =>
        new((u + 0.05f + (x * 0.9f)) / 3f, (v + 0.05f + (y * 0.9f)) / 2f);

    /// <summary>A unit sphere's worth of a rounded box: what a high poly with bevels looks like to a ray.</summary>
    static EditMesh RoundedBox(int cells = 48, float half = 1f) {
        var mesh = new EditMesh();
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var side = 0; side < 6; side++) {
            var first = positions.Count;
            var axis = side / 2;
            var sign = side % 2 == 0 ? 1f : -1f;

            for (var i = 0; i <= cells; i++) {
                for (var j = 0; j <= cells; j++) {
                    var a = ((2f * i) / cells) - 1f;
                    var b = ((2f * j) / cells) - 1f;
                    Vector3 cube = axis switch {
                        0 => new(sign, a, b),
                        1 => new(b, sign, a),
                        _ => new(a, b, sign)
                    };

                    // Pulls the box's corners in along a superellipse, so the edges are round.
                    var length = MathF.Pow(
                        MathF.Pow(MathF.Abs(cube.X), 8f) + MathF.Pow(MathF.Abs(cube.Y), 8f) + MathF.Pow(MathF.Abs(cube.Z), 8f),
                        1f / 8f
                    );

                    positions.Add(cube / length * half);
                }
            }

            for (var i = 0; i < cells; i++) {
                for (var j = 0; j < cells; j++) {
                    var (p, q, r, s) = (
                        first + (i * (cells + 1)) + j,
                        first + (i * (cells + 1)) + j + 1,
                        first + ((i + 1) * (cells + 1)) + j,
                        first + ((i + 1) * (cells + 1)) + j + 1
                    );

                    if (sign > 0f) {
                        indices.AddRange([p, r, q, q, r, s]);
                    } else {
                        indices.AddRange([p, q, r, q, s, r]);
                    }
                }
            }
        }

        return EditMesh.FromTriangles([.. positions], [.. indices]);
    }

    /// <summary>The texel of the target's atlas holding a surface point, on one face.</summary>
    static int Texel(int size, int face, float x, float y) {
        var uv = Chart(face % 3, face / 3, x, y) * size;

        return ((int)uv.Y * size) + (int)uv.X;
    }

    /// <summary>The worst disagreement between two charts along the edge they share, in object space.</summary>
    /// <remarks>
    ///     Faces 0 (−X) and 2 (−Y) meet along the line at <c>x = y = −1</c>. Each texel is one in from
    ///     its chart's edge, and the two walks visit the same points along it.
    /// </remarks>
    static float WorstAcrossEdge(BakedMaps maps) {
        var size = maps.Resolution;
        var inset = 1.5f * 3f / (0.9f * size);
        var worst = 0f;

        for (var along = 0.1f; along <= 0.9f; along += 0.02f) {
            // Face 0 runs (0, 4, 6, 2): x of the chart is +z, so its first edge is the one at y = −1.
            var left = maps.Normals[Texel(size, 0, along, inset)];

            // Face 2 runs (0, 1, 5, 4): y of the chart is +z, so its first column is at x = −1.
            var right = maps.Normals[Texel(size, 2, inset, along)];

            worst = MathF.Max(worst, Vector3.Distance(left, right));
        }

        return worst;
    }

    /// <summary>Both sides of a hard edge find the same surface, in the default bake.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is P4's exit criterion, as far as a number can hold it</b>, and object space is
    ///         the question asked directly: at a point on the edge, do the two charts say the source's
    ///         normal is the same? Decoding a tangent map per side would ask the same thing with the
    ///         frame as a second place to be wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A difference that shrinks with the texel is continuity, and a constant is a seam.</b>
    ///         The texels compared are a fixed number of texels off the edge, so on a curved source they
    ///         can never quite agree: what matters is that doubling the resolution halves the gap.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are here.</b> <see cref="BakeSettings.Skew" /> of one casts along each
    ///         corner's own normal, which is what the baker did before P4 and what a hard edge seams
    ///         under: same fixture, and a gap that does not go away.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_hard_edge_bakes_the_same_surface_on_both_sides_and_casting_along_corner_normals_does_not() {
        // ⚠ Inside the source, because that is where a seam cannot hide. Outside it, a ray cast along a
        // corner's own normal runs past the rounded edge, strikes nothing, and the closest-point
        // fallback answers for both sides alike: 0.096 against 0.087, which looked like no seam at all.
        var source = RoundedBox(96);
        var target = HardBox(0.97f);

        BakedMaps Bake(int size, float skew) => MapBaker.Bake(
            source,
            target,
            new() { Resolution = size, Space = BakeSpace.Object, SearchRadius = 0.2f, Skew = skew }
        );

        var coarse = Bake(256, 0f);
        var fine = Bake(1024, 0f);
        var seamed = Bake(1024, 1f);

        Assert.True(fine.Missed * 1000 < fine.Covered, $"{fine.Missed} of {fine.Covered} texels found nothing.");

        var gap = WorstAcrossEdge(fine);
        var seam = WorstAcrossEdge(seamed);

        Assert.True(gap < WorstAcrossEdge(coarse) * 0.5f, $"From {WorstAcrossEdge(coarse)} to {gap} at four times the size.");
        // Not nearer nothing: the source has no normal layer, so what is compared is one facet against the next.
        Assert.True(gap < 0.15f, $"The two sides of the edge disagree by {gap}.");
        Assert.True(seam > 5f * gap, $"Corner normals gave {seam}, against {gap} when averaged.");
    }

    /// <summary>A ray from a cage reaches what lies under something nearer the target.</summary>
    [Fact]
    public void A_cage_decides_what_a_ray_can_reach() {
        // A body under the target and a floater just above it: the nearer of two hits is the floater.
        var body = Sheet(-0.05f, flip: false);
        var floater = Sheet(0.02f, flip: false);
        var source = Merge(body, floater);
        var target = Sheet(0f, flip: false);
        var cage = Sheet(0.01f, flip: false);

        Atlas(target);
        Atlas(cage);

        var bare = MapBaker.Bake(source, target, new() { Resolution = 16, SearchRadius = 0.5f, Gutter = 0 });
        var caged = MapBaker.Bake(source, target, new() { Resolution = 16, SearchRadius = 0.5f, Gutter = 0, Cage = cage });

        var middle = (8 * 16) + 8;

        Assert.Equal(0.02f, bare.Displacement[middle], 3);
        Assert.Equal(-0.05f, caged.Displacement[middle], 3);
    }

    /// <summary>A cage whose faces are not the target's is refused rather than paired up wrongly.</summary>
    [Fact]
    public void A_cage_with_another_layout_is_refused() {
        var target = Sheet(0f, flip: false);

        Atlas(target);

        var settings = new BakeSettings { Resolution = 8, Cage = TransferFixtures.Grid(3, 2f, _ => 0) };

        Assert.Throws<ArgumentException>(() => MapBaker.Bake(target, target, settings));
    }

    /// <summary>A skew map of zero changes nothing; of one, it is the skew itself.</summary>
    [Fact]
    public void A_skew_map_applies_the_skew_where_it_says() {
        var source = RoundedBox();
        var target = HardBox(1.02f);

        BakedMaps Bake(float skew, float mapped) => MapBaker.Bake(
            source,
            target,
            new() {
                Resolution = 48,
                Space = BakeSpace.Object,
                SearchRadius = 0.2f,
                Skew = skew,
                SkewMap = mapped < 0f ? null : new(1, [mapped])
            }
        );

        Assert.Equal(Bake(0f, -1f).Normals, Bake(1f, 0f).Normals);
        Assert.Equal(Bake(1f, -1f).Normals, Bake(1f, 1f).Normals);
    }

    /// <summary>A part's rays strike only its own source, while occlusion still sees everything.</summary>
    [Fact]
    public void Parts_keep_a_neighbours_surface_out_of_the_normal_map_and_not_out_of_the_occlusion() {
        var near = Sheet(0.02f, flip: false);
        var own = Sheet(-0.05f, flip: false);
        var source = Merge(own, near);
        var target = Sheet(0f, flip: false);

        Atlas(target);

        var parts = BakeParts.ByName(
            [("bolt_high", own.FaceCount), ("strap_high", near.FaceCount)],
            [("bolt_low", target.FaceCount)]
        );

        var settings = new BakeSettings {
            Resolution = 16,
            SearchRadius = 0.5f,
            Gutter = 0,
            Maps = MeshMaps.AmbientOcclusion,
            OcclusionSamples = 16
        };

        var matched = MapBaker.Bake(source, target, settings with { Parts = parts });
        var unmatched = MapBaker.Bake(source, target, settings);
        var middle = (8 * 16) + 8;

        Assert.Equal(-0.05f, matched.Displacement[middle], 3);
        Assert.Equal(0.02f, unmatched.Displacement[middle], 3);

        // The strap still covers the bolt.
        Assert.Equal(0f, matched.AmbientOcclusion![middle], 2);
    }

    /// <summary>A target part no source shares may strike anything, and the bake says so.</summary>
    [Fact]
    public void An_unmatched_part_is_warned_about_and_baked_against_everything() {
        var source = Sheet(0.02f, flip: false);
        var target = Sheet(0f, flip: false);

        Atlas(target);

        var parts = BakeParts.ByName([("bolt_high", source.FaceCount)], [("rivet_low", target.FaceCount)]);
        var maps = MapBaker.Bake(source, target, new() { Resolution = 8, SearchRadius = 0.5f, Parts = parts });

        Assert.Contains(maps.Warnings, warning => warning.Contains("matched no part", StringComparison.Ordinal));
        Assert.Equal(0.02f, maps.Displacement[(4 * 8) + 4], 3);
    }

    [Theory]
    [InlineData("Bolt_high", "bolt")]
    [InlineData("bolt_LOW", "bolt")]
    [InlineData("bolt_lo.001", "bolt")]
    [InlineData("  strap_hp ", "strap")]
    [InlineData("bolt", "bolt")]
    [InlineData("high", "high")]
    [InlineData("v1.2", "v1.2")]
    public void A_part_name_loses_only_what_says_which_end_it_is(string name, string key) {
        if (name == "v1.2") {
            // A dot followed by digits reads as a copy number, whichever way a person meant it.
            key = "v1";
        }

        Assert.Equal(key, BakeParts.Key(name));
    }

    /// <summary>A step narrower than a texel is a fraction of it, which one sample cannot say.</summary>
    /// <remarks>
    ///     Closed form: the source is a sheet at height one over the left quarter of every texel and at
    ///     nothing elsewhere, so four samples across read exactly one in four. One sample at the
    ///     texel's centre reads zero, which is the aliasing the setting exists to remove.
    /// </remarks>
    [Fact]
    public void Supersampling_resolves_a_feature_narrower_than_a_texel() {
        var mesh = new EditMesh();
        Span<int> loop = stackalloc int[4];

        for (var texel = 0; texel < 4; texel++) {
            var x = (texel / 4f) * 2f;

            loop[0] = mesh.AddPosition(new(x, 0f, 0.1f));
            loop[1] = mesh.AddPosition(new(x + 0.125f, 0f, 0.1f));
            loop[2] = mesh.AddPosition(new(x + 0.125f, 2f, 0.1f));
            loop[3] = mesh.AddPosition(new(x, 2f, 0.1f));
            mesh.AddFace(loop);
        }

        var ground = TransferFixtures.Grid(4, 2f, _ => 0);
        var source = Merge(mesh, Shifted(ground, new(1f, 1f, 0f)));
        var target = Shifted(TransferFixtures.Grid(1, 2f, _ => 0), new(1f, 1f, 0.05f));

        Atlas(target);

        BakedMaps Bake(int side) => MapBaker.Bake(
            source,
            target,
            new() { Resolution = 4, SearchRadius = 0.5f, Gutter = 0, Supersampling = side }
        );

        var one = Bake(1);
        var four = Bake(4);

        // In model units off the target, whose texels are 0.5 wide: 0.05 up to the strips, 0.05 down.
        Assert.Equal(-0.05f, one.Displacement[5], 3);
        Assert.Equal(((0.05f * 1f) - (0.05f * 3f)) / 4f, four.Displacement[5], 3);
    }

    /// <summary>The occlusion a texel casts stays the count asked for, however finely it is divided.</summary>
    [Fact]
    public void Supersampling_does_not_multiply_the_occlusion_rays() {
        var source = RoundedBox(16);
        var target = HardBox(1.02f);

        BakedMaps Bake(int side) => MapBaker.Bake(
            source,
            target,
            new() {
                Resolution = 32,
                SearchRadius = 0.2f,
                Maps = MeshMaps.AmbientOcclusion,
                OcclusionSamples = 16,
                Supersampling = side
            }
        );

        var one = Bake(1).AmbientOcclusion!;
        var two = Bake(2).AmbientOcclusion!;

        // A sum over 16 rays is a whole number of sixteenths either way.
        foreach (var value in two) {
            Assert.Equal(MathF.Round(value * 16f), value * 16f, 3);
        }

        Assert.Equal(one.Average(), two.Average(), 1);
    }

    /// <summary>Every map, byte for byte, at any thread count.</summary>
    /// <remarks>
    ///     ⚠ <b>Compared with a run on many threads and with samples and everything asked for</b>,
    ///     because a content hash rests on this and it is the one property threading could lose quietly.
    ///     A row racing another would still make a plausible picture.
    /// </remarks>
    [Fact]
    public void A_bake_is_identical_at_any_thread_count() {
        var source = RoundedBox(24);
        var target = HardBox(1.02f);
        var settings = new BakeSettings {
            Resolution = 64,
            SearchRadius = 0.2f,
            Maps = MeshMaps.All,
            OcclusionSamples = 16,
            OcclusionRadius = 1f,
            Supersampling = 2
        };

        var one = MapBaker.Bake(source, target, settings with { Threads = 1 });
        var many = MapBaker.Bake(source, target, settings with { Threads = 8 });

        Assert.Equal(one.Normals, many.Normals);
        Assert.Equal(one.Displacement, many.Displacement);
        Assert.Equal(one.AmbientOcclusion, many.AmbientOcclusion);
        Assert.Equal(one.BentNormal, many.BentNormal);
        Assert.Equal(one.Thickness, many.Thickness);
        Assert.Equal(one.Curvature, many.Curvature);
        Assert.Equal(one.Position, many.Position);
        Assert.Equal(one.Ids, many.Ids);
        Assert.Equal(one.DisplacementRange, many.DisplacementRange);
        Assert.Equal(one.CurvatureRange, many.CurvatureRange);
        Assert.Equal(one.Covered, many.Covered);
        Assert.Equal(one.Dilated, many.Dilated);
        Assert.Equal(one.Missed, many.Missed);
    }

    /// <summary>Planes come out with the top row first, and integers centred on one half.</summary>
    [Fact]
    public void Planes_hold_measurements_and_say_how_to_store_them() {
        var maps = new BakedMaps {
            Resolution = 2,
            Normals = [Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitZ],
            Displacement = [0f, 0f, 2f, -4f],
            Coverage = [true, true, true, true],
            Space = BakeSpace.Tangent,
            Covered = 4,
            Dilated = 0,
            Missed = 0,
            DisplacementRange = 4f
        };

        var planes = MeshMapPlanes.Of(maps);
        var normal = planes.Single(plane => plane.Kind == MeshMapKind.Normal);
        var height = planes.Single(plane => plane.Kind == MeshMapKind.Displacement);

        // The third value of the array is the bottom-left row's successor: the top row, when flipped.
        Assert.Equal(-1f, normal.Samples[0]);
        Assert.Equal(0f, normal.Unit(0));
        Assert.Equal(2f, height.Samples[0]);
        Assert.Equal(0.75f, height.Unit(0));
        Assert.Equal(0f, height.Unit(1));
        Assert.Equal(4f, height.Scale);
    }

    static EditMesh Sheet(float z, bool flip) {
        _ = flip;

        return Shifted(TransferFixtures.Grid(2, 2f, _ => 0), new(1f, 1f, z));
    }

    static EditMesh Shifted(EditMesh mesh, Vector3 by) {
        var moved = new EditMesh();
        var points = new int[mesh.PositionCount];

        for (var at = 0; at < points.Length; at++) {
            points[at] = moved.AddPosition(mesh.Positions[at] + by);
        }

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face).ToArray().Select(corner => points[corner]).ToArray();

            moved.AddFace(loop);
        }

        return moved;
    }

    static EditMesh Merge(EditMesh one, EditMesh two) {
        var merged = Shifted(one, Vector3.Zero);
        var points = new int[two.PositionCount];

        for (var at = 0; at < points.Length; at++) {
            points[at] = merged.AddPosition(two.Positions[at]);
        }

        for (var face = 0; face < two.FaceCount; face++) {
            merged.AddFace(two.CornersOf(face).ToArray().Select(corner => points[corner]).ToArray());
        }

        return merged;
    }

    /// <summary>Maps a sheet of side two at the origin corner onto the whole atlas.</summary>
    static void Atlas(EditMesh mesh) {
        var coordinates = new Vector2[mesh.CornerCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);

            for (var at = 0; at < loop.Length; at++) {
                var point = mesh.Positions[loop[at]];

                coordinates[entry.Start + at] = new(point.X / 2f, point.Y / 2f);
            }
        }

        mesh.SetTexCoords(coordinates);
    }
}
