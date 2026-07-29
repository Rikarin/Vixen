// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The shapes the engine builds rather than imports.</summary>
public class MeshPrimitivesTests {
    public static TheoryData<PrimitiveKind> Kinds {
        get {
            var data = new TheoryData<PrimitiveKind>();

            foreach (var kind in Enum.GetValues<PrimitiveKind>()) {
                data.Add(kind);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_shape_is_a_whole_number_of_triangles(PrimitiveKind kind) {
        var mesh = MeshPrimitives.Create(kind);

        Assert.NotEmpty(mesh.Positions);
        Assert.NotEmpty(mesh.Indices);

        // The topology takes them three at a time. A leftover index draws a triangle joined to
        // whatever the buffer happens to hold next, which is a shard across the viewport.
        Assert.Equal(0, mesh.Indices.Length % 3);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_index_names_a_vertex_that_exists(PrimitiveKind kind) {
        var mesh = MeshPrimitives.Create(kind);

        foreach (var index in mesh.Indices) {
            // Out of range is a read past the end of a vertex buffer: undefined behaviour rather
            // than a missing face, and invisible until it is a crash on somebody else's driver.
            Assert.InRange(index, 0, mesh.VertexCount - 1);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_shape_has_a_normal_and_a_texture_coordinate_per_vertex(PrimitiveKind kind) {
        var mesh = MeshPrimitives.Create(kind);

        Assert.Equal(mesh.VertexCount, mesh.Normals.Length);
        Assert.Equal(mesh.VertexCount, mesh.TexCoords.Length);

        foreach (var normal in mesh.Normals) {
            Assert.Equal(1f, normal.Length(), 3);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_shape_fits_the_unit_cube(PrimitiveKind kind) {
        var mesh = MeshPrimitives.Create(kind);

        // The one rule the whole set keeps, and the reason a spawn menu can drop any of them at a
        // point and have them arrive the same size. A shape added that breaks it makes `scale: 2`
        // mean two different things depending on which shape it is on.
        foreach (var position in mesh.Positions) {
            Assert.InRange(position.X, -0.5f, 0.5f);
            Assert.InRange(position.Y, -0.5f, 0.5f);
            Assert.InRange(position.Z, -0.5f, 0.5f);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_bounds_are_the_bounds(PrimitiveKind kind) {
        var mesh = MeshPrimitives.Create(kind);

        foreach (var position in mesh.Positions) {
            Assert.True(position.X >= mesh.Bounds.Minimum.X && position.X <= mesh.Bounds.Maximum.X);
            Assert.True(position.Y >= mesh.Bounds.Minimum.Y && position.Y <= mesh.Bounds.Maximum.Y);
            Assert.True(position.Z >= mesh.Bounds.Minimum.Z && position.Z <= mesh.Bounds.Maximum.Z);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void No_triangle_is_degenerate(PrimitiveKind kind) {
        var mesh = MeshPrimitives.Create(kind);

        for (var triangle = 0; triangle < mesh.Indices.Length; triangle += 3) {
            var a = mesh.Positions[mesh.Indices[triangle]];
            var b = mesh.Positions[mesh.Indices[triangle + 1]];
            var c = mesh.Positions[mesh.Indices[triangle + 2]];

            // A pole's quad collapses to a line and `Band` is meant to skip both halves of it. One
            // that survives costs bandwidth, shows up in a triangle count somebody is reading, and
            // produces a NaN normal in anything that recomputes them.
            Assert.True(Vector3.Cross(b - a, c - a).Length() > 1e-7f, $"Triangle {triangle / 3} has no area.");
        }
    }

    [Fact]
    public void A_cube_has_a_hard_edge_at_every_corner() {
        var cube = MeshPrimitives.Cube();

        // Six faces of four. Eight shared corners would be a cube lit as a very lumpy sphere.
        Assert.Equal(24, cube.VertexCount);
        Assert.Equal(12, cube.TriangleCount);
    }

    [Fact]
    public void A_cubes_faces_point_the_six_ways_they_should() {
        var cube = MeshPrimitives.Cube();
        var seen = new HashSet<(int X, int Y, int Z)>();

        foreach (var normal in cube.Normals) {
            seen.Add(((int) MathF.Round(normal.X), (int) MathF.Round(normal.Y), (int) MathF.Round(normal.Z)));
        }

        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void A_spheres_vertices_are_all_the_same_distance_from_the_middle() {
        foreach (var position in MeshPrimitives.Sphere().Positions) {
            Assert.Equal(0.5f, position.Length(), 4);
        }
    }

    [Fact]
    public void A_shapes_faces_are_wound_the_same_way_round() {
        // Every triangle of a closed convex shape should face away from the middle, which is what
        // "counter-clockwise seen from outside" means once you stop taking it on trust. A single
        // face wound the other way is invisible under back-face culling and impossible to spot in a
        // screenshot of a shape that is mostly right.
        var sphere = MeshPrimitives.Sphere();

        for (var triangle = 0; triangle < sphere.Indices.Length; triangle += 3) {
            var a = sphere.Positions[sphere.Indices[triangle]];
            var b = sphere.Positions[sphere.Indices[triangle + 1]];
            var c = sphere.Positions[sphere.Indices[triangle + 2]];

            var facing = Vector3.Cross(b - a, c - a);
            var outward = (a + b + c) * (1f / 3f);

            Assert.True(Vector3.Dot(facing, outward) > 0f, $"Triangle {triangle / 3} is wound inside out.");
        }
    }

    [Fact]
    public void A_capsule_that_is_no_taller_than_it_is_wide_is_refused() {
        // It would be a sphere, and silently building one is worse than saying so: the caller asked
        // for a capsule and would get geometry that is right and not what they described.
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPrimitives.Capsule(0.25f, 0.4f));
    }

    [Fact]
    public void A_shape_too_coarse_to_be_a_solid_is_refused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPrimitives.Sphere(0.5f, 2, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPrimitives.Cylinder(0.5f, 1f, 2));
    }

    [Fact]
    public void A_kind_that_is_not_one_is_refused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPrimitives.Create((PrimitiveKind) 99));
    }

    [Fact]
    public void A_plane_is_subdivided_and_a_quad_is_not() {
        // The two exist to be different: a quad is two triangles and a plane is what somebody puts a
        // displaced or vertex-lit material on.
        Assert.Equal(2, MeshPrimitives.Quad().TriangleCount);
        Assert.True(MeshPrimitives.Create(PrimitiveKind.Plane).TriangleCount > 2);
    }
}
