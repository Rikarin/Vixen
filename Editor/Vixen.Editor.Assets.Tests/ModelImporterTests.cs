// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Models;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class ModelImporterTests {
    const string Triangle = """
        o Tri
        v 0 0 0
        v 1 0 0
        v 0 1 0
        f 1 2 3
        """;

    [Fact]
    public void ItClaimsTheFormatsDoc08Lists() {
        var importer = new ModelImporter();

        Assert.Equal("ModelImporter", importer.Name);
        Assert.Contains(".fbx", importer.Extensions);
        Assert.Contains(".gltf", importer.Extensions);
        Assert.Contains(".obj", importer.Extensions);
    }

    /// <summary>
    ///     The first importer that produces more than one thing, and the first real consumer of the
    ///     sub-asset addressing <c>BuildPlanner</c> already had.
    /// </summary>
    [Fact]
    public async Task AModelIsTheMainObjectAndItsMeshesAreSubAssets() {
        var (context, result) = await Import("hero.obj", Triangle);

        Assert.True(result.Succeeded);

        var model = Assert.Single(result.Artifacts, artifact => artifact.SubAsset == SubAssetId.Main);
        var mesh = Assert.Single(result.Artifacts, artifact => artifact.SubAsset != SubAssetId.Main);

        Assert.Equal("Model", model.Type);
        Assert.Equal("Mesh", mesh.Type);
        Assert.Equal("Mesh", Assert.Single(result.SubAssets).Type);
        Assert.Empty(context.AssetDependencies);
    }

    /// <summary>
    ///     A sub-asset id is derived from the importer, the kind and the name, so a part's chunk can
    ///     be found by the name the model refers to it by. That is the whole reason the model stores
    ///     names instead of indices.
    /// </summary>
    [Fact]
    public async Task ThePartsTheModelNamesAreTheSubAssetsItDeclared() {
        var (_, result) = await Import("hero.obj", Triangle);

        var model = Serializer.Read<ModelData>(
            Assert.Single(result.Artifacts, artifact => artifact.SubAsset == SubAssetId.Main).Content.Span.ToArray()
        );

        var declared = result.SubAssets.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(model.Parts, part => Assert.Contains(part.Mesh, declared));
    }

    [Fact]
    public async Task TheMeshChunkIsTheGeometryAndNotADescriptionOfIt() {
        var (_, result) = await Import("hero.obj", Triangle);

        var mesh = Serializer.Read<MeshData>(
            Assert.Single(result.Artifacts, artifact => artifact.SubAsset != SubAssetId.Main).Content.Span.ToArray()
        );

        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(1, mesh.TriangleCount);
    }

    [Fact]
    public async Task AFileAssimpWillNotReadFailsThatAssetAndSaysWhy() {
        var (_, result) = await Import("hero.gltf", "{ this is not glTF");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Equal(ImportSeverity.Error, result.Diagnostics[^1].Severity);
    }

    /// <summary>
    ///     A file holding only a camera, a light or an armature imports to nothing drawable. That is
    ///     not a failure — the file parsed — but it is worth saying before something tries to render
    ///     it.
    /// </summary>
    [Fact]
    public async Task AModelWithNoMeshesIsCarriedForwardWithAWarning() {
        var (_, result) = await Import("empty.obj", "this file has no geometry in it\n");

        Assert.True(result.Succeeded);
        Assert.Single(result.Artifacts);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(string name, string text) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();
        files.Seed(path, Encoding.UTF8.GetBytes(text));

        var importer = new ModelImporter();
        var context = new ImportContext(AssetId.New(), path, importer.CreateSettings(), files, importer.Name, "Windows");

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
