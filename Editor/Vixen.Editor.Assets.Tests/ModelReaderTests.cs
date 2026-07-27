// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Against real files, read by the real Assimp. The fixtures are OBJ and glTF because both are
///     text and can be written in the test that needs them — a binary fixture checked in beside the
///     tests is a thing nobody can edit and nobody can read the diff of.
/// </summary>
public sealed class ModelReaderTests {
    static readonly ModelImportSettings Default = new();

    [Fact]
    public void ATriangleComesOutAsOneMeshWithItsAttributes() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default);

        var mesh = Assert.Single(read.Meshes);

        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(3, mesh.Normals.Length);
        Assert.Equal(3, mesh.TexCoords.Length);
        Assert.False(mesh.IsSkinned);
    }

    [Fact]
    public void TheModelNamesItsPartsRatherThanNumberingThem() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default);

        var part = Assert.Single(read.Model.Parts);

        Assert.Equal(read.Meshes[0].Name, part.Mesh);
        Assert.InRange(part.Node, 0, read.Model.Nodes.Length - 1);
    }

    /// <summary>
    ///     Assimp names the root of a memory import <c>$$$___magic___$$$</c>, which is an artefact of
    ///     how it was handed the bytes rather than anything in the file. Nobody wants that in an
    ///     inspector.
    /// </summary>
    [Fact]
    public void TheRootNodeIsCalledAfterTheAssetRatherThanAfterAssimpsPlaceholder() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default);

        Assert.Equal("Shape", read.Model.Nodes[0].Name);
        Assert.Equal(-1, read.Model.Nodes[0].Parent);
    }

    [Fact]
    public void ScaleMultipliesEveryLength() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default with { Scale = 100f });

        Assert.Equal(100f, read.Meshes[0].Positions.Max(position => position.X), 3);
        Assert.Equal(100f, read.Model.Bounds.Maximum.X, 3);
    }

    /// <summary>
    ///     The one that would be quietly wrong for ever. Assimp stores a column-vector matrix
    ///     row-major, so a node's translation is in its fourth <em>column</em>; Vixen's row-vector
    ///     matrix keeps it in the fourth <em>row</em>. A field-for-field copy compiles, runs, and
    ///     assembles every hierarchy inside out.
    /// </summary>
    [Fact]
    public void ANodeTranslationLandsInTheRowVixenKeepsItIn() {
        var read = ModelReader.Read(Gltf(3f, 4f, 5f), ".gltf", "Offset", Default);

        // Whichever node the mesh hangs off. Assimp collapses a single-node glTF scene onto the
        // root, so naming an index here would pin an Assimp detail rather than the conversion.
        var node = read.Model.Nodes[Assert.Single(read.Model.Parts).Node];

        Assert.Equal(3f, node.Transform.M41, 3);
        Assert.Equal(4f, node.Transform.M42, 3);
        Assert.Equal(5f, node.Transform.M43, 3);
    }

    /// <summary>
    ///     Bounds are the union of each part put through its node's world transform. A mesh at the
    ///     origin hanging off a node three metres along X occupies a box three metres along X, and
    ///     bounds that ignored the hierarchy would cull it away.
    /// </summary>
    [Fact]
    public void BoundsFollowTheHierarchyRatherThanTheMeshesAsTheySit() {
        var read = ModelReader.Read(Gltf(3f, 0f, 0f), ".gltf", "Offset", Default);

        Assert.Equal(3f, read.Model.Bounds.Minimum.X, 3);
        Assert.Equal(4f, read.Model.Bounds.Maximum.X, 3);
    }

    /// <summary>
    ///     Sub-asset ids are derived from names, so two meshes called the same thing derive one id
    ///     and the import is refused outright. An exporter naming every mesh after one source object
    ///     is ordinary, so this is the common case rather than the pathological one.
    /// </summary>
    [Fact]
    public void TwoMeshesWithOneNameAreGivenDistinctOnes() {
        var read = ModelReader.Read(GltfTwins(), ".gltf", "Shape", Default);

        Assert.Equal(2, read.Meshes.Length);
        Assert.Equal(2, read.Meshes.Select(mesh => mesh.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, read.Model.Parts.Select(part => part.Mesh).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SomethingThatIsNotAModelSaysSoRatherThanReturningAnEmptyOne() {
        var failure = Assert.Throws<ModelFormatException>(
            () => ModelReader.Read("{ this is not glTF"u8, ".gltf", "Shape", Default)
        );

        Assert.NotEmpty(failure.Message);
    }

    /// <summary>
    ///     Assimp's OBJ reader ignores lines it does not recognise, so prose with an <c>.obj</c>
    ///     extension parses successfully into a scene with nothing in it. That is not an error the
    ///     reader can invent — the file really did parse — so it comes out as a model with no meshes,
    ///     and the importer is what says so.
    /// </summary>
    [Fact]
    public void AFileThatParsesIntoNothingIsAModelWithNoMeshesRatherThanAFailure() {
        var read = ModelReader.Read("this is not a model"u8, ".obj", "Shape", Default);

        Assert.Empty(read.Meshes);
    }

    [Fact]
    public void AnEmptyFileIsRefusedBeforeAssimpSeesIt() {
        Assert.Throws<ModelFormatException>(() => ModelReader.Read([], ".obj", "Shape", Default));
    }

    [Fact]
    public void TurningTangentsOffLeavesTheArrayEmptyRatherThanZeroed() {
        var read = ModelReader.Read(Obj(Triangle), ".obj", "Shape", Default with { GenerateTangents = false });

        Assert.Empty(read.Meshes[0].Tangents);
        Assert.NotEmpty(read.Meshes[0].Normals);
    }

    const string Triangle = """
        o Tri
        v 0 0 0
        v 1 0 0
        v 0 1 0
        vt 0 0
        vt 1 0
        vt 0 1
        f 1/1 2/2 3/3
        """;

    static byte[] Obj(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    ///     A glTF with one triangle under one translated node, assembled here rather than checked in.
    /// </summary>
    /// <remarks>
    ///     The buffer is built and base64'd at run time, so the fixture reads as the geometry it is
    ///     rather than as a wall of encoded bytes nobody can verify by looking.
    /// </remarks>
    static byte[] Gltf(float x, float y, float z) {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        ushort[] indices = [0, 1, 2];

        var buffer = new byte[(positions.Length * 4) + (indices.Length * 2)];
        Buffer.BlockCopy(positions, 0, buffer, 0, positions.Length * 4);
        Buffer.BlockCopy(indices, 0, buffer, positions.Length * 4, indices.Length * 2);

        var json = $$"""
            {
              "asset": { "version": "2.0" },
              "scene": 0,
              "scenes": [{ "nodes": [0] }],
              "nodes": [{ "name": "Offset", "translation": [{{F(x)}}, {{F(y)}}, {{F(z)}}], "mesh": 0 }],
              "meshes": [{
                "name": "Tri",
                "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1 }]
              }],
              "buffers": [{
                "byteLength": {{buffer.Length}},
                "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}"
              }],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length * 4}}, "target": 34962 },
                { "buffer": 0, "byteOffset": {{positions.Length * 4}}, "byteLength": {{indices.Length * 2}}, "target": 34963 }
              ],
              "accessors": [
                {
                  "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3",
                  "min": [0, 0, 0], "max": [1, 1, 0]
                },
                { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
              ]
            }
            """;

        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>Two meshes, both called <c>Tri</c>, which is what an exporter does all the time.</summary>
    static byte[] GltfTwins() {
        var text = Encoding.UTF8.GetString(Gltf(0f, 0f, 0f))
            .Replace(
                "\"nodes\": [{ \"name\": \"Offset\", \"translation\": [0.0, 0.0, 0.0], \"mesh\": 0 }]",
                "\"nodes\": [{ \"name\": \"A\", \"mesh\": 0 }, { \"name\": \"B\", \"mesh\": 1 }]",
                StringComparison.Ordinal
            )
            .Replace("\"nodes\": [0]", "\"nodes\": [0, 1]", StringComparison.Ordinal)
            .Replace(
                "\"meshes\": [{\n    \"name\": \"Tri\",\n    \"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }]\n  }]",
                "\"meshes\": [{ \"name\": \"Tri\", \"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }] },"
                + " { \"name\": \"Tri\", \"primitives\": [{ \"attributes\": { \"POSITION\": 0 }, \"indices\": 1 }] }]",
                StringComparison.Ordinal
            );

        return Encoding.UTF8.GetBytes(text);
    }

    static string F(float value) => value.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
}
