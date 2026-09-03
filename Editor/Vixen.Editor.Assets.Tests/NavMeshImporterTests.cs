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
    [Fact]
    public async Task GeometryCanBeSeveralPlacedPieces() {
        const string Document = """
            geometry:
              - floor.obj
              - source: crate.obj
                position: [6, 0, 6]
              - source: crate.obj
                position: [14, 0, 14]
            """;

        var (context, result) = await Import(Document, Floor(20f), extra: [("/Assets/crate.obj", Crate)]);

        Assert.True(result.Succeeded);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;
        var query = new NavMeshQuery(asset.ToNavMesh());
        var tight = new Vector3(0.3f, 1f, 0.3f);

        // The same file placed twice is two obstacles, and each is where it was put — which is the
        // whole thing a placement has to get right, and which cannot be seen from one crate.
        Assert.False(query.FindNearestPoly(new(6, 0, 6), tight, NavQueryFilter.Default, out _, out _), "The first crate is not an obstacle.");
        Assert.False(query.FindNearestPoly(new(14, 0, 14), tight, NavQueryFilter.Default, out _, out _), "The second crate is not an obstacle.");
        Assert.True(query.FindNearestPoly(new(10, 0, 10), Extents, NavQueryFilter.Default, out _, out _), "The floor between them is still floor.");

        // Every piece is a dependency of its own, so re-exporting the crate rebakes the level.
        Assert.Contains(new VirtualPath("/Assets/floor.obj"), context.FileDependencies);
        Assert.Contains(new VirtualPath("/Assets/crate.obj"), context.FileDependencies);
    }

    [Fact]
    public async Task APlacementRotatesAndScalesAsWellAsMoves() {
        // A crate stretched four times along X and turned ninety degrees about Y, so it blocks a
        // stretch of Z rather than of X. Rotation without scale would be invisible on a square.
        const string Document = """
            geometry:
              - floor.obj
              - source: crate.obj
                position: [10, 0, 10]
                rotation: [0, 90, 0]
                scale: [4, 1, 1]
            """;

        var (_, result) = await Import(Document, Floor(20f), extra: [("/Assets/crate.obj", Crate)]);

        Assert.True(result.Succeeded);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;
        var query = new NavMeshQuery(asset.ToNavMesh());
        var tight = new Vector3(0.3f, 1f, 0.3f);

        Assert.False(
            query.FindNearestPoly(new(10, 0, 13), tight, NavQueryFilter.Default, out _, out _),
            "Three metres along Z of the crate's middle is inside it once it has been turned and stretched."
        );

        Assert.True(
            query.FindNearestPoly(new(14, 0, 10), Extents, NavQueryFilter.Default, out _, out _),
            "Three metres along X is outside it, which is where the stretch went before the rotation."
        );
    }

    [Fact]
    public async Task AGeometryEntryWithoutASourceIsRefused() {
        const string Document = """
            geometry:
              - position: [1, 2, 3]
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("`source`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMalformedLinkIsReportedAgainstLinksRatherThanAreas() {
        const string Document = """
            geometry: floor.obj
            links:
              - start: [1, 0]
                end: [5, 0, 5]
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.False(result.Succeeded);

        // It used to say `areas`, which sends the author to the wrong half of the file.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("`links`", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("`areas`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABidirectionalNobodyCanReadIsRefusedRatherThanAssumed() {
        const string Document = """
            geometry: floor.obj
            links:
              - start: [2, 0, 2]
                end: [8, 0, 8]
                bidirectional: maybe
            """;

        var (_, result) = await Import(Document, Floor(20f));

        // `bool.TryParse` accepts one of YAML's several spellings of a boolean and the old code fell
        // back to the default for the rest, so `no` quietly meant `yes`.
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("not a yes or a no", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AOneWayLinkSpeltTheYamlWayIsOneWay() {
        const string Document = """
            geometry: floor.obj
            links:
              - start: [2, 0, 2]
                end: [8, 0, 8]
                bidirectional: no
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.True(result.Succeeded);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;
        var connection = Assert.Single(asset.Tiles[0].OffMeshConnections);

        Assert.False(connection.Bidirectional);
    }

    /// <summary>A key the document does not have is said out loud rather than skipped over.</summary>
    /// <remarks>
    ///     ⚠ <c>area:</c> where <c>areas:</c> was meant is a whole list of authored areas that never
    ///     reaches the bake — and because every list here is optional, the result is a mesh that bakes
    ///     cleanly, reports success, and has no water in it.
    /// </remarks>
    [Fact]
    public async Task AMisspeltRootKeyIsSaidRatherThanDropped() {
        const string Document = """
            geometry: floor.obj
            area:
              - area: 9
                min: [8, -1, 0]
                max: [12, 3, 20]
            """;

        var (_, result) = await Import(Document, Floor(20f));

        // It still bakes: refusing the file outright would make one stray key a broken build.
        Assert.True(result.Succeeded);

        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);

        Assert.Contains("'area'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("areas", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>The sharpest of them, because the fallback is a legitimate number.</summary>
    /// <remarks>
    ///     ⚠ A link's <c>radius</c> falls back to one metre, so a misspelling does not fail to parse —
    ///     it bakes a different ladder. The assertion is both halves: the warning, and the metre that
    ///     is what the warning is about.
    /// </remarks>
    [Fact]
    public async Task AMisspeltLinkFieldIsSaidRatherThanTakingItsFallback() {
        const string Document = """
            geometry: floor.obj
            links:
              - start: [2, 0, 2]
                end: [8, 0, 8]
                radious: 6
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.True(result.Succeeded);

        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
        Assert.Contains("'radious'", warning.Message, StringComparison.Ordinal);

        var asset = Serializer.Read<NavMeshAsset>(Assert.Single(result.Artifacts).Content.Span)!;
        var connection = Assert.Single(asset.Tiles[0].OffMeshConnections);

        Assert.Equal(1f, connection.Radius);
    }

    /// <summary>Every field the importer reads, in one document, warning about none of them.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the half that makes the warning worth having.</b> A spelling check whose list
    ///     of known keys has a typo in it warns about a correct document, and a warning an author
    ///     learns to ignore is worse than the silence it replaced. It goes red if a field is renamed
    ///     without the list following, which is the drift the other two tests cannot see.
    /// </remarks>
    [Fact]
    public async Task ADocumentUsingEveryFieldWarnsAboutNothing() {
        const string Document = """
            geometry:
              - source: floor.obj
                position: [0, 0, 0]
                rotation: [0, 0, 0]
                scale: [1, 1, 1]
            areas:
              - area: 9
                min: [8, -1, 0]
                max: [12, 3, 20]
            links:
              - start: [2, 0, 2]
                end: [8, 0, 8]
                radius: 2
                area: 9
                bidirectional: true
                userId: 42
            """;

        var (_, result) = await Import(Document, Floor(20f));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
    }

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

    /// <summary>A two-by-two crate two metres tall, centred on the origin of its own file.</summary>
    /// <remarks>
    ///     Its own origin is the point: what the placement tests are checking is that the file lands
    ///     where the document says it does, which cannot be seen in a piece that is already in place.
    /// </remarks>
    const string Crate = """
        o Crate
        v -1 0 -1
        v -1 0 1
        v 1 0 1
        v 1 0 -1
        v -1 2 -1
        v -1 2 1
        v 1 2 1
        v 1 2 -1
        f 5 6 7
        f 5 7 8
        f 1 2 3
        f 1 3 4
        f 1 2 6
        f 1 6 5
        f 2 3 7
        f 2 7 6
        f 3 4 8
        f 3 8 7
        f 4 1 5
        f 4 5 8
        """;

    static async Task<(ImportContext Context, ImportResult Result)> Import(
        string document,
        string geometry,
        NavMeshImportSettings? settings = null,
        (string Path, string Content)[]? extra = null
    ) {
        var path = new VirtualPath("/Assets/level.vxnavmesh");
        var files = new MemoryFileProvider();

        files.Seed(path, Encoding.UTF8.GetBytes(document));
        files.Seed(new("/Assets/floor.obj"), Encoding.UTF8.GetBytes(geometry));

        foreach (var (name, content) in extra ?? []) {
            files.Seed(new(name), Encoding.UTF8.GetBytes(content));
        }

        var importer = new NavMeshImporter();
        var context = new ImportContext(AssetId.New(), path, settings ?? new NavMeshImportSettings(), files, importer.Name, "Windows");

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
