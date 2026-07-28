// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets.Navigation;
using Vixen.Navigation;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class NavMeshImporterTests {
    static readonly Vector3 Extents = new(2f, 4f, 2f);

    [Fact]
    public void ItClaimsTheNavmeshExtension() {
        var importer = new NavMeshImporter();

        Assert.Equal("NavMeshImporter", importer.Name);
        Assert.Contains(".vxnavmesh", importer.Extensions);
    }

    [Fact]
    public async Task ItBakesTheGeometryItNamesIntoAWalkableMesh() {
        var (context, result) = await Import("geometry: floor.obj\n", Floor(20f));

        Assert.True(result.Succeeded);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("NavMesh", artifact.Type);

        var asset = Serializer.Read<NavMeshAsset>(artifact.Content.Span);
        Assert.NotNull(asset);
        Assert.NotEmpty(asset.Tiles);
        Assert.True(asset.PolyCount > 0);

        var query = new NavMeshQuery(asset.ToNavMesh());
        Assert.True(query.FindNearestPoly(new(10, 0, 10), Extents, NavQueryFilter.Default, out _, out _));

        // And the geometry it read is a dependency, which is what makes a re-export re-bake.
        Assert.Contains(new VirtualPath("/Assets/floor.obj"), context.FileDependencies);
    }

    [Fact]
    public async Task TheGeometryIsADeclaredReadRatherThanASneakedOne() {
        // The framework refuses an undeclared read, so an importer that opened the file without
        // declaring it would fail here rather than produce an artefact that is stale for ever.
        var (_, result) = await Import("geometry: floor.obj\n", Floor(20f));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Error);
    }

    [Fact]
    public async Task AreasInTheDocumentAreStampedOnTheMesh() {
        const string Document = """
            geometry: floor.obj
            areas:
              - area: 9
                min: [8, -1, 0]
                max: [12, 3, 20]
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.True(result.Succeeded);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;
        var mesh = asset.ToNavMesh();
        var query = new NavMeshQuery(mesh);

        Assert.True(query.FindNearestPoly(new(10, 0, 10), Extents, NavQueryFilter.Default, out var inside, out _));
        Assert.True(mesh.TryGetPolyAttributes(inside, out var area, out _));
        Assert.Equal(9, area);

        Assert.True(query.FindNearestPoly(new(3, 0, 10), Extents, NavQueryFilter.Default, out var outside, out _));
        Assert.True(mesh.TryGetPolyAttributes(outside, out var dry, out _));
        Assert.Equal(NavArea.Walkable, dry);
    }

    [Fact]
    public async Task LinksInTheDocumentConnectWhatTheGeometryDoesNot() {
        // Two floors with a four-metre gap, and a link across it.
        const string Split = """
            o West
            v 0 0 0
            v 0 0 10
            v 10 0 10
            v 10 0 0
            f 1 2 3
            f 1 3 4
            o East
            v 14 0 0
            v 14 0 10
            v 24 0 10
            v 24 0 0
            f 5 6 7
            f 5 7 8
            """;

        const string Document = """
            geometry: floor.obj
            links:
              - start: [9, 0, 5]
                end: [15, 0, 5]
                radius: 2
                userId: 7
            """;

        var (_, result) = await Import(Document, Split);

        Assert.True(result.Succeeded);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;
        var connection = Assert.Single(asset.Tiles[0].OffMeshConnections);

        Assert.Equal(7u, connection.UserId);
        Assert.True(connection.Bidirectional);

        var mesh = asset.ToNavMesh();
        var query = new NavMeshQuery(mesh);

        query.FindNearestPoly(new(3, 0, 5), Extents, NavQueryFilter.Default, out var start, out var startPoint);
        query.FindNearestPoly(new(21, 0, 5), Extents, NavQueryFilter.Default, out var end, out var endPoint);

        Span<NavPolyRef> corridor = stackalloc NavPolyRef[256];

        Assert.Equal(
            NavPathStatus.Complete,
            query.FindPath(start, end, startPoint, endPoint, NavQueryFilter.Default, corridor, out _)
        );
    }

    [Fact]
    public async Task ALinkWithNoStartIsAnError() {
        const string Document = """
            geometry: floor.obj
            links:
              - end: [15, 0, 5]
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Error);
    }

    [Fact]
    public async Task TileSizeProducesATiledMesh() {
        var settings = new NavMeshImportSettings { TileSize = 32 };
        var (_, result) = await Import("geometry: floor.obj\n", Floor(40f), settings);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;

        Assert.True(asset.Tiles.Length > 1);
        Assert.Equal(asset.Tiles.Length, asset.ToNavMesh().TileCount);
    }

    [Fact]
    public async Task ADocumentWithNoGeometryFieldIsAnError() {
        var (_, result) = await Import("cellSize: 0.3\n", Floor(20f));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Error);
    }

    [Fact]
    public async Task GeometryThatIsNotThereIsAnErrorNamingIt() {
        var (_, result) = await Import("geometry: missing.obj\n", Floor(20f));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Severity == ImportSeverity.Error && diagnostic.Message.Contains("missing.obj", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AnAreaEntryThatDoesNotParseIsAnError() {
        const string Document = """
            geometry: floor.obj
            areas:
              - area: 9
                min: [8, -1]
                max: [12, 3, 20]
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Error);
    }

    [Fact]
    public async Task GeometryWithNothingWalkableInItIsAWarningRatherThanAnError() {
        // A single wall: vertical, so no part of it is ground.
        const string Wall = """
            o Wall
            v 0 0 0
            v 0 4 0
            v 0 4 4
            v 0 0 4
            f 1 2 3
            f 1 3 4
            """;

        var (_, result) = await Import("geometry: floor.obj\n", Wall);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;
        Assert.Empty(asset.Tiles);
    }

    [Fact]
    public async Task TwoImportsOfTheSameInputProduceTheSameBytes() {
        var (_, first) = await Import("geometry: floor.obj\n", Floor(20f));
        var (_, second) = await Import("geometry: floor.obj\n", Floor(20f));

        Assert.Equal(first.Artifacts[0].Content.ToArray(), second.Artifacts[0].Content.ToArray());
    }

    /// <summary>A flat square floor as an OBJ, wound so its front face points up.</summary>
    static string Floor(float size) {
        var value = size.ToString(CultureInfo.InvariantCulture);

        return $"""
            o Floor
            v 0 0 0
            v 0 0 {value}
            v {value} 0 {value}
            v {value} 0 0
            f 1 2 3
            f 1 3 4
            """;
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(
        string document,
        string geometry,
        NavMeshImportSettings? settings = null
    ) {
        var path = new VirtualPath("/Assets/level.vxnavmesh");
        var files = new MemoryFileProvider();

        files.Seed(path, Encoding.UTF8.GetBytes(document));
        files.Seed(new("/Assets/floor.obj"), Encoding.UTF8.GetBytes(geometry));

        var importer = new NavMeshImporter();
        var context = new ImportContext(AssetId.New(), path, settings ?? new NavMeshImportSettings(), files, importer.Name, "Windows");

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
