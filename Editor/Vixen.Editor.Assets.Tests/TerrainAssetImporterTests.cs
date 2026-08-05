// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets.Terrain;
using Vixen.Foliage;
using Vixen.Terrain;
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
        Assert.Contains(".vxfol", importer.Extensions);
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
        Assert.Equal("FoliageVolume", TerrainAssetImporter.AliasOf(".vxfol"));
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

    /// <summary>A grass document is compiled to the record the runtime opens.</summary>
    /// <remarks>
    ///     ⚠ <b>The other three ship as text; this one cannot.</b> <c>AssetTerrainSource</c> hands
    ///     the chunk's payload to the binary serializer — a game does not carry the YAML dialect —
    ///     so a text chunk here is a field that quietly never grows. Asserted by reading the
    ///     artefact back the way the runtime does.
    /// </remarks>
    [Fact]
    public async Task AGrassChunkIsTheSerializedRecord() {
        var (_, result) = await Import(
            "meadow.vxgrass",
            """
            name: Meadow
            layer: Grass
            density: 24
            """
        );

        Assert.True(result.Succeeded);

        var artefact = Assert.Single(result.Artifacts);
        var written = Serializer.Read<GrassType>(artefact.Content.Span);

        Assert.Equal("GrassType", artefact.Type);
        Assert.Equal("Meadow", written.Name);
        Assert.Equal("Grass", written.Layer);
        Assert.Equal(24f, written.Density);
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

    /// <summary>A foliage document is compiled to the record the runtime opens.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap the grass chunk closed, one asset kind over.</b> These shipped as YAML text
    ///     until the runtime path existed, and <c>AssetTerrainSource</c> hands the payload to the
    ///     binary serializer — so a text chunk is a forest that quietly never stands. Asserted by
    ///     reading the artefact back the way the runtime does.
    /// </remarks>
    [Fact]
    public async Task AFoliageChunkIsTheSerializedRecord() {
        var (_, result) = await Import(
            "pine.vxfoliage",
            """
            name: Pine
            mesh: vx:9e8a44c9930c64e388ca034c5fe4c426
            radius: 3
            castShadows: true
            """
        );

        Assert.True(result.Succeeded);

        var artefact = Assert.Single(result.Artifacts);
        var written = Serializer.Read<FoliageType>(artefact.Content.Span);

        Assert.Equal("FoliageType", artefact.Type);
        Assert.Equal("Pine", written.Name);
        Assert.Equal(3f, written.Radius);
        Assert.True(written.CastShadows);
    }

    /// <summary>A volume's instances are carried forward as the store's own bytes.</summary>
    [Fact]
    public async Task AVolumeChunkIsTheStoresOwnBytes() {
        var volume = new FoliageVolume(new(32f));
        var type = volume.AddType(FoliageType.Of("Pine") with { Radius = 2f });

        volume.Add(type, new(new(8f, 0f, 8f), Vixen.Core.Mathematics.Quaternion.Identity, 1f));

        var bytes = new byte[FoliageStore.ByteCount(volume)];

        FoliageStore.Write(volume, bytes);

        var (_, result) = await ImportBytes("meadow.vxfol", bytes);

        Assert.True(result.Succeeded);

        var artefact = Assert.Single(result.Artifacts);

        Assert.Equal("FoliageVolume", artefact.Type);
        Assert.True(artefact.Content.Span.SequenceEqual(bytes));

        // And read back the way the runtime reads it: same instance, same cell.
        var reread = new FoliageVolume(new(32f));

        reread.AddType(FoliageType.Of("Pine") with { Radius = 2f });

        Assert.Equal(1, FoliageStore.Read(reread, artefact.Content.Span));
    }

    /// <summary>Bytes that are not a foliage file are an error naming the reason, not instances.</summary>
    [Fact]
    public async Task AVolumeWithoutTheMagicIsRefused() {
        var (_, result) = await ImportBytes("meadow.vxfol", [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Error
                && message.Message.Contains("magic", StringComparison.Ordinal)
        );
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

    /// <summary>A spline document is compiled to the record <c>AssetWaterSource</c> opens.</summary>
    /// <remarks>
    ///     ⚠ <b>This shipped as a text chunk, and the importer's own remarks said it would "stay text
    ///     until a runtime consumer exists".</b> One does: docs/plan/35 § D6 makes a water body a
    ///     spline reference, so a <c>.vxspline</c> is now read by a game and a game does not carry the
    ///     YAML dialect. A text chunk here is a lake that quietly never appears — the failure this
    ///     asserts against by reading the artefact back the way the runtime does.
    /// </remarks>
    [Fact]
    public async Task ASplineChunkIsTheSerializedRecord() {
        var (_, result) = await Import(
            "river.vxspline",
            """
            name: River
            isClosed: false
            points:
              - position: 0 1 0
              - position: 10 1 4
              - position: 20 1 0
            """
        );

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(entry => entry.Message)));

        var artefact = Assert.Single(result.Artifacts);
        var written = Serializer.Read<Vixen.Core.Mathematics.SplineAsset>(artefact.Content.Span);

        Assert.Equal("SplineAsset", artefact.Type);
        Assert.Equal("River", written.Name);
        Assert.Equal(3, written.Count);
        Assert.True(written.CanBuild);
        Assert.Equal(20f, written[2].Position.X);
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
    public async Task ASixteenBitPngHeightmapCarriesItsOwnSize() {
        var samples = new ushort[16 * 12];

        for (var index = 0; index < samples.Length; index++) {
            samples[index] = (ushort)(index * 401 % 65536);
        }

        // ⚠ Settings that say something else, deliberately. A PNG carries its size and its bit depth,
        // which is the whole reason to prefer it over raw — a person who filled the form in for a
        // `.r16` and then imported a `.hmpng` must not get whichever answer the form happened to hold.
        var (_, result) = await ImportHeightmap(
            "terrain.hmpng",
            TerrainHeightmapPng.Encode(16, 12, samples),
            new HeightmapImportSettings { Width = 999, Height = 999 }
        );

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, message => message.Severity == ImportSeverity.Error);
    }

    /// <summary>And an eight-bit one is still refused, with the reason.</summary>
    /// <remarks>
    ///     ⚠ <b>An eight-bit import is a terrain quantised to 256 heights</b>, which reads as a faint
    ///     terrace on every slope and gets attributed to whatever generated it rather than to the
    ///     import.
    /// </remarks>
    [Fact]
    public async Task AnEightBitPngHeightmapIsRefusedWithTheReason() {
        var file = TerrainHeightmapPng.Encode(4, 4, new ushort[16]);

        // The bit depth is the ninth byte of the IHDR body.
        file[16 + 8] = 8;

        var (_, result) = await ImportHeightmap("terrain.hmpng", file);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            message => message.Severity == ImportSeverity.Error
                && message.Message.Contains("sixteen", StringComparison.Ordinal)
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

    /// <summary>Imports a binary source — the volume path, which never sees a StreamReader.</summary>
    static async Task<(ImportContext Context, ImportResult Result)> ImportBytes(string name, byte[] bytes) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();

        files.Seed(path, bytes);

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

    static async Task<(ImportContext Context, ImportResult Result)> ImportHeightmap(
        string name,
        byte[] bytes,
        HeightmapImportSettings? settings = null
    ) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();

        files.Seed(path, bytes);

        var importer = new HeightmapImporter();
        var context = new ImportContext(
            AssetId.New(),
            path,
            settings ?? importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
