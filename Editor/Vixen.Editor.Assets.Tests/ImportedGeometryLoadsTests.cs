// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets.Models;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     What an import writes is what the runtime loads.
/// </summary>
/// <remarks>
///     <para>
///         <b>The two halves had never been introduced.</b> The importer wrote artefacts and the renderer
///         had a loader for them, and nothing had ever put the output of one into the input of the other
///         — so the names, the serialisation and the pairing were agreements held in two files by hand.
///         Worse, the artefacts were not addressable at all: three chunks shared one sub-asset id, which
///         a content build refuses, so nothing shipped could ever have loaded one.
///     </para>
///     <para>
///         <b>Here rather than in the renderer's own tests, because here is where both sides exist.</b>
///         <c>Vixen.Rendering</c> does not reference the importer and should not: an editor assembly is
///         not something a game links. That is exactly why the names are spelled twice, and exactly why
///         the test that they still match has to live on the side that can see both.
///     </para>
/// </remarks>
public sealed class ImportedGeometryLoadsTests {
    /// <summary>A tessellated plane, big enough to page into more than one page.</summary>
    static readonly string Plane = Grid(24);

    /// <summary>
    ///     A model's artefacts load into a registered, streamable mesh.
    /// </summary>
    /// <remarks>
    ///     The whole path in one test: an OBJ goes in, two chunks come out of the importer under the
    ///     names the loader asks for, and what comes out of the loader is a mesh the traversal has
    ///     registered with its pages reachable. Every step of it was tested; the joins were not.
    /// </remarks>
    [Fact]
    public async Task A_models_artefacts_load_into_a_drawable_mesh() {
        var result = await Import();

        var records = Artifact(result, VirtualGeometryContent.ClusterArtifact);
        var data = Artifact(result, VirtualGeometryContent.ClusterPageArtifact);

        using var device = new NullDevice();
        using var geometry = new VirtualGeometrySystem(device, slots: 32, pageSize: 128 * 1024);

        var asset = Serializer.Read<VirtualGeometryAsset>(records.ToArray());
        var index = geometry.Content(3, asset, new MemoryStream(data.ToArray()));

        Assert.Equal(0, index);
        Assert.Equal(1, geometry.MeshCount);
        Assert.True(geometry.Visibility.PageCount > 0);

        // The blob is as long as the records say the pages are, which is the two chunks agreeing about
        // one mesh rather than each being individually well-formed.
        Assert.Equal(asset.Pages.TotalBytes, data.Length);
    }

    /// <summary>
    ///     Every artefact has a sub-asset of its own, so every one of them has an address.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The defect this was written for shipped for as long as the feature existed.</b> The
    ///         importer wrote three artefacts under one sub-asset id, and an address names exactly one
    ///         chunk — so <c>BuildPlanner</c> refuses the model outright with "imported to two chunks for
    ///         sub-asset X". Every test of the virtualized path read the artefacts straight out of the
    ///         <c>ImportResult</c>, which is the one place the collision does not show.
    ///     </para>
    ///     <para>
    ///         The names matter as much as the ids: a sub-asset's address is built from its name alone,
    ///         so a hierarchy called what its mesh is called collides with the mesh in the other
    ///         direction — the "a mesh and a material both called Body" case.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Each_artefact_is_its_own_addressable_sub_asset() {
        var result = await Import();

        var ids = result.Artifacts.Select(artifact => artifact.SubAsset).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());

        // And each is declared, because a chunk whose sub-asset the .meta does not name is one nothing
        // can address — the planner's other refusal.
        foreach (var artifact in result.Artifacts.Where(artifact => !artifact.SubAsset.IsMain)) {
            Assert.Contains(result.SubAssets, declared => declared.Id == artifact.SubAsset);
        }

        var names = result.SubAssets.Select(declared => declared.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(ModelImporter.ClusterName("Plane"), names);
        Assert.Contains(ModelImporter.PageName("Plane"), names);
    }

    /// <summary>The mesh points at its own clusters, because nothing else could find them.</summary>
    /// <remarks>
    ///     A sub-asset id is derived from the importer's name, the kind and the mesh's name, and a frame
    ///     holding a mesh reference knows none of the three. Without the link on the mesh there is no
    ///     route from "this entity draws that mesh" to "and here is its hierarchy" at all.
    /// </remarks>
    [Fact]
    public async Task A_clustered_mesh_carries_the_reference_to_its_clusters() {
        var result = await Import();
        var mesh = Serializer.Read<MeshData>(Artifact(result, ModelImporter.MeshType).ToArray());

        Assert.True(mesh.IsClustered);

        var records = Assert.Single(
            result.Artifacts,
            artifact => artifact.Type == VirtualGeometryContent.ClusterArtifact
        );

        var blob = Assert.Single(
            result.Artifacts,
            artifact => artifact.Type == VirtualGeometryContent.ClusterPageArtifact
        );

        Assert.Equal(records.SubAsset, mesh.Clusters.SubAsset);
        Assert.Equal(blob.SubAsset, mesh.ClusterPages.SubAsset);
    }

    /// <summary>A mesh imported without clusters says so, rather than pointing at nothing.</summary>
    [Fact]
    public async Task An_unclustered_mesh_carries_no_reference() {
        var result = await Import(clusters: false);
        var mesh = Serializer.Read<MeshData>(Artifact(result, ModelImporter.MeshType).ToArray());

        Assert.False(mesh.IsClustered);
        Assert.DoesNotContain(result.Artifacts, artifact => artifact.Type == VirtualGeometryContent.ClusterArtifact);
    }

    static ReadOnlySpan<byte> Artifact(ImportResult result, string type) =>
        Assert.Single(result.Artifacts, artifact => artifact.Type == type).Content.Span;

    static async Task<ImportResult> Import(bool clusters = true) {
        var path = new VirtualPath("/Assets/plane.obj");
        var files = new MemoryFileProvider();
        files.Seed(path, Encoding.UTF8.GetBytes(Plane));

        var importer = new ModelImporter();

        var context = new ImportContext(
            AssetId.New(),
            path,
            new ModelImportSettings { GenerateDistanceFields = false, GenerateMeshlets = clusters },
            files,
            importer.Name,
            "Windows"
        );

        var result = await importer.ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        return result;
    }

    /// <summary>A tessellated quad as Wavefront OBJ.</summary>
    static string Grid(int segments) {
        var text = new StringBuilder("o Plane\n");

        for (var y = 0; y <= segments; y++) {
            for (var x = 0; x <= segments; x++) {
                text.Append(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"v {(float)x / segments} 0 {(float)y / segments}\n"
                );
            }
        }

        for (var y = 0; y < segments; y++) {
            for (var x = 0; x < segments; x++) {
                var a = (y * (segments + 1)) + x + 1;
                var b = a + 1;
                var c = a + segments + 1;
                var d = c + 1;

                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"f {a} {c} {b}\n");
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"f {b} {c} {d}\n");
            }
        }

        return text.ToString();
    }
}
