// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Xunit;

namespace Vixen.Interchange.Tests;

/// <summary>A bake's view of a file: world space, one mesh, and its hard edges still hard.</summary>
public sealed class ModelGeometryFlattenTests {
    static ReadModel Model() {
        // Two triangles sharing an edge by position and not by normal, as a file stores a hard edge.
        MeshData mesh = new() {
            Name = "bolt_low",
            Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 0f), new(0f, 1f, 0f), new(-1f, 0f, 1f)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f), new(0.5f, 0f), new(0.5f, 1f), new(1f, 1f)],
            Indices = [0, 1, 2, 3, 4, 5]
        };

        var node = new ModelNode { Name = "bolt_low", Transform = Matrix4x4.FromTranslation(new(0f, 0f, 10f)) };
        var part = new ModelPart { Mesh = "bolt_low", Node = 0, Material = 3 };

        return new(new() { Nodes = [node], Parts = [part] }, [mesh], null, []);
    }

    [Fact]
    public void A_hard_edge_shares_its_points_and_keeps_its_two_normals() {
        var flat = ModelGeometry.Flatten(Model());

        // Six vertices in the file, four points on the model.
        Assert.Equal(4, flat.Mesh.PositionCount);
        Assert.Equal(2, flat.Mesh.FaceCount);
        Assert.Equal(Vector3.UnitZ, flat.Mesh.Normals[0]);
        Assert.Equal(Vector3.UnitY, flat.Mesh.Normals[3]);
        Assert.Equal(new Vector2(0.5f, 0f), flat.Mesh.TexCoords[3]);
    }

    [Fact]
    public void Everything_is_in_world_space_and_the_parts_are_counted() {
        var flat = ModelGeometry.Flatten(Model());

        Assert.Equal(new Vector3(0f, 0f, 10f), flat.Mesh.Positions[0]);
        Assert.Equal([("bolt_low", 2)], flat.Parts);
    }

    [Fact]
    public void The_groups_are_the_files_materials() {
        var flat = ModelGeometry.Flatten(Model());

        Assert.Equal(MeshGroupSource.Assigned, flat.Mesh.GroupSource);
        Assert.Equal(3, flat.Mesh.Faces[0].Group);
    }

    static ReadModel Coloured(params Vector4[] colours) {
        var model = Model();
        model.Meshes[0].Colours = colours;
        return model;
    }

    [Fact]
    public void Vertex_colours_are_the_groups_when_a_file_has_them() {
        Vector4 red = new(1f, 0f, 0f, 1f);
        Vector4 blue = new(0f, 0f, 1f, 1f);

        var flat = ModelGeometry.Flatten(Coloured(red, red, red, blue, blue, blue));

        Assert.True(flat.ColouredGroups);
        Assert.Equal(0xFF0000, flat.Mesh.Faces[0].Group);
        Assert.Equal(0x0000FF, flat.Mesh.Faces[1].Group);
    }

    [Fact]
    public void A_face_across_a_painted_border_takes_a_colour_nobody_mixed() {
        Vector4 red = new(1f, 0f, 0f, 1f);
        Vector4 blue = new(0f, 0f, 1f, 1f);

        var flat = ModelGeometry.Flatten(Coloured(red, blue, blue, red, red, blue));

        Assert.Equal(0x0000FF, flat.Mesh.Faces[0].Group);
        Assert.Equal(0xFF0000, flat.Mesh.Faces[1].Group);
    }

    [Fact]
    public void A_colour_reads_as_the_artist_saw_it() =>
        Assert.Equal(0xBCBCBC, ModelGeometry.PackedColour(new(0.5f, 0.5f, 0.5f, 1f)));

    [Fact]
    public void With_no_colours_the_groups_stay_materials() =>
        Assert.False(ModelGeometry.Flatten(Model()).ColouredGroups);
}
