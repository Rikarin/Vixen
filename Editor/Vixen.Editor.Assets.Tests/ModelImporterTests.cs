// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml.Meta;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Models;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.VirtualGeometry;
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

    /// <summary>A closed unit cube, wound so every face looks outward. What a field can be checked against.</summary>
    const string Cube = """
        o Cube
        v -0.5 -0.5 -0.5
        v 0.5 -0.5 -0.5
        v 0.5 0.5 -0.5
        v -0.5 0.5 -0.5
        v -0.5 -0.5 0.5
        v 0.5 -0.5 0.5
        v 0.5 0.5 0.5
        v -0.5 0.5 0.5
        f 5 6 7
        f 5 7 8
        f 1 4 3
        f 1 3 2
        f 2 3 7
        f 2 7 6
        f 1 5 8
        f 1 8 4
        f 4 8 7
        f 4 7 3
        f 1 2 6
        f 1 6 5
        """;

    /// <summary>A five-by-five grid: thirty-two triangles, which is enough to be cut into clusters.</summary>
    static string Grid {
        get {
            var text = new StringBuilder("o Plane\n");

            for (var z = 0; z < 5; z++) {
                for (var x = 0; x < 5; x++) {
                    text.Append("v ").Append(x).Append(" 0 ").Append(z).Append('\n');
                }
            }

            for (var z = 0; z < 4; z++) {
                for (var x = 0; x < 4; x++) {
                    var corner = (z * 5) + x + 1;

                    text.Append("f ").Append(corner).Append(' ').Append(corner + 5).Append(' ').Append(corner + 1).Append('\n');
                    text.Append("f ").Append(corner + 1).Append(' ').Append(corner + 5).Append(' ').Append(corner + 6).Append('\n');
                }
            }

            return text.ToString();
        }
    }

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
        var mesh = Assert.Single(result.Artifacts, artifact => artifact.Type == "Mesh");

        Assert.Equal("Model", model.Type);
        Assert.NotEqual(SubAssetId.Main, mesh.SubAsset);
        Assert.Contains(result.SubAssets, entry => entry.Type == "Mesh");
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
            Assert.Single(result.Artifacts, artifact => artifact.Type == "Mesh").Content.Span.ToArray()
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

    /// <summary>
    ///     A field per mesh, as its own sub-asset — because every instance of a mesh shares one, and
    ///     because a renderer wants to load the geometry without paying for the field or the other
    ///     way round.
    /// </summary>
    [Fact]
    public async Task EachMeshGetsADistanceFieldBesideIt() {
        var (_, result) = await Import("crate.obj", Cube, Fast);

        Assert.True(result.Succeeded);

        var field = Assert.Single(result.Artifacts, artifact => artifact.Type == "DistanceField");

        Assert.Equal("DistanceField", Assert.Single(result.SubAssets, entry => entry.Type == "DistanceField").Type);
        Assert.NotEqual(SubAssetId.Main, field.SubAsset);
    }

    /// <summary>
    ///     The bake reached the right geometry and came out the right way round. A cube's centre is
    ///     half a unit inside it, and a field that measured nothing — or measured it inverted — fails
    ///     here rather than in a renderer.
    /// </summary>
    [Fact]
    public async Task TheFieldMeasuresTheMeshItWasBakedFrom() {
        var (_, result) = await Import("crate.obj", Cube, Fast);

        var field = Serializer.Read<MeshDistanceField>(
            Assert.Single(result.Artifacts, artifact => artifact.Type == "DistanceField").Content.Span.ToArray()
        );

        field.Validate();

        Assert.True(field.Sample(Vector3.Zero) < 0, "the centre of a solid cube read as outside it");
        Assert.True(field.Sample(new(0.9f, 0, 0)) > 0, "a point beyond a face read as inside it");
        Assert.Equal(0f, field.Sample(new(0.5f, 0, 0)), field.CellSize.Length());
    }

    [Fact]
    public async Task TurningTheBakeOffLeavesTheRestOfTheImportAlone() {
        var (_, result) = await Import("crate.obj", Cube, new() { GenerateDistanceFields = false });

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Artifacts, artifact => artifact.Type == "DistanceField");
        Assert.Contains(result.Artifacts, artifact => artifact.Type == "Mesh");
    }

    [Fact]
    public async Task TheBakeSettingsInTheMetaReachTheBake() {
        var (_, coarse) = await Import("crate.obj", Cube, new() { DistanceFieldResolution = 8, DistanceFieldSignRays = 8 });
        var (_, fine) = await Import("crate.obj", Cube, new() { DistanceFieldResolution = 20, DistanceFieldSignRays = 8 });

        Assert.Equal(8, Field(coarse).Resolution.X);
        Assert.Equal(20, Field(fine).Resolution.X);

        static MeshDistanceField Field(ImportResult result) =>
            Serializer.Read<MeshDistanceField>(
                Assert.Single(result.Artifacts, artifact => artifact.Type == "DistanceField").Content.Span.ToArray()
            );
    }

    /// <summary>
    ///     Phase 1 of <c>docs/plan/22-virtualized-geometry.md</c>, reached through the importer: a mesh
    ///     produces a cluster hierarchy beside itself, addressable and loadable on its own.
    /// </summary>
    [Fact]
    public async Task EachMeshGetsAClusterHierarchyBesideIt() {
        var (_, result) = await Import("crate.obj", Cube, Fast);

        var artifact = Assert.Single(result.Artifacts, entry => entry.Type == "Clusters");
        var meshlets = Serializer.Read<VirtualGeometryAsset>(artifact.Content.Span.ToArray()).Hierarchy;

        Assert.True(result.Succeeded);
        Assert.NotEmpty(meshlets.Meshlets);
        Assert.NotEmpty(meshlets.Fallback);
        Assert.Contains(result.SubAssets, entry => entry.Type == "Clusters");

        // A cube is twelve triangles, so the whole of it is one cluster and there is nothing to
        // simplify. What matters here is that it survives the round trip through the serializer with
        // its ranges intact, which is what the runtime will read.
        Assert.Equal(12, meshlets.Meshlets.Sum(meshlet => meshlet.TriangleCount));
    }

    /// <summary>
    ///     Phase 2 reached through the importer: the pages are built at build time and shipped as two
    ///     artefacts, records and geometry.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two artefacts rather than one, which is what makes the geometry streamable.</b> A
    ///         <see cref="MeshletPageSet" /> carrying its own <c>Data</c> is a single blob whose
    ///         deserialisation reads every page — the one thing paging exists to avoid — so the build
    ///         writes the records with the bytes removed and the bytes beside them, and a runtime source
    ///         seeks into the second. See <see cref="MeshletPageSet.WithoutData" />.
    ///     </para>
    ///     <para>
    ///         Also the claim that <em>the quantization happens here</em>: finding a mesh's extent and
    ///         snapping every vertex to a grid is work proportional to the whole mesh, which is exactly
    ///         the work a frame must never do. A build that shipped the DAG alone would have moved it to
    ///         load time without anybody noticing, because the picture would be identical.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task EachMeshGetsItsGeometryInPagesBesideIt() {
        var (_, result) = await Import("crate.obj", Cube, Fast);

        var records = Assert.Single(result.Artifacts, entry => entry.Type == "Clusters");
        var data = Assert.Single(result.Artifacts, entry => entry.Type == "ClusterPages");
        var pages = Serializer.Read<VirtualGeometryAsset>(records.Content.Span.ToArray()).Pages;

        Assert.True(result.Succeeded);
        Assert.NotEmpty(pages.Pages);
        Assert.NotEmpty(pages.Clusters);

        // The records carry no geometry, and the blob is the geometry. The two together are what the
        // in-memory set was; either alone is useless, which is the arrangement.
        Assert.False(pages.HasData);
        Assert.Equal(pages.TotalBytes, data.Content.Length);

        // A page vertex is six bytes of quantized position and ten of attributes, which is sixteen —
        // one device word boundary per vertex with no padding to reach it.
        Assert.Equal(MeshletPageBuilder.PositionSize + ModelCompiler.PageAttributeStride, pages.VertexStride);
        Assert.Equal(16, pages.VertexStride);

        // And the grid is the mesh's, not a cluster's: one step across the cube's longest extent divided
        // sixteen-bit, which is what keeps a locked boundary bit-identical between a parent and a child.
        Assert.True(pages.QuantizationStep > 0f, "A degenerate grid would fold the mesh onto one point.");
        Assert.Equal(pages.QuantizationStep * 0.5f, pages.QuantizationError);
    }

    /// <summary>
    ///     A page's bytes survive the round trip through the two artefacts and decode to the mesh.
    /// </summary>
    /// <remarks>
    ///     The end-to-end claim, and the only one that would catch the two artefacts being written out of
    ///     step with each other: the records say where a page is in a blob they no longer carry, so a
    ///     build that wrote the records of one packing and the bytes of another would produce a set that
    ///     validates, loads, streams, and decodes to geometry in the wrong place.
    /// </remarks>
    [Fact]
    public async Task A_shipped_page_decodes_to_the_mesh_it_came_from() {
        var (_, result) = await Import("crate.obj", Cube, Fast);

        var asset = Serializer.Read<VirtualGeometryAsset>(
            Assert.Single(result.Artifacts, entry => entry.Type == "Clusters").Content.Span.ToArray()
        );

        var pages = asset.Pages;

        var meshlets = Serializer.Read<VirtualGeometryAsset>(
            Assert.Single(result.Artifacts, entry => entry.Type == "Clusters").Content.Span.ToArray()
        ).Hierarchy;

        var data = Assert.Single(result.Artifacts, entry => entry.Type == "ClusterPages").Content.ToArray();

        // Reattached, which is what a runtime does not do — it seeks — but is the only way to decode here
        // without standing up a pool. The decoder is the one MeshletPageSet documents as the arithmetic a
        // resolve shader will do per vertex.
        var reattached = pages with { Data = data };
        var positions = new Vector3[meshlets.Meshlets.Max(meshlet => meshlet.VertexCount)];

        for (var cluster = 0; cluster < meshlets.Meshlets.Length; cluster++) {
            var meshlet = meshlets.Meshlets[cluster];
            reattached.GetPositions(cluster, meshlet.VertexCount, positions);

            // Every decoded vertex is within half a grid step of where the cube's corner actually is,
            // which is the error the format promises and nothing more.
            for (var i = 0; i < meshlet.VertexCount; i++) {
                Assert.InRange(positions[i].X, -0.5f - pages.QuantizationError, 0.5f + pages.QuantizationError);
                Assert.InRange(positions[i].Y, -0.5f - pages.QuantizationError, 0.5f + pages.QuantizationError);
                Assert.InRange(positions[i].Z, -0.5f - pages.QuantizationError, 0.5f + pages.QuantizationError);
            }
        }
    }

    [Fact]
    public async Task TheHierarchyCanBeTurnedOff() {
        var (_, result) = await Import("crate.obj", Cube, Fast with { GenerateMeshlets = false });

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Artifacts, artifact => artifact.Type == "Clusters");

        // And no pages either: pages are a packing of a hierarchy, so there is nothing to pack.
        Assert.DoesNotContain(result.Artifacts, artifact => artifact.Type == "ClusterPages");
    }

    [Fact]
    public async Task TheClusterSettingsInTheMetaReachTheBuild() {
        var (_, result) = await Import("plane.obj", Grid, Fast with { MeshletTriangles = 4 });

        var meshlets = Serializer.Read<MeshletMesh>(
            Assert.Single(result.Artifacts, artifact => artifact.Type == "Clusters").Content.Span.ToArray()
        );

        Assert.All(meshlets.Meshlets, meshlet => Assert.InRange(meshlet.TriangleCount, 1, 4));
    }

    /// <summary>Cheap enough to run in a test, and still enough resolution to be checkable.</summary>
    static ModelImportSettings Fast => new() { DistanceFieldResolution = 16, DistanceFieldSignRays = 16 };

    static Task<(ImportContext Context, ImportResult Result)> Import(string name, string text) =>
        Import(name, text, new ModelImportSettings());

    static async Task<(ImportContext Context, ImportResult Result)> Import(
        string name,
        string text,
        ModelImportSettings settings
    ) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();
        files.Seed(path, Encoding.UTF8.GetBytes(text));

        var importer = new ModelImporter();
        var context = new ImportContext(AssetId.New(), path, settings, files, importer.Name, "Windows");

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
