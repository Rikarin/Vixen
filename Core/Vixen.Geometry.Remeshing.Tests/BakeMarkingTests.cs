// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>
///     The edge, opacity and island maps, and the rounding profiles, each against an answer worked out first on a hard
///     box whose faces are six charts.
/// </summary>
public sealed class BakeMarkingTests {
    const int Size = 128;
    const float Width = 0.5f;

    static BakedMaps Box(MeshMaps maps, BakeRounding? edges = null, BakeRounding? rounding = null) =>
        MapBaker.Bake(
            HardEdgeBakeTests.HardBox(),
            HardEdgeBakeTests.HardBox(),
            new() {
                Resolution = Size,
                Gutter = 1,
                Space = BakeSpace.Object,
                Units = BakeUnits.Absolute,
                SearchRadius = 0.01f,
                Maps = maps,
                EdgeMap = edges,
                Rounding = rounding
            }
        );

    /// <summary>The texel of face 0 (the −X face) at a point given as the face's own two parameters.</summary>
    static int Texel(float x, float y) {
        var uv = HardEdgeBakeTests.Chart(0, 0, x, y) * Size;

        return (int)uv.Y * Size + (int)uv.X;
    }

    /// <summary>How far a texel's centre on face 0 is from the edge it shares with the −Y face, in model units.</summary>
    static float FromEdge(int texel) {
        var v = (texel / Size + 0.5f) / Size;

        return 2f * ((v * 2f - 0.05f) / 0.9f);
    }

    static float Smoothstep(float share) => share * share * (3f - 2f * share);

    /// <summary>
    ///     The edge map is one on an edge and falls with distance as the profile says, to nothing at the width, and the
    ///     middle of a face, out of every band's reach, is black.
    /// </summary>
    [Theory]
    [InlineData(RoundingProfile.Even)]
    [InlineData(RoundingProfile.Smooth)]
    [InlineData(RoundingProfile.Chamfer)]
    public void The_edge_map_is_a_band_along_the_hard_edges_shaped_by_the_profile(RoundingProfile profile) {
        var maps = Box(MeshMaps.Edges, new(Width, RoundedEdges.Hard) { Profile = profile });
        var edges = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.Edges);
        var checkedTexels = 0;

        foreach (var y in (float[])[0.02f, 0.06f, 0.1f, 0.15f, 0.2f]) {
            var texel = Texel(0.5f, y);
            var share = MathF.Max(0f, 1f - FromEdge(texel) / Width);
            var expected = profile switch {
                RoundingProfile.Smooth => Smoothstep(share),
                RoundingProfile.Chamfer => share > 0f ? 1f : 0f,
                _ => share
            };

            Assert.True(
                MathF.Abs(edges[texel] - expected) < 0.02f,
                $"At {FromEdge(texel):F3} from the edge the map read {edges[texel]}, and {expected} was worked out."
            );
            checkedTexels++;
        }

        Assert.Equal(0f, edges[Texel(0.5f, 0.5f)]);
        Assert.Equal(5, checkedTexels);
    }

    /// <summary>With no edge sharp enough the map is black and the bake says so; by default it marks the hard edges.</summary>
    [Fact]
    public void An_edge_map_with_no_edge_to_mark_is_black_and_says_so() {
        var none = Box(MeshMaps.Edges, new(Width, RoundedEdges.Angle, 120f));
        var standard = MapBaker.Bake(
            HardEdgeBakeTests.HardBox(),
            HardEdgeBakeTests.HardBox(),
            new() { Resolution = Size, Gutter = 1, Maps = MeshMaps.Edges }
        );

        Assert.All(none.Edges!, static value => Assert.Equal(0f, value));
        Assert.Contains(none.Warnings, static warning => warning.Contains("edge map", StringComparison.Ordinal));

        // The default is the hard edges a hundredth of the diagonal wide, 0.035 here: the texel nearest the edge,
        // about 0.01 from it, is inside the band, and one a fifth of the way across is not.
        Assert.True(standard.Edges![Texel(0.5f, 0f)] > 0.5f);
        Assert.Equal(0f, standard.Edges![Texel(0.5f, 0.1f)]);
    }

    /// <summary>
    ///     The edge map marks the target's hard edges when asked to, measured at the texel: a high mesh rounded all
    ///     over has no edge of its own to mark, and the low box under it still has twelve.
    /// </summary>
    [Fact]
    public void The_edge_map_can_mark_the_targets_edges_where_the_source_has_none() {
        static BakedMaps Of(BakeMesh mesh) =>
            MapBaker.Bake(
                HardEdgeBakeTests.RoundedBox(24),
                HardEdgeBakeTests.HardBox(),
                new() {
                    Resolution = Size,
                    Gutter = 1,
                    Units = BakeUnits.Absolute,
                    SearchRadius = 0.2f,
                    Maps = MeshMaps.Edges,
                    EdgeMap = new(Width, RoundedEdges.Hard),
                    EdgeMapOf = mesh
                }
            );

        var source = Of(BakeMesh.Source);
        var target = Of(BakeMesh.Target);

        Assert.All(source.Edges!, static value => Assert.Equal(0f, value));
        Assert.Contains(source.Warnings, static warning => warning.Contains("source", StringComparison.Ordinal));
        Assert.DoesNotContain(
            target.Warnings,
            static warning => warning.Contains("edge map", StringComparison.Ordinal)
        );

        foreach (var y in (float[])[0.02f, 0.1f, 0.2f]) {
            var texel = Texel(0.5f, y);
            var expected = MathF.Max(0f, 1f - FromEdge(texel) / Width);

            Assert.True(
                MathF.Abs(target.Edges![texel] - expected) < 0.02f,
                $"At {FromEdge(texel):F3} from the low box's edge the map read {target.Edges[texel]}, not {expected}."
            );
        }

        Assert.Equal(0f, target.Edges![Texel(0.5f, 0.5f)]);
    }

    /// <summary>A chamfer turns the whole way to halfway between the faces across its band, where an even turn ramps.</summary>
    [Fact]
    public void A_chamfered_edge_is_one_flat_face_at_the_halfway_angle() {
        var chamfer = Box(MeshMaps.None, rounding: new(Width) { Profile = RoundingProfile.Chamfer });
        var even = Box(MeshMaps.None, rounding: new(Width));
        Vector3 half = new(-MathF.Sqrt(0.5f), -MathF.Sqrt(0.5f), 0f);

        foreach (var y in (float[])[0.06f, 0.15f, 0.2f]) {
            var texel = Texel(0.5f, y);

            Assert.True(Vector3.Dot(chamfer.Normals[texel], half) > 0.9999f, $"Chamfer: {chamfer.Normals[texel]}.");
            Assert.True(Vector3.Dot(even.Normals[texel], half) < 0.999f, $"Even: {even.Normals[texel]}.");
        }

        Assert.True(Vector3.Dot(chamfer.Normals[Texel(0.5f, 0.5f)], -Vector3.UnitX) > 0.9999f);
    }

    /// <summary>A quad at a height, with the coordinates given at its corners.</summary>
    static EditMesh Quad(float x0, float x1, float z, params Vector2[] uvs) {
        var mesh = new EditMesh();
        Span<int> loop = [
            mesh.AddPosition(new(x0, 0f, z)),
            mesh.AddPosition(new(x1, 0f, z)),
            mesh.AddPosition(new(x1, 1f, z)),
            mesh.AddPosition(new(x0, 1f, z))
        ];

        mesh.AddFace(loop);

        if (uvs.Length > 0) {
            mesh.SetTexCoords(uvs);
        }

        return mesh;
    }

    /// <summary>
    ///     Opacity is white where a ray struck the source and black where it fell back to the nearest point: a source
    ///     covering the left half of the target is the left half of the map.
    /// </summary>
    [Fact]
    public void Opacity_is_where_a_ray_found_the_source() {
        var target = Quad(0f, 1f, 0f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        var maps = MapBaker.Bake(
            Quad(0f, 0.5f, 0.005f),
            target,
            new() {
                Resolution = 32,
                Gutter = 0,
                Units = BakeUnits.Absolute,
                SearchRadius = 0.01f,
                Maps = MeshMaps.Opacity
            }
        );
        var opacity = maps.Opacity!;

        foreach (var row in (int[])[4, 16, 28]) {
            Assert.Equal(1f, opacity[row * 32 + 4]);
            Assert.Equal(1f, opacity[row * 32 + 12]);
            Assert.Equal(0f, opacity[row * 32 + 20]);
            Assert.Equal(0f, opacity[row * 32 + 28]);
        }

        Assert.True(maps.Missed > 0);
    }

    /// <summary>
    ///     A mesh baked onto itself is struck everywhere: its rays start on the surface they should strike, which a ray
    ///     meets at zero distance, so a short ray a hair either side of the point finds it first. Without it every
    ///     texel of this box was missed.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void A_mesh_baked_onto_itself_is_struck_everywhere(int supersampling) {
        var maps = MapBaker.Bake(
            HardEdgeBakeTests.HardBox(),
            HardEdgeBakeTests.HardBox(),
            new() {
                Resolution = Size,
                Gutter = 0,
                Supersampling = supersampling,
                Units = BakeUnits.Absolute,
                SearchRadius = 0.01f,
                Maps = MeshMaps.Opacity
            }
        );

        Assert.True(maps.Covered > 0);
        Assert.Equal(0, maps.Missed);

        for (var index = 0; index < maps.Coverage.Count; index++) {
            if (maps.Coverage[index]) {
                Assert.Equal(1f, maps.Opacity![index]);
            }
        }

        Assert.True(maps.Displacement.All(static height => MathF.Abs(height) < 1e-3f));
    }

    /// <summary>
    ///     Each of the box's six charts is an island of its own, numbered in face order, and its texels read its
    ///     colour, nearest, through the gutter too.
    /// </summary>
    [Fact]
    public void Every_chart_of_the_box_is_an_island_numbered_in_face_order() {
        var maps = Box(MeshMaps.Islands);
        var islands = maps.Islands!;

        for (var face = 0; face < 6; face++) {
            var uv = HardEdgeBakeTests.Chart(face % 3, face / 3, 0.5f, 0.5f) * Size;

            Assert.Equal(face, islands[(int)uv.Y * Size + (int)uv.X]);
        }

        Assert.Equal(-1, islands[0]);

        var plane = Assert.Single(MeshMapPlanes.Of(maps), static plane => plane.Kind == MeshMapKind.Islands);

        Assert.Equal(3, plane.Channels);
    }

    /// <summary>
    ///     The borders map is each texel's distance in UV to its island's edge, over the width: along the middle row of
    ///     the box's first chart it climbs a texel's share of the atlas per texel from zero at the chart's first texel,
    ///     and down its middle column it climbs by a share of the atlas's height, which is twice as tall a step here.
    /// </summary>
    [Fact]
    public void The_borders_map_is_the_distance_in_uv_to_the_islands_edge() {
        const float width = 0.1f;
        const int rows = Size / 2;
        var maps = MapBaker.Bake(
            HardEdgeBakeTests.HardBox(),
            HardEdgeBakeTests.HardBox(),
            new() {
                Resolution = Size,
                Height = rows,
                Gutter = 2,
                Units = BakeUnits.Absolute,
                SearchRadius = 0.01f,
                Maps = MeshMaps.Borders,
                BorderWidth = width
            }
        );
        var borders = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.Borders);
        var middle = HardEdgeBakeTests.Chart(0, 0, 0.5f, 0.5f);
        var (column, row) = ((int)(middle.X * Size), (int)(middle.Y * rows));
        var left = Enumerable.Range(0, Size).First(x => maps.Coverage[row * Size + x]);
        var bottom = Enumerable.Range(0, rows).First(y => maps.Coverage[y * Size + column]);

        Assert.Null(maps.Islands);
        Assert.Equal(0f, borders[row * Size + left - 1]);

        for (var step = 0; step < 8; step++) {
            Assert.Equal(MathF.Min(1f, step / (float)Size / width), borders[row * Size + left + step], 1e-5f);
            Assert.Equal(MathF.Min(1f, step / (float)rows / width), borders[(bottom + step) * Size + column], 1e-5f);
        }

        Assert.Equal(1f, borders[row * Size + column]);
        Assert.Single(MeshMapPlanes.Of(maps), static plane => plane.Kind == MeshMapKind.Borders);
        Assert.Throws<ArgumentOutOfRangeException>(() => MapBaker.Bake(
                HardEdgeBakeTests.HardBox(),
                HardEdgeBakeTests.HardBox(),
                new() { Resolution = 16, Maps = MeshMaps.Borders, BorderWidth = 0f }
            )
        );
    }

    /// <summary>
    ///     The two passes give the Euclidean distance to the nearest border texel, diagonals included, as a search over
    ///     every texel does: islands of random blobs on a non-square atlas, against the brute force.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_border_distance_is_the_brute_force_euclidean_one(int seed) {
        const int columns = 40;
        const int rows = 24;
        var random = new Random(seed);
        var discs = Enumerable.Range(0, 3)
            .Select(_ => (X: random.Next(columns), Y: random.Next(rows), R: 4 + random.Next(8)))
            .ToArray();
        var islands = new int[columns * rows];

        for (var index = 0; index < islands.Length; index++) {
            var (x, y) = (index % columns, index / columns);
            // Three discs and the rest empty: borders against each other and against nothing.
            islands[index] = -1;

            for (var disc = 0; disc < discs.Length; disc++) {
                var (cx, cy, r) = discs[disc];

                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < r * r) {
                    islands[index] = disc;
                }
            }
        }

        var values = UvBorders.Of(islands, columns, rows, 1f, new());

        for (var index = 0; index < islands.Length; index++) {
            if (islands[index] < 0) {
                Assert.Equal(0f, values[index]);

                continue;
            }

            var nearest = double.PositiveInfinity;

            for (var other = 0; other < islands.Length; other++) {
                if (BorderTexel(islands, columns, rows, other)) {
                    var dx = (index % columns - other % columns) / (double)columns;
                    var dy = (index / columns - other / columns) / (double)rows;
                    nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dy * dy));
                }
            }

            Assert.Equal((float)Math.Min(1d, nearest), values[index], 1e-5f);
        }
    }

    static bool BorderTexel(int[] islands, int columns, int rows, int index) {
        var (x, y) = (index % columns, index / columns);
        var island = islands[index];

        if (island < 0 || x == 0 || y == 0 || x == columns - 1 || y == rows - 1) {
            return true;
        }

        return islands[index - 1] != island
            || islands[index + 1] != island
            || islands[index - columns] != island
            || islands[index + columns] != island;
    }

    /// <summary>Two faces sharing an edge are one island where their coordinates agree along it, and two at a seam.</summary>
    [Fact]
    public void A_shared_edge_joins_two_faces_only_where_their_coordinates_agree() {
        static EditMesh Pair(float gap) {
            var mesh = new EditMesh();
            Span<int> points = [
                mesh.AddPosition(new(0f, 0f, 0f)),
                mesh.AddPosition(new(1f, 0f, 0f)),
                mesh.AddPosition(new(1f, 1f, 0f)),
                mesh.AddPosition(new(0f, 1f, 0f)),
                mesh.AddPosition(new(2f, 0f, 0f)),
                mesh.AddPosition(new(2f, 1f, 0f))
            ];

            mesh.AddFace([points[0], points[1], points[2], points[3]]);
            mesh.AddFace([points[1], points[4], points[5], points[2]]);
            mesh.SetTexCoords(
                [
                    new(0f, 0f), new(0.4f, 0f), new(0.4f, 0.4f), new(0f, 0.4f),
                    new(0.4f + gap, 0f), new(0.8f, 0f), new(0.8f, 0.4f), new(0.4f + gap, 0.4f)
                ]
            );

            return mesh;
        }

        Assert.Equal([0, 0], UvIslands.Of(Pair(0f)));
        Assert.Equal([0, 1], UvIslands.Of(Pair(0.1f)));
    }
}
