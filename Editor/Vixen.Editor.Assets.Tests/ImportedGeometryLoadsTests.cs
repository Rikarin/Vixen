// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
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
///         <b>The two halves had never been introduced.</b> The importer wrote three artefacts per mesh
///         and the renderer had a loader for three artefacts, and nothing had ever put the output of one
///         into the input of the other — so the names, the serialisation and the pairing were three
///         agreements held in two files by hand.
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
    ///     The whole path in one test: an OBJ goes in, three artefacts come out of the importer under
    ///     the names the loader asks for, and what comes out of the loader is a mesh the traversal has
    ///     registered with its pages reachable. Every step of it was tested; the joins were not.
    /// </remarks>
    [Fact]
    public async Task A_models_artefacts_load_into_a_drawable_mesh() {
        var result = await Import();

        var hierarchy = Artifact(result, VirtualGeometryContent.HierarchyArtifact);
        var pages = Artifact(result, VirtualGeometryContent.PageArtifact);
        var data = Artifact(result, VirtualGeometryContent.PageDataArtifact);

        using var device = new NullDevice();
        using var geometry = new VirtualGeometrySystem(device, slots: 32, pageSize: 128 * 1024);

        var index = geometry.Content(3, hierarchy, pages, new MemoryStream(data.ToArray()));

        Assert.Equal(0, index);
        Assert.Equal(1, geometry.MeshCount);
        Assert.True(geometry.Visibility.PageCount > 0);

        // The blob is as long as the records say the pages are, which is the two artefacts agreeing
        // about one mesh rather than each being individually well-formed.
        var asset = VirtualGeometryContent.Read(hierarchy, pages);

        Assert.Equal(asset.Pages.TotalBytes, data.Length);
    }

    /// <summary>
    ///     The names the loader asks for are the names the importer writes.
    /// </summary>
    /// <remarks>
    ///     Three strings in two assemblies that cannot reference each other. Renaming one is a mesh that
    ///     imports, ships and never loads — and nothing on either side would report it, because each
    ///     half is internally consistent.
    /// </remarks>
    [Fact]
    public async Task The_loader_asks_for_the_names_the_importer_writes() {
        var result = await Import();

        foreach (var name in (string[])[
            VirtualGeometryContent.HierarchyArtifact,
            VirtualGeometryContent.PageArtifact,
            VirtualGeometryContent.PageDataArtifact
        ]) {
            Assert.Contains(result.Artifacts, artifact => artifact.Type == name);
        }
    }

    static ReadOnlySpan<byte> Artifact(ImportResult result, string type) =>
        Assert.Single(result.Artifacts, artifact => artifact.Type == type).Content.Span;

    static async Task<ImportResult> Import() {
        var path = new VirtualPath("/Assets/plane.obj");
        var files = new MemoryFileProvider();
        files.Seed(path, Encoding.UTF8.GetBytes(Plane));

        var importer = new ModelImporter();

        var context = new ImportContext(
            AssetId.New(),
            path,
            new ModelImportSettings { GenerateDistanceFields = false },
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
