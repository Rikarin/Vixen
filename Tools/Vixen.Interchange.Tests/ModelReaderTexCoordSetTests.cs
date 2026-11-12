// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Interchange;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>A model's second and later texture coordinate sets, which arrive only when asked for.</summary>
public sealed class ModelReaderTexCoordSetTests {
    [Fact]
    public void EverySetArrivesByItsChannelWhenAskedFor() {
        var read = ModelReader.Read(TwoSets(), ".gltf", "Tri", new ModelReadOptions { ImportAllTexCoords = true });

        var sets = Assert.Single(read.TexCoordSets);
        Assert.Equal(2, sets.Length);
        Assert.Equal(read.Meshes[0].TexCoords, sets[0]);
        Assert.Contains(sets[1], static coordinate => coordinate.X == 0.5f && coordinate.Y == 0.25f);
        Assert.DoesNotContain(sets[0], static coordinate => coordinate.X == 0.5f);
    }

    [Fact]
    public void NoSetsArriveUnlessAskedFor() {
        var read = ModelReader.Read(TwoSets(), ".gltf", "Tri", new ModelReadOptions());

        Assert.Empty(read.TexCoordSets);
        Assert.Equal(3, read.Meshes[0].TexCoords.Length);
    }

    /// <summary>A triangle with a layout in each of two sets: the first fills the square, the second a quarter of it.</summary>
    static byte[] TwoSets() {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        float[] first = [0, 0, 1, 0, 0, 1];
        float[] second = [0.5f, 0.25f, 0.75f, 0.25f, 0.5f, 0.5f];
        ushort[] indices = [0, 1, 2];
        var buffer = new byte[36 + 24 + 24 + 6];
        Buffer.BlockCopy(positions, 0, buffer, 0, 36);
        Buffer.BlockCopy(first, 0, buffer, 36, 24);
        Buffer.BlockCopy(second, 0, buffer, 60, 24);
        Buffer.BlockCopy(indices, 0, buffer, 84, 6);
        var json = $$"""
                     {
                       "asset": { "version": "2.0" },
                       "scene": 0,
                       "scenes": [{ "nodes": [0] }],
                       "nodes": [{ "mesh": 0 }],
                       "meshes": [{
                         "name": "Tri",
                         "primitives": [{ "attributes": { "POSITION": 0, "TEXCOORD_0": 1, "TEXCOORD_1": 2 }, "indices": 3 }]
                       }],
                       "buffers": [{
                         "byteLength": {{buffer.Length}},
                         "uri": "data:application/octet-stream;base64,{{Convert.ToBase64String(buffer)}}"
                       }],
                       "bufferViews": [
                         { "buffer": 0, "byteOffset": 0, "byteLength": 36 },
                         { "buffer": 0, "byteOffset": 36, "byteLength": 24 },
                         { "buffer": 0, "byteOffset": 60, "byteLength": 24 },
                         { "buffer": 0, "byteOffset": 84, "byteLength": 6 }
                       ],
                       "accessors": [
                         { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                         { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC2" },
                         { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC2" },
                         { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" }
                       ]
                     }
                     """;
        return Encoding.UTF8.GetBytes(json);
    }
}
