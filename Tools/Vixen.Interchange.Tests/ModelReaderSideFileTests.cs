// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Interchange;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The files a model names beside itself, read through the caller's resolver. Before it, a model read from its
///     bytes looked for them in the process's working directory: an OBJ's materials all came back as Assimp's default.
/// </summary>
public sealed class ModelReaderSideFileTests {
    static readonly ModelReadOptions Default = new() { GenerateTangents = false };

    const string Quad = """
                        mtllib paint.mtl
                        o Quad
                        v 0 0 0
                        v 1 0 0
                        v 1 1 0
                        v 0 1 0
                        vt 0 0
                        vt 1 0
                        vt 1 1
                        vt 0 1
                        usemtl Red
                        f 1/1 2/2 3/3
                        usemtl Blue
                        f 1/1 3/3 4/4
                        """;

    const string Library = """
                           newmtl Red
                           Kd 1 0 0
                           newmtl Blue
                           Kd 0 0 1
                           """;

    [Fact]
    public void AnObjFindsItsMaterialLibraryThroughTheResolver() {
        var asked = new List<string>();
        var read = ModelReader.Read(
            Bytes(Quad),
            ".obj",
            "Quad",
            Default,
            sideFiles: path => {
                asked.Add(path);
                return path == "paint.mtl" ? Bytes(Library) : null;
            }
        );

        Assert.Contains("paint.mtl", asked);
        var red = Array.IndexOf(read.Model.Materials, "Red");
        var blue = Array.IndexOf(read.Model.Materials, "Blue");
        Assert.True(red >= 0 && blue >= 0, string.Join(", ", read.Model.Materials));
        Assert.Equal(1f, read.MaterialFactors[red].BaseColor.X);
        Assert.Equal(0f, read.MaterialFactors[red].BaseColor.Z);
        Assert.Equal(1f, read.MaterialFactors[blue].BaseColor.Z);
        Assert.Equal(2, read.Meshes.Length);
    }

    /// <summary>
    ///     With nothing to read side files the library is missing: the faces keep the names they ask for, and every one
    ///     of them has the default colour.
    /// </summary>
    [Fact]
    public void WithoutAResolverAnObjsMaterialsHaveNoColour() {
        var read = ModelReader.Read(Bytes(Quad), ".obj", "Quad", Default);

        var red = Array.IndexOf(read.Model.Materials, "Red");
        Assert.True(red < 0 || read.MaterialFactors[red].BaseColor.Z > 0f);
    }

    /// <summary>The resolver is asked for the path as the model spells it, folders and all.</summary>
    [Fact]
    public void TheResolverIsAskedForThePathAsTheModelSpellsIt() {
        var asked = new List<string>();
        ModelReader.Read(
            Bytes(Quad.Replace("mtllib paint.mtl", "mtllib looks/paint.mtl", StringComparison.Ordinal)),
            ".obj",
            "Quad",
            Default,
            sideFiles: path => {
                asked.Add(path);
                return null;
            }
        );

        Assert.Contains("looks/paint.mtl", asked);
    }

    /// <summary>A resolver that throws is a file that is not there, and the report says why.</summary>
    [Fact]
    public void AResolverThatThrowsIsAMissingFileAndAWarning() {
        var said = new List<(ImportSeverity Severity, string Message)>();
        var read = ModelReader.Read(
            Bytes(Quad),
            ".obj",
            "Quad",
            Default,
            (severity, message) => said.Add((severity, message)),
            static _ => throw new IOException("locked")
        );

        Assert.NotEmpty(read.Meshes);
        Assert.Contains(
            said,
            static line => line.Severity == ImportSeverity.Warning && line.Message.Contains("locked")
        );
    }

    /// <summary>A <c>.gltf</c> whose buffer is a file beside it reads, which from bytes alone it did not.</summary>
    [Fact]
    public void AGltfReadsItsBufferFromBesideIt() {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        ushort[] indices = [0, 1, 2];
        var buffer = new byte[positions.Length * 4 + indices.Length * 2];
        Buffer.BlockCopy(positions, 0, buffer, 0, positions.Length * 4);
        Buffer.BlockCopy(indices, 0, buffer, positions.Length * 4, indices.Length * 2);
        var json = $$"""
                     {
                       "asset": { "version": "2.0" },
                       "scene": 0,
                       "scenes": [{ "nodes": [0] }],
                       "nodes": [{ "mesh": 0 }],
                       "meshes": [{ "name": "Tri", "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1 }] }],
                       "buffers": [{ "byteLength": {{buffer.Length}}, "uri": "tri.bin" }],
                       "bufferViews": [
                         { "buffer": 0, "byteOffset": 0, "byteLength": 36, "target": 34962 },
                         { "buffer": 0, "byteOffset": 36, "byteLength": 6, "target": 34963 }
                       ],
                       "accessors": [
                         { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                         { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
                       ]
                     }
                     """;

        var read = ModelReader.Read(
            Bytes(json),
            ".gltf",
            "Tri",
            Default,
            sideFiles: path => path == "tri.bin" ? buffer : null
        );

        Assert.Equal(1, Assert.Single(read.Meshes).TriangleCount);
        Assert.Throws<ModelFormatException>(() => ModelReader.Read(Bytes(json), ".gltf", "Tri", Default));
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
