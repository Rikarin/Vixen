// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Assets.Terrain;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The four terrain assets, and the heightmap — [docs/plan/31]'s owed importers.</summary>
public sealed class TerrainAssetImporterTests {
    [Fact]
    public void ItClaimsTheFourExtensionsTheToolsetAuthors() {
        var importer = new TerrainAssetImporter();

        Assert.Equal("TerrainAssetImporter", importer.Name);
        Assert.Contains(".vxlayer", importer.Extensions);
        Assert.Contains(".vxfoliage", importer.Extensions);
        Assert.Contains(".vxgrass", importer.Extensions);
        Assert.Contains(".vxspline", importer.Extensions);
    }

    /// <summary>Each extension is recorded under the alias of the type actually written.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this pins down is <c>MaterialImporter</c>'s, recorded in its own
    ///     remarks</b>: a chunk's type string is resolved through the type registry at load, so the
    ///     wrong alias hands the bytes of one record to the reader of another — thrown from inside the
    ///     asset manager about content the build had just declared good.
    /// </remarks>
    [Fact]
    public void EachExtensionNamesItsOwnType() {
        Assert.Equal("TerrainLayerDescription", TerrainAssetImporter.AliasOf(".vxlayer"));
        Assert.Equal("FoliageType", TerrainAssetImporter.AliasOf(".vxfoliage"));
        Assert.Equal("GrassType", TerrainAssetImporter.AliasOf(".vxgrass"));
        Assert.Equal("SplineAsset", TerrainAssetImporter.AliasOf(".vxspline"));
        Assert.Null(TerrainAssetImporter.AliasOf(".vxmat"));
    }

    [Fact]
    public async Task AWellFormedLayerImports() {
        var (_, result) = await Import(
            "gravel.vxlayer",
            """
            name: Gravel
            albedo: Textures/gravel
            tilingMetres: 4
            """
        );

        Assert.True(result.Succeeded);
    }

    /// <summary>A layer naming a texture declares a dependency on it.</summary>
    [Fact]
    public async Task AReferenceBecomesADeclaredDependency() {
        var (context, result) = await Import(
            "gravel.vxlayer",
            """
            name: Gravel
            albedo: vx:9e8a44c9930c64e388ca034c5fe4c426
            """
        );

        Assert.True(result.Succeeded);
        Assert.Single(context.AssetDependencies);
    }

    /// <summary>What the type says about itself reaches the author at import time.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole reason this is not <c>NativeFormatImporter</c> with four more
    ///     extensions.</b> That importer carries a document forward untouched and cannot read it; this
    ///     one runs the type's own <c>Validate()</c>, which turns "the grass never grew" from a bug
    ///     report into a message beside the file that caused it.
    /// </remarks>
    [Fact]
    public async Task AGrassTypeWithNoDensityIsReported() {
        var (_, result) = await Import(
            "meadow.vxgrass",
            """
            name: Meadow
            layer: Grass
            density: 0
            """
        );

        Assert.True(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Message.Contains("candidate density", StringComparison.Ordinal)
        );
    }

    /// <summary>An author part-way through a file is warned, not failed.</summary>
    /// <remarks>
    ///     ⚠ <b>A foliage type with no mesh is a legal state of a file somebody is editing.</b>
    ///     Failing a build over one is how a toolset earns a reputation for getting in the way.
    /// </remarks>
    [Fact]
    public async Task AnIncompleteAssetIsAWarningRatherThanAnError() {
        var (_, result) = await Import("pine.vxfoliage", "name: \"\"\n");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, message => message.Severity == ImportSeverity.Error);
    }

    /// <summary>A spline that cannot be built is an error.</summary>
    [Fact]
    public async Task ASplineWithOnePointFailsTheImport() {
        var (_, result) = await Import(
            "road.vxspline",
            """
            name: Road
            points:
              - position: {x: 0, y: 0, z: 0}
            """
        );

        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Error
                && message.Message.Contains("control point", StringComparison.Ordinal)
        );

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task BrokenYamlIsReportedRatherThanThrown() {
        var (_, result) = await Import("gravel.vxlayer", "name: [unclosed\n");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, message => message.Severity == ImportSeverity.Error);
    }

    // ── The heightmap ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASquareFileGivesUpItsDimensions() {
        Assert.Equal(512, HeightmapImporter.SquareSideOf(512L * 512 * 2));
        Assert.Equal(2049, HeightmapImporter.SquareSideOf(2049L * 2049 * 2));

        // Not a square of 16-bit samples: an odd byte count, and a rectangle.
        Assert.Equal(0, HeightmapImporter.SquareSideOf(1023));
        Assert.Equal(0, HeightmapImporter.SquareSideOf(512L * 256 * 2));
    }

    [Fact]
    public async Task ARawHeightmapImportsAtItsGuessedSize() {
        var (_, result) = await ImportHeightmap("terrain.r16", new byte[64 * 64 * 2]);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AFileThatIsNotASquareAsksForItsDimensions() {
        var (_, result) = await ImportHeightmap("terrain.r16", new byte[100]);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Message.Contains("width and height", StringComparison.Ordinal)
        );
    }

    /// <summary>A 16-bit PNG is refused with the reason rather than imported at eight bits.</summary>
    /// <remarks>
    ///     ⚠ <b>A heightmap quantised to 256 heights does not look like a broken import.</b> It looks
    ///     like a faint terrace on every slope, and it would be attributed to whatever generated the
    ///     file. The decoder this build ships reads every PNG at eight bits a channel, so the
    ///     extension is claimed in order to say so.
    /// </remarks>
    [Fact]
    public async Task APngHeightmapIsRefusedWithTheReason() {
        var (_, result) = await ImportHeightmap("terrain.hmpng", new byte[64]);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Error
                && message.Message.Contains("eight bits", StringComparison.Ordinal)
                && message.Message.Contains(".r16", StringComparison.Ordinal)
        );
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(string name, string text) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();

        files.Seed(path, text);

        var importer = new TerrainAssetImporter();
        var context = new ImportContext(
            AssetId.New(),
            path,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }

    static async Task<(ImportContext Context, ImportResult Result)> ImportHeightmap(string name, byte[] bytes) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();

        files.Seed(path, bytes);

        var importer = new HeightmapImporter();
        var context = new ImportContext(
            AssetId.New(),
            path,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
