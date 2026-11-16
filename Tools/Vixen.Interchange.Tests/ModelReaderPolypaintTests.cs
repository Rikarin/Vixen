// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Xunit;

namespace Vixen.Interchange.Tests;

/// <summary>ZBrush's polypaint, the <c>#MRGB</c> comments of its OBJ export, read as vertex colours.</summary>
public sealed class ModelReaderPolypaintTests {
    /// <summary>
    ///     Two triangles over four vertices, polypainted red, green, blue and white, in one run of hex as ZBrush writes
    ///     it; the mask bytes differ and are dropped. The first and last vertex are at one point, so only the order can tell
    ///     their colours apart.
    /// </summary>
    const string Painted = """
                           v 0 0 0
                           v 1 0 0
                           v 1 1 0
                           v 0 0 0
                           #MRGB ffff00008000ff00000000ffffffffff
                           vt 0 0
                           f 1 2 3
                           f 4 3 2
                           """;

    static ReadModel Read(string obj, bool colours = true) =>
        ModelReader.Read(
            Encoding.ASCII.GetBytes(obj),
            ".obj",
            "Painted",
            new ModelReadOptions { ImportVertexColours = colours, GenerateTangents = false }
        );

    /// <summary>The colour each read vertex carries, by where it is and which triangle corner it was.</summary>
    static HashSet<(Vector3, Vector4)> Pairs(MeshData mesh) =>
        [..mesh.Positions.Select((position, at) => (position, mesh.Colours[at]))];

    /// <summary>
    ///     Each vertex takes the colour of its place in the <c>#MRGB</c> lines: red, green and blue on the first
    ///     triangle and white on the corner of the second that lies where the first triangle's red one does.
    /// </summary>
    [Fact]
    public void Polypaint_is_read_as_the_vertex_colours_in_vertex_order() {
        var mesh = Assert.Single(Read(Painted).Meshes);
        var pairs = Pairs(mesh);

        Assert.Equal(mesh.Positions.Length, mesh.Colours.Length);
        Assert.Contains((new Vector3(1f, 0f, 0f), new Vector4(0f, 1f, 0f, 1f)), pairs);
        Assert.Contains((new Vector3(1f, 1f, 0f), new Vector4(0f, 0f, 1f, 1f)), pairs);
        Assert.Contains((new Vector3(0f, 0f, 0f), new Vector4(1f, 0f, 0f, 1f)), pairs);
        Assert.Contains((new Vector3(0f, 0f, 0f), new Vector4(1f, 1f, 1f, 1f)), pairs);
    }

    /// <summary>A vertex that names its own colour keeps it, and a reader that asks for none gets none.</summary>
    [Fact]
    public void A_colour_on_the_vertex_wins_and_none_is_read_unless_asked_for() {
        var own = Painted.Replace("v 1 0 0\n", "v 1 0 0 0.5 0.5 0.5\n", StringComparison.Ordinal)
            .Replace("v 1 0 0\r\n", "v 1 0 0 0.5 0.5 0.5\r\n", StringComparison.Ordinal);
        var mesh = Assert.Single(Read(own).Meshes);

        Assert.Contains((new Vector3(1f, 0f, 0f), new Vector4(0.5f, 0.5f, 0.5f, 1f)), Pairs(mesh));
        Assert.Empty(Assert.Single(Read(Painted, false).Meshes).Colours);
    }

    /// <summary>An OBJ with no polypaint is read as it was: no colours from nowhere.</summary>
    [Fact]
    public void An_obj_without_polypaint_has_no_colours() {
        var plain = Painted.Replace("#MRGB ffff00008000ff00000000ffffffffff", "# a comment", StringComparison.Ordinal);

        Assert.Empty(Assert.Single(Read(plain).Meshes).Colours);
    }
}
