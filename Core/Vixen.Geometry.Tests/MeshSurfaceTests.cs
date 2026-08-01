// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>Doc 24's P5, kernel side: where a face's texels come from and which normals it shades with.</summary>
public class MeshSurfaceTests {
    [Fact]
    public void A_world_projection_gives_two_boxes_of_different_sizes_texels_of_the_same_size() {
        // The complaint doc 24's P5 opens with, as a test. Two walls of the same shape, one of them
        // eight metres long and one two, and a checker whose squares have to come out the same size.
        var small = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = new(2f, 3f, 0.2f) });
        var large = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = new(8f, 3f, 0.2f) });

        MeshSurfaces.Project(small, null, UvProjection.World);
        MeshSurfaces.Project(large, null, UvProjection.World);

        Assert.Equal(Span(small, MeshShapes.GroupFront).Y, Span(large, MeshShapes.GroupFront).Y, 3);

        // And the long one covers four times as many repeats across, which is what "a metre is a
        // metre" means when said about the other axis.
        Assert.Equal(2f, Span(small, MeshShapes.GroupFront).X, 3);
        Assert.Equal(8f, Span(large, MeshShapes.GroupFront).X, 3);
    }

    [Fact]
    public void A_world_projection_follows_the_entity_and_a_box_projection_does_not() {
        var moved = Matrix4x4.FromTranslation(new(17f, 0f, 5f));

        var world = MeshShapes.Create(ShapeKind.Box);
        var box = MeshShapes.Create(ShapeKind.Box);
        var here = MeshShapes.Create(ShapeKind.Box);

        MeshSurfaces.Project(world, null, UvProjection.World, toWorld: moved);
        MeshSurfaces.Project(box, null, UvProjection.Box, toWorld: moved);
        MeshSurfaces.Project(here, null, UvProjection.Box);

        // ⚠ The point of having both. A world projection is nailed to the level, so a wall dragged
        // sideways slides under its own texture and stays aligned with the floor; a box projection
        // travels with the object and comes out identical wherever the object is, which is what a
        // prop wants and a wall does not.
        Assert.NotEqual(world.TexCoords[0], box.TexCoords[0]);
        Assert.Equal(here.TexCoords[0], box.TexCoords[0]);
    }

    [Fact]
    public void Only_the_faces_named_are_mapped() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        MeshSurfaces.Project(mesh, [0], UvProjection.Box, scale: 0.5f);

        var untouched = mesh.Faces[1];

        for (var corner = 0; corner < untouched.Count; corner++) {
            Assert.Equal(Vector2.Zero, mesh.TexCoords[untouched.Start + corner]);
        }

        Assert.NotEqual(Vector2.Zero, mesh.TexCoords[mesh.Faces[0].Start]);
    }

    [Fact]
    public void A_scale_of_two_metres_a_repeat_halves_the_numbers() {
        var one = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = new(4f, 4f, 4f) });
        var two = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = new(4f, 4f, 4f) });

        MeshSurfaces.Project(one, null, UvProjection.Box, scale: 1f);
        MeshSurfaces.Project(two, null, UvProjection.Box, scale: 2f);

        Assert.Equal(Span(one, MeshShapes.GroupFront).X / 2f, Span(two, MeshShapes.GroupFront).X, 3);
    }

    [Fact]
    public void Fitting_a_face_puts_it_exactly_in_the_unit_square() {
        var mesh = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = new(7f, 3f, 2f) });

        MeshSurfaces.Project(mesh, null, UvProjection.Box);
        MeshSurfaces.Fit(mesh, [0]);

        var face = mesh.Faces[0];

        for (var corner = 0; corner < face.Count; corner++) {
            var uv = mesh.TexCoords[face.Start + corner];

            Assert.InRange(uv.X, 0f, 1f);
            Assert.InRange(uv.Y, 0f, 1f);
        }

        Assert.Equal(1f, Span(mesh, mesh.Faces[0].Group).X, 3);
        Assert.Equal(1f, Span(mesh, mesh.Faces[0].Group).Y, 3);
    }

    [Fact]
    public void A_rotation_turns_a_face_about_its_own_centre_rather_than_about_the_origin() {
        var mesh = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = new(2f, 2f, 2f) });

        MeshSurfaces.Project(mesh, null, UvProjection.Box);

        var before = Centre(mesh, 0);

        MeshSurfaces.Transform(mesh, [0], rotation: MathF.PI * 0.5f);

        // ⚠ The whole of what "about its own centre" buys: a quarter turn of a wall's texture leaves
        // the texture on the wall. A matrix multiply about the mapping's origin would have swung it
        // off into the next room, which is what makes a rotate field feel broken.
        Assert.Equal(before.X, Centre(mesh, 0).X, 3);
        Assert.Equal(before.Y, Centre(mesh, 0).Y, 3);

        // A square face turned a quarter of a turn is the same square, so the extents have swapped
        // rather than changed.
        Assert.Equal(2f, Span(mesh, mesh.Faces[0].Group).X, 3);
    }

    [Fact]
    public void An_offset_moves_a_face_and_leaves_its_neighbours() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        MeshSurfaces.Project(mesh, null, UvProjection.Box);

        var neighbour = mesh.TexCoords[mesh.Faces[1].Start];
        var before = mesh.TexCoords[mesh.Faces[0].Start];

        MeshSurfaces.Transform(mesh, [0], offset: new(0.25f, -0.5f));

        Assert.Equal(before + new Vector2(0.25f, -0.5f), mesh.TexCoords[mesh.Faces[0].Start]);
        Assert.Equal(neighbour, mesh.TexCoords[mesh.Faces[1].Start]);
    }

    [Fact]
    public void Auto_smoothing_a_cylinder_makes_its_wall_one_surface_and_leaves_its_rims_hard() {
        var mesh = MeshShapes.Create(
            new ShapeParameters { Kind = ShapeKind.Cylinder, Size = new(2f, 3f, 2f), Sides = 24 }
        );

        var groups = MeshSurfaces.AutoSmooth(mesh);

        // One group: the wall. The caps meet it at a right angle and stay hard, which is the
        // difference between a cylinder and a very lumpy sphere.
        Assert.Equal(1, groups);

        var wall = mesh.Faces.Where(face => face.Group == MeshShapes.GroupSide).ToList();

        Assert.Equal(24, wall.Count);
        Assert.All(wall, face => Assert.NotEqual(0, face.Smoothing));

        Assert.All(
            mesh.Faces.Where(face => face.Group != MeshShapes.GroupSide),
            face => Assert.Equal(0, face.Smoothing)
        );
    }

    [Fact]
    public void Auto_smoothing_a_box_leaves_every_face_hard() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        Assert.Equal(0, MeshSurfaces.AutoSmooth(mesh));
        Assert.All(mesh.Faces, face => Assert.Equal(0, face.Smoothing));
    }

    [Fact]
    public void A_hard_face_shades_flat_and_a_smoothed_one_averages_with_its_group() {
        var mesh = MeshShapes.Create(
            new ShapeParameters { Kind = ShapeKind.Cylinder, Size = new(2f, 3f, 2f), Sides = 24 }
        );

        var flat = MeshSurfaces.Normals(mesh);

        MeshSurfaces.AutoSmooth(mesh);

        var smooth = MeshSurfaces.Normals(mesh);

        var wall = mesh.Faces.Index().First(entry => entry.Item.Group == MeshShapes.GroupSide);
        var cap = mesh.Faces.Index().First(entry => entry.Item.Group == MeshShapes.GroupTop);

        // The wall's corners moved off the face normal, which is the whole of what smoothing is.
        Assert.NotEqual(flat[wall.Item.Start], smooth[wall.Item.Start]);
        Assert.Equal(1f, smooth[wall.Item.Start].Length(), 3);

        // ⚠ And the cap did not, even though its corners are shared with the wall's. A group boundary
        // is what stops the average, and a cap that softened here would be a cylinder whose rim reads
        // as a smear rather than as an edge.
        Assert.Equal(flat[cap.Item.Start], smooth[cap.Item.Start]);
        Assert.Equal(mesh.Normal(cap.Index), smooth[cap.Item.Start]);
    }

    [Fact]
    public void Smoothing_survives_an_extrude() {
        var mesh = MeshShapes.Create(ShapeKind.Cylinder);

        MeshSurfaces.AutoSmooth(mesh);

        var top = mesh.Faces.Index().First(entry => entry.Item.Group == MeshShapes.GroupTop);

        MeshOperations.Extrude(mesh, [top.Index], 2f);

        // ⚠ A verb that carried the group and dropped the smoothing would give back a cylinder that
        // is materialled correctly and faceted, which reads as a renderer bug rather than as the
        // extrude that caused it. Every operation carries both — see `MeshLoop`.
        Assert.Contains(mesh.Faces, face => face.Group == MeshShapes.GroupSide && face.Smoothing != 0);
    }

    /// <summary>How far a group's coordinates run, on each axis.</summary>
    static Vector2 Span(EditMesh mesh, int group) {
        var low = new Vector2(float.MaxValue);
        var high = new Vector2(float.MinValue);

        foreach (var face in mesh.Faces) {
            if (face.Group != group) {
                continue;
            }

            for (var corner = 0; corner < face.Count; corner++) {
                low = Vector2.Min(low, mesh.TexCoords[face.Start + corner]);
                high = Vector2.Max(high, mesh.TexCoords[face.Start + corner]);
            }
        }

        return high - low;
    }

    static Vector2 Centre(EditMesh mesh, int face) {
        var entry = mesh.Faces[face];
        var total = Vector2.Zero;

        for (var corner = 0; corner < entry.Count; corner++) {
            total += mesh.TexCoords[entry.Start + corner];
        }

        return total / entry.Count;
    }
}
