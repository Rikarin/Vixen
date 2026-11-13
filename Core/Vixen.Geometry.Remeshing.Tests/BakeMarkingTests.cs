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
