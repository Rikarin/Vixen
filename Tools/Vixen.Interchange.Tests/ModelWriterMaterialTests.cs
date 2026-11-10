// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Interchange;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     A material written with its images, read back by Assimp, which nobody here wrote: each slot arrives as the
///     file it was given, so what a texturing tool writes beside its maps is what a DCC binds.
/// </summary>
public sealed class ModelWriterMaterialTests {
    static readonly ModelWriterMaterial Worn = new("Worn", ModelMaterialFactors.Default with { Metallic = 0f }) {
        Textures = new() {
            BaseColor = "maps/worn_albedo.png",
            MetallicRoughness = "maps/worn_orm.png",
            Occlusion = "maps/worn_orm.png",
            Normal = "maps/worn_normal.png",
            Emissive = "maps/worn_emission.png",
            NormalScale = 0.5f,
            AlphaMode = ModelAlphaMode.Blend
        }
    };

    static MeshData Triangle =>
        new() {
            Name = "Shape",
            Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [new(0.1f, 0.2f), new(0.3f, 0.2f), new(0.1f, 0.4f)],
            Indices = [0, 1, 2],
            Bounds = new(new(0f), new(1f)),
            MaterialIndex = 0
        };

    [Fact]
    public void EverySlotComesBackAsTheImageItWasGiven() {
        var json = ModelWriter.Gltf([Triangle], "Shape", [Worn]);

        var read = ModelReader.Read(Encoding.UTF8.GetBytes(json), ".gltf", "Shape", new ModelReadOptions());
        var textures = read.MaterialTextures[read.Model.Parts[0].Material];

        Assert.Equal("Worn", read.Model.Materials[read.Model.Parts[0].Material]);
        Assert.Equal("maps/worn_albedo.png", textures.BaseColor!.Path);
        Assert.Equal("maps/worn_orm.png", textures.MetallicRoughness!.Path);
        Assert.Equal("maps/worn_orm.png", textures.Occlusion!.Path);
        Assert.Equal("maps/worn_normal.png", textures.Normal!.Path);
        Assert.Equal("maps/worn_emission.png", textures.Emissive!.Path);
        Assert.Equal(0.5f, textures.NormalScale);
        Assert.Equal(0f, read.MaterialFactors[0].Metallic);
    }

    /// <summary>An image two slots share is one image and one texture, which is what glTF's ORM packing is for.</summary>
    [Fact]
    public void AMaterialFileHoldsEachImageOnceAndNoMesh() {
        using var document = JsonDocument.Parse(ModelWriter.GltfMaterials([Worn]));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("meshes", out _));
        Assert.False(root.TryGetProperty("buffers", out _));
        Assert.Equal(4, root.GetProperty("images").GetArrayLength());
        Assert.Equal(4, root.GetProperty("textures").GetArrayLength());
        var material = root.GetProperty("materials")[0];
        var orm = material.GetProperty("pbrMetallicRoughness").GetProperty("metallicRoughnessTexture").GetProperty("index");
        Assert.Equal(orm.GetInt32(), material.GetProperty("occlusionTexture").GetProperty("index").GetInt32());
        Assert.Equal("BLEND", material.GetProperty("alphaMode").GetString());
        Assert.Equal(3, material.GetProperty("emissiveFactor").GetArrayLength());
        Assert.False(root.TryGetProperty("extensionsUsed", out _));
    }

    /// <summary>A WebP image has no core slot, so the extension that carries it is required, and so is the strength.</summary>
    [Fact]
    public void AWebPImageAndABrightEmissionNameTheirExtensions() {
        var bright = Worn with {
            Textures = Worn.Textures! with { BaseColor = "maps/worn albedo.webp", EmissiveStrength = 4f }
        };

        using var document = JsonDocument.Parse(ModelWriter.GltfMaterials([bright]));
        var root = document.RootElement;

        Assert.Equal(
            ["EXT_texture_webp", "KHR_materials_emissive_strength"],
            root.GetProperty("extensionsUsed").EnumerateArray().Select(static e => e.GetString()!)
        );
        Assert.Equal("EXT_texture_webp", Assert.Single(root.GetProperty("extensionsRequired").EnumerateArray()).GetString());
        Assert.Equal("maps/worn%20albedo.webp", root.GetProperty("images")[0].GetProperty("uri").GetString());
        Assert.Equal(0, root.GetProperty("textures")[0].GetProperty("extensions").GetProperty("EXT_texture_webp").GetProperty("source").GetInt32());
        Assert.Equal(
            4f,
            root.GetProperty("materials")[0].GetProperty("extensions").GetProperty("KHR_materials_emissive_strength").GetProperty("emissiveStrength").GetSingle()
        );
    }
}
