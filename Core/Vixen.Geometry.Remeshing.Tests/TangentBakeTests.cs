// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>A tangent-space bake, read back the way a renderer reads it.</summary>
public sealed class TangentBakeTests {
    const int Size = 96;
    const int Sides = 6;

    /// <summary>
    ///     A smooth band of a sphere with few cells. Its normals turn two ways across a triangle, which is what
    ///     leaves an interpolated tangent and normal out of square: on a tube they stay perpendicular, and any
    ///     frame reads back.
    /// </summary>
    static EditMesh Band(int sides, int rows, bool mirrored = false) {
        var mesh = new EditMesh();
        List<Vector3> normals = [];
        List<Vector2> uvs = [];

        Vector3 On(int side, int row) {
            var around = MathF.Tau * side / sides;
            var up = 1.1f * (((float)row / rows) - 0.5f);

            return new(MathF.Cos(around) * MathF.Cos(up), MathF.Sin(up), MathF.Sin(around) * MathF.Cos(up));
        }

        Vector2 Uv(int side, int row) {
            var along = (float)side / sides;

            return new(0.05f + (0.9f * (mirrored ? 1f - along : along)), 0.1f + (0.8f * row / rows));
        }

        void Triangle((int Side, int Row) a, (int Side, int Row) b, (int Side, int Row) c) {
            Span<int> loop = [
                mesh.AddPosition(On(a.Side, a.Row)),
                mesh.AddPosition(On(b.Side, b.Row)),
                mesh.AddPosition(On(c.Side, c.Row))
            ];

            mesh.AddFace(loop);
            normals.AddRange([On(a.Side, a.Row), On(b.Side, b.Row), On(c.Side, c.Row)]);
            uvs.AddRange([Uv(a.Side, a.Row), Uv(b.Side, b.Row), Uv(c.Side, c.Row)]);
        }

        for (var side = 0; side < sides; side++) {
            for (var row = 0; row < rows; row++) {
                Triangle((side, row), (side, row + 1), (side + 1, row));
                Triangle((side + 1, row), (side, row + 1), (side + 1, row + 1));
            }
        }

        mesh.SetNormals([.. normals]);
        mesh.SetTexCoords([.. uvs]);

        return mesh;
    }

    /// <summary>The largest angle, as a sine, between the normal a renderer rebuilds and the bake's world normal.</summary>
    static float WorstError(EditMesh low, EditMesh high) {
        var maps = MapBaker.Bake(
            high,
            low,
            new() { Resolution = Size, Gutter = 0, SearchRadius = 0.2f, Maps = MeshMaps.WorldNormal }
        );
        var corners = low.FaceCount * 3;
        var points = new Vector3[corners];
        var normals = new Vector3[corners];
        var uvs = new Vector2[corners];

        for (var face = 0; face < low.FaceCount; face++) {
            var loop = low.CornersOf(face);

            for (var at = 0; at < 3; at++) {
                points[(face * 3) + at] = low.Positions[loop[at]];
                normals[(face * 3) + at] = low.Normals[(face * 3) + at];
                uvs[(face * 3) + at] = low.TexCoords[(face * 3) + at];
            }
        }

        var tangents = MikkTSpace.Generate(points, normals, uvs);
        var worst = 0f;
        var checkedTexels = 0;

        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var place = new Vector2(x + 0.5f, y + 0.5f) / Size;

                for (var triangle = 0; triangle < low.FaceCount; triangle++) {
                    var at = triangle * 3;

                    if (Weights(place, uvs[at], uvs[at + 1], uvs[at + 2]) is not { } weights) {
                        continue;
                    }

                    var n = (normals[at] * weights.X) + (normals[at + 1] * weights.Y) + (normals[at + 2] * weights.Z);
                    var blended = (tangents[at] * weights.X) + (tangents[at + 1] * weights.Y) + (tangents[at + 2] * weights.Z);
                    var t = new Vector3(blended.X, blended.Y, blended.Z);
                    var b = Vector3.Cross(n, t) * tangents[at].W;
                    var stored = maps.Normals[(y * Size) + x];
                    var shaded = Vector3.Normalize((t * stored.X) + (b * stored.Y) + (n * stored.Z));
                    var truth = maps.WorldNormal![(y * Size) + x];

                    worst = MathF.Max(worst, Vector3.Cross(shaded, truth).Length());
                    checkedTexels++;

                    break;
                }
            }
        }

        Assert.True(checkedTexels > Size * Size / 20, $"only {checkedTexels} texels were looked at");

        return worst;
    }

    static Vector3? Weights(Vector2 place, Vector2 a, Vector2 b, Vector2 c) {
        var area = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
        var one = (((b.X - place.X) * (c.Y - place.Y)) - ((b.Y - place.Y) * (c.X - place.X))) / area;
        var two = (((c.X - place.X) * (a.Y - place.Y)) - ((c.Y - place.Y) * (a.X - place.X))) / area;
        var three = 1f - one - two;

        // Well inside: a texel touching two triangles belongs to the lower one, whichever holds its centre.
        return one > 0.12f && two > 0.12f && three > 0.12f ? new(one, two, three) : null;
    }

    [Fact]
    public void What_a_renderer_rebuilds_from_a_tangent_bake_is_the_surface_that_was_baked() =>
        Assert.InRange(WorstError(Band(Sides, 3), Band(256, 128)), 0f, 2e-5f);

    [Fact]
    public void A_mirrored_atlas_reads_back_as_well_as_a_plain_one() =>
        Assert.InRange(WorstError(Band(Sides, 3, true), Band(256, 128)), 0f, 2e-5f);
}
