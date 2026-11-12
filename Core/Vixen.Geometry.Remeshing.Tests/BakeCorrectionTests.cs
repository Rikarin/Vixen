// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>Rounded edges, carried colours and the distance map, each against an answer worked out first.</summary>
public sealed class BakeCorrectionTests {
    const int Size = 128;

    /// <summary>A hard box baked onto itself, with its edges rounded over a quarter of a unit, in object space.</summary>
    static BakedMaps RoundedBox(BakeRounding? rounding, bool smooth = false) {
        var box = HardEdgeBakeTests.HardBox();

        if (smooth) {
            // The same box with one normal per position, the average of its faces': nothing in it is hard.
            var normals = new Vector3[box.CornerCount];

            for (var corner = 0; corner < normals.Length; corner++) {
                normals[corner] = Vector3.Normalize(box.Positions[box.Corners[corner]]);
            }

            box.SetNormals(normals);
        }

        return MapBaker.Bake(
            box,
            HardEdgeBakeTests.HardBox(),
            new() {
                Resolution = Size,
                Gutter = 1,
                Space = BakeSpace.Object,
                Units = BakeUnits.Absolute,
                SearchRadius = 0.01f,
                Rounding = rounding
            }
        );
    }

    /// <summary>The texel of face 0 (the −X face) at a point given as the face's own two parameters.</summary>
    static int Texel(float x, float y) {
        var uv = HardEdgeBakeTests.Chart(0, 0, x, y) * Size;

        return (int)uv.Y * Size + (int)uv.X;
    }

    /// <summary>How far a texel's centre on face 0 is from the edge face 0 shares with the −Y face.</summary>
    /// <remarks>Face 0's second parameter runs from <c>y = −1</c> to <c>y = 1</c> over two units, so the distance is twice it.</remarks>
    static float FromEdge(int texel) {
        var v = (texel / Size + 0.5f) / Size;

        return 2f * ((v * 2f - 0.05f) / 0.9f);
    }

    /// <summary>
    ///     Within the width of a rounded edge the normal turns evenly towards the direction halfway between its faces,
    ///     arriving there on the edge, and beyond the width it is the face's own.
    /// </summary>
    [Fact]
    public void A_rounded_edge_turns_the_normal_evenly_towards_halfway_between_its_faces() {
        const float width = 0.5f;
        var maps = RoundedBox(new(width));
        var checkedTexels = 0;

        foreach (var y in (float[])[0.02f, 0.06f, 0.1f, 0.15f, 0.2f]) {
            var texel = Texel(0.5f, y);
            var distance = FromEdge(texel);
            var turn = MathF.PI / 4f * MathF.Max(0f, 1f - distance / width);
            Vector3 expected = new(-MathF.Cos(turn), -MathF.Sin(turn), 0f);
            var baked = maps.Normals[texel];

            Assert.True(
                Vector3.Dot(baked, expected) > MathF.Cos(0.5f * (MathF.PI / 180f)),
                $"At {distance:F3} from the edge the normal was {baked}, and {expected} was worked out."
            );
            checkedTexels++;
        }

        // The middle of the face is beyond every bevel's reach, so it keeps the face's own normal.
        Assert.True(Vector3.Dot(maps.Normals[Texel(0.5f, 0.5f)], -Vector3.UnitX) > 0.9999f);
        Assert.Equal(5, checkedTexels);
    }

    /// <summary>With no rounding the same texels are the flat face, so it is the rounding that turned them.</summary>
    [Fact]
    public void Without_rounding_the_box_is_flat_up_to_its_edges() {
        var maps = RoundedBox(null);

        foreach (var y in (float[])[0.02f, 0.06f, 0.1f]) {
            Assert.True(Vector3.Dot(maps.Normals[Texel(0.5f, y)], -Vector3.UnitX) > 0.9999f);
        }
    }

    /// <summary>Hard edges are the ones the normals split: a box with one normal per position has none to round.</summary>
    [Fact]
    public void Rounding_by_hard_edge_reads_the_split_normals_and_by_angle_reads_the_faces() {
        var hard = RoundedBox(new(0.5f, RoundedEdges.Hard));
        var smooth = RoundedBox(new(0.5f, RoundedEdges.Hard), true);
        var steep = RoundedBox(new(0.5f, RoundedEdges.Angle, 100f));
        var texel = Texel(0.5f, 0.06f);

        Assert.True(Vector3.Dot(hard.Normals[texel], -Vector3.UnitX) < 0.97f, $"Hard: {hard.Normals[texel]}.");
        Assert.Contains(smooth.Warnings, static warning => warning.Contains("round", StringComparison.Ordinal));
        Assert.Contains(steep.Warnings, static warning => warning.Contains("round", StringComparison.Ordinal));
        Assert.True(Vector3.Dot(steep.Normals[texel], -Vector3.UnitX) > 0.9999f);
    }

    /// <summary>A unit quad on the XY plane, with the coordinates given at its four corners.</summary>
    static EditMesh Quad(float z, params Vector2[] uvs) {
        var mesh = new EditMesh();
        Span<int> loop = [
            mesh.AddPosition(new(0f, 0f, z)),
            mesh.AddPosition(new(1f, 0f, z)),
            mesh.AddPosition(new(1f, 1f, z)),
            mesh.AddPosition(new(0f, 1f, z))
        ];

        mesh.AddFace(loop);
        mesh.SetTexCoords(uvs);

        return mesh;
    }

    /// <summary>A picture whose pixel is its own coordinate: red across, green down.</summary>
    static BakeImage Ramp(int side) {
        var pixels = new Vector4[side * side];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                pixels[y * side + x] = new((x + 0.5f) / side, (y + 0.5f) / side, 0f, 1f);
            }
        }

        return new(side, side, pixels);
    }

    /// <summary>
    ///     A picture made for the source's layout lands on the target's: here the target's is the source's mirrored
    ///     left to right, so a texel at <c>u</c> reads what the source had at <c>1 − u</c>.
    /// </summary>
    [Fact]
    public void A_picture_moves_from_the_sources_layout_to_the_targets() {
        var source = Quad(0f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        var target = Quad(0f, new(0.9f, 0.1f), new(0.1f, 0.1f), new(0.1f, 0.9f), new(0.9f, 0.9f));
        var maps = MapBaker.Bake(
            source,
            target,
            new() {
                Resolution = 64,
                Gutter = 0,
                Maps = MeshMaps.Colour,
                SearchRadius = 0.01f,
                Colours = new(null, [Ramp(256)])
            }
        );
        var covered = 0;

        for (var index = 0; index < maps.Coverage.Count; index++) {
            if (!maps.Coverage[index]) {
                continue;
            }

            // The target's own coordinate, and the surface point it stands for, which the source holds at x, y.
            var (u, v) = ((index % 64 + 0.5f) / 64f, (index / 64 + 0.5f) / 64f);
            var x = (0.9f - u) / 0.8f;
            var y = (v - 0.1f) / 0.8f;
            var colour = maps.Colour![index];

            Assert.True(
                MathF.Abs(colour.X - x) < 0.01f && MathF.Abs(colour.Y - y) < 0.01f,
                $"Texel ({u:F3}, {v:F3}) carried {colour}, and ({x:F3}, {y:F3}) was the source's there."
            );
            covered++;
        }

        Assert.True(covered > 1000, $"Only {covered} texels were covered.");
    }

    /// <summary>Corner colours blend across the face and multiply the picture; with no colours the map is white.</summary>
    [Fact]
    public void Corner_colours_blend_and_no_colours_bake_white_with_a_warning() {
        var source = Quad(0f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        var target = Quad(0f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        BakeSettings settings = new() { Resolution = 32, Gutter = 0, Maps = MeshMaps.Colour, SearchRadius = 0.01f };
        Vector4[] corners = [new(1f, 0f, 0f, 1f), new(0f, 0f, 1f, 1f), new(0f, 0f, 1f, 1f), new(1f, 0f, 0f, 1f)];

        var blended = MapBaker.Bake(source, target, settings with { Colours = new(corners, []) });
        var white = MapBaker.Bake(source, target, settings);

        // Red at x = 0 and blue at x = 1, evenly between: a texel's red is one less its own coordinate.
        var middle = 16 * 32 + 8;
        var u = (8 + 0.5f) / 32f;

        Assert.InRange(blended.Colour![middle].X, 1f - u - 0.01f, 1f - u + 0.01f);
        Assert.InRange(blended.Colour[middle].Z, u - 0.01f, u + 0.01f);
        Assert.Equal(Vector4.One, white.Colour![middle]);
        Assert.Contains(white.Warnings, static warning => warning.Contains("colour", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The distance map scales the rays: a source just past the set distance is missed at one half and found at
    ///     one, which sends them twice as far, and nothing pulls them in.
    /// </summary>
    [Fact]
    public void The_distance_map_sends_the_rays_further_or_nearer() {
        var source = Quad(0.08f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        var target = Quad(0f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        BakeSettings settings = new() {
            Resolution = 16,
            Gutter = 0,
            Units = BakeUnits.Absolute,
            SearchRadius = 0.05f,
            Space = BakeSpace.Object
        };
        static BakeMask Even(float value) => new(4, [..Enumerable.Repeat(value, 16)]);

        var asSet = MapBaker.Bake(source, target, settings with { DistanceMap = Even(0.5f) });
        var further = MapBaker.Bake(source, target, settings with { DistanceMap = Even(1f) });
        var none = MapBaker.Bake(source, target, settings with { DistanceMap = Even(0f) });
        var plain = MapBaker.Bake(source, target, settings);

        Assert.Equal(asSet.Covered, asSet.Missed);
        Assert.Equal(plain.Missed, asSet.Missed);
        Assert.Equal(0, further.Missed);
        Assert.Equal(none.Covered, none.Missed);
        Assert.InRange(further.Displacement[8 * 16 + 8], 0.079f, 0.081f);
    }

    /// <summary>
    ///     The reach a preview draws is the bake's: a flat quad's rays run along its normal, the front distance out and
    ///     the rear distance in, and a distance map stretches both; a cage is where the rays start.
    /// </summary>
    [Fact]
    public void The_reach_is_the_bakes_own_distances_and_cage() {
        var target = Quad(0f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        var up = Vector3.Normalize(target.Normal(0));
        BakeSettings settings = new() {
            Resolution = 16, Units = BakeUnits.Absolute, SearchRadius = 0.2f, RearDistance = 0.1f
        };

        var plain = MapBaker.Reach(target, target, settings);
        var stretched = MapBaker.Reach(target, target, settings with { DistanceMap = new(1, [1f]) });
        var caged = MapBaker.Reach(
            target,
            target,
            settings with { Cage = Quad(0.3f, new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)) }
        );

        for (var corner = 0; corner < target.CornerCount; corner++) {
            var point = target.Positions[target.Corners[corner]];

            Assert.True(Vector3.Distance(plain.Outer[corner], point + up * 0.2f) < 1e-5f);
            Assert.True(Vector3.Distance(plain.Inner[corner], point - up * 0.1f) < 1e-5f);
            Assert.True(Vector3.Distance(stretched.Outer[corner], point + up * 0.4f) < 1e-5f);
            Assert.True(Vector3.Distance(caged.Outer[corner], point + new Vector3(0f, 0f, 0.3f)) < 1e-5f);
            Assert.True(Vector3.Distance(caged.Inner[corner], point - new Vector3(0f, 0f, 0.1f)) < 1e-5f);
        }
    }

    /// <summary>
    ///     The lean a preview draws is the bake's: across a hard fold, a full skew casts every ray inside a face along
    ///     that face's own normal, a skew map at zero leaves them on the averaged direction that leans over the fold, and
    ///     no skew leans nothing, so there is nothing to draw.
    /// </summary>
    [Fact]
    public void The_lean_inside_a_face_is_the_bakes_own_skew() {
        var fold = new EditMesh();
        Span<int> flat = [
            fold.AddPosition(new(0f, 0f, 0f)),
            fold.AddPosition(new(1f, 0f, 0f)),
            fold.AddPosition(new(1f, 1f, 0f)),
            fold.AddPosition(new(0f, 1f, 0f))
        ];
        Span<int> side = [flat[1], fold.AddPosition(new(1f, 0f, -1f)), fold.AddPosition(new(1f, 1f, -1f)), flat[2]];
        fold.AddFace(flat);
        fold.AddFace(side);
        fold.SetNormals(
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX,
                Vector3.UnitX]
        );
        fold.SetTexCoords(
            [new(0f, 0f), new(0.5f, 0f), new(0.5f, 1f), new(0f, 1f), new(0.5f, 0f), new(1f, 0f), new(1f, 1f),
                new(0.5f, 1f)]
        );
        BakeSettings settings = new() { Resolution = 16, Units = BakeUnits.Absolute, SearchRadius = 0.2f };

        Vector3 Along(BakeLeanRay ray) => (ray.Outer - ray.Point) / 0.2f;
        bool OnTop(BakeLeanRay ray) => ray.Point.X < 0.999f;

        Assert.Empty(MapBaker.Leans(fold, fold, settings, 4));

        var full = MapBaker.Leans(fold, fold, settings with { Skew = 1f }, 4);
        Assert.Equal(12, full.Count);
        Assert.All(
            full,
            ray => Assert.True(Vector3.Distance(Along(ray), OnTop(ray) ? Vector3.UnitZ : Vector3.UnitX) < 1e-4f)
        );

        var unmapped = MapBaker.Leans(fold, fold, settings with { Skew = 1f, SkewMap = new(1, [0f]) }, 4);
        Assert.Equal(12, unmapped.Count);
        Assert.Contains(unmapped, ray => OnTop(ray) && Along(ray).X > 0.05f);
        Assert.All(unmapped, ray => Assert.InRange(Along(ray).Length(), 0.999f, 1.001f));
    }
}
