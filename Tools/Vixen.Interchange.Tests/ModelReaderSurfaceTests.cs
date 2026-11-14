// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Interchange;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     What a glTF material says beyond its images, read through Assimp: emission, alpha, and where each image lands.
///     The file is written by hand here, so the numbers are glTF's and not a writer's idea of them.
/// </summary>
public sealed class ModelReaderSurfaceTests {
    [Fact]
    public void EmissionAndAlphaComeBackAsTheFileWroteThem() {
        var textures = Read(Material).MaterialTextures[0];

        Assert.Equal(new Vector3(1f, 0.5f, 0.25f), textures.EmissiveFactor);
        Assert.Equal(4f, textures.EmissiveStrength);
        Assert.Equal(ModelAlphaMode.Mask, textures.AlphaMode);
        Assert.Equal(0.3f, textures.AlphaCutoff, 5);
    }

    [Fact]
    public void AMaterialThatSaysNothingIsOpaqueAndDark() {
        var textures = Read("""{ "name": "Plain" }""").MaterialTextures[0];

        Assert.Equal(Vector3.Zero, textures.EmissiveFactor);
        Assert.Equal(1f, textures.EmissiveStrength);
        Assert.Equal(ModelAlphaMode.Opaque, textures.AlphaMode);
        Assert.Equal(0.5f, textures.AlphaCutoff);
    }

    /// <summary>
    ///     ⚠ Assimp stores the transform in its own convention, and taken as stored this offset reads (1.13, 0.89);
    ///     the reader gives back the file's own numbers.
    /// </summary>
    [Fact]
    public void ATextureTransformComesBackInGltfsOwnTerms() {
        var colour = Read(Material).MaterialTextures[0].BaseColor!;

        var transform = Assert.IsType<ModelTextureTransform>(colour.Transform);
        Assert.Equal(0.25f, transform.Offset.X, 4);
        Assert.Equal(-0.5f, transform.Offset.Y, 4);
        Assert.Equal(0.7f, transform.Rotation, 4);
        Assert.Equal(2f, transform.Scale.X, 4);
        Assert.Equal(3f, transform.Scale.Y, 4);
        Assert.Equal(0, colour.UvSet);
    }

    [Fact]
    public void AnImageThatReadsTheSecondSetSaysSoAndOneWithoutATransformHasNone() {
        var emissive = Read(Material).MaterialTextures[0].Emissive!;

        Assert.Equal(1, emissive.UvSet);
        Assert.Null(emissive.Transform);
    }

    [Fact]
    public void ApplyIsGltfsMatrix() {
        var transform = new ModelTextureTransform(new(0.1f, 0.2f), MathF.PI / 2f, new(2f, 1f));

        // Scaled to (2, 0), turned a quarter (counter-clockwise in UV, so +x goes to −y), then moved.
        var moved = transform.Apply(new(1f, 0f));

        Assert.Equal(0.1f, moved.X, 4);
        Assert.Equal(-1.8f, moved.Y, 4);
    }

    const string Material = """
                            {
                              "name": "Glow",
                              "pbrMetallicRoughness": {
                                "baseColorTexture": {
                                  "index": 0,
                                  "extensions": {
                                    "KHR_texture_transform": { "offset": [0.25, -0.5], "rotation": 0.7, "scale": [2, 3] }
                                  }
                                }
                              },
                              "emissiveTexture": { "index": 1, "texCoord": 1 },
                              "emissiveFactor": [1, 0.5, 0.25],
                              "alphaMode": "MASK",
                              "alphaCutoff": 0.3,
                              "extensions": { "KHR_materials_emissive_strength": { "emissiveStrength": 4 } }
                            }
                            """;

    /// <summary>A triangle with two coordinate sets wearing <paramref name="material" />.</summary>
    static ReadModel Read(string material) {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        float[] coordinates = [0, 0, 1, 0, 0, 1];
        ushort[] indices = [0, 1, 2];
        var buffer = new byte[36 + 24 + 24 + 6];
        Buffer.BlockCopy(positions, 0, buffer, 0, 36);
        Buffer.BlockCopy(coordinates, 0, buffer, 36, 24);
        Buffer.BlockCopy(coordinates, 0, buffer, 60, 24);
        Buffer.BlockCopy(indices, 0, buffer, 84, 6);
        var json = $$"""
                     {
                       "asset": { "version": "2.0" },
                       "extensionsUsed": ["KHR_texture_transform", "KHR_materials_emissive_strength"],
                       "scene": 0,
                       "scenes": [{ "nodes": [0] }],
                       "nodes": [{ "mesh": 0 }],
                       "meshes": [{
                         "name": "Tri",
                         "primitives": [{
                           "attributes": { "POSITION": 0, "TEXCOORD_0": 1, "TEXCOORD_1": 2 },
                           "indices": 3,
                           "material": 0
                         }]
                       }],
                       "materials": [{{material}}],
                       "images": [{ "uri": "colour.png" }, { "uri": "glow.png" }],
                       "textures": [{ "source": 0 }, { "source": 1 }],
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
        return ModelReader.Read(Encoding.UTF8.GetBytes(json), ".gltf", "Tri", new ModelReadOptions());
    }
}
