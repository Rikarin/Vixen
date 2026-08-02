// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>A tag a game might declare: a bit in the archetype, and no bytes in any column.</summary>
[DataContract("SceneTestSpawnPoint")]
public struct SpawnPoint : ITagComponent;

/// <summary>
///     The build step: an authored <c>.vxscene</c> in, the chunk a player loads out. What these hold
///     is that the two halves agree — a scene compiled here creates the world the file described.
/// </summary>
public sealed class SceneImporterTests {
    public SceneImporterTests() => SceneComponentRegistry.Register<SpawnPoint>();

    /// <summary>A root with a camera and two children, one of them carrying an asset reference.</summary>
    const string Level = """
                         version: 1
                         name: Level1
                         roots:
                           - id: 0123456789abcdef0123456789abcdef
                             name: Root
                             position: 1 2 3
                             children:
                               - id: 11111111111111111111111111111111
                                 name: Camera
                                 position: 0 5 -10
                                 components:
                                   - !Camera
                                     focalLength: 24
                                     nearPlane: 0.05
                                     farPlane: 400
                                     order: 2
                               - id: 22222222222222222222222222222222
                                 name: Crate
                                 scale: 2 2 2
                                 asset: vx:9e8a44c9930c64e388ca034c5fe4c426
                         """;

    [Fact]
    public void ItClaimsTheFormatsItCompiles() {
        var importer = new SceneImporter();

        Assert.Equal("SceneImporter", importer.Name);
        Assert.Contains(".vxscene", importer.Extensions);
        Assert.Contains(".vxprefab", importer.Extensions);
    }

    /// <summary>
    ///     The importer's version is the compiler's, so that a change to the compiled layout
    ///     invalidates every scene artefact rather than only the ones somebody re-saved.
    /// </summary>
    [Fact]
    public void ItsVersionIsTheCompilers() =>
        Assert.Equal(SceneCompiler.Version, new SceneImporter().Version);

    [Fact]
    public async Task ASceneCompilesIntoAChunkThatLoadsAsTheWorldItDescribed() {
        var (_, result) = await Import("Level1.vxscene", Level);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(SceneImporter.SceneType, artifact.Type);
        Assert.Equal(SubAssetId.Main, artifact.SubAsset);

        var asset = Serializer.Read<SceneAsset>(artifact.Content.Span);

        using var world = new World();
        var created = new Entity[3];
        var roots = asset.Instantiate(world, created);

        Assert.Equal("Level1", asset.Name);
        Assert.Equal(3, asset.Content.Count);
        Assert.Single(roots);
        Assert.Equal(new Vector3(1, 2, 3), world.Read<LocalTransform>(created[0]).Position);
        Assert.Equal(new Vector3(2, 2, 2), world.Read<LocalTransform>(created[2]).Scale);
        Assert.Equal(created[0], Hierarchy.ParentOf(world, created[1]));

        // The component came out of the file's `!Camera` block, through the binder, into a column,
        // and back onto an entity — which is the whole path this feature is.
        Assert.Equal(400f, world.Read<Camera>(created[1]).FarPlane);
        Assert.Equal(2, world.Read<Camera>(created[1]).Order);
        Assert.False(world.Has<Camera>(created[2]));
    }

    /// <summary>
    ///     What makes replacing a mesh recompile the level. A scene that did not declare what it
    ///     points at produces a chunk that is correct today and stale for ever.
    /// </summary>
    [Fact]
    public async Task WhatTheSceneNamesBecomesADeclaredDependency() {
        var (context, result) = await Import("Level1.vxscene", Level);

        Assert.True(result.Succeeded);
        Assert.Equal(new(Guid.Parse("9e8a44c9930c64e388ca034c5fe4c426")), Assert.Single(context.AssetDependencies));
    }

    /// <summary>
    ///     A tag is the shape with nothing in the middle: it has a name in the file, a bit in the
    ///     archetype, and no bytes in its column — so it is the case where "write every entity's value
    ///     into the column" has to mean writing nothing at all.
    /// </summary>
    [Fact]
    public async Task ATagIsCarriedAsAPresenceAndCostsNoBytes() {
        var (_, result) = await Import(
            "Level1.vxscene",
            """
            version: 1
            roots:
              - name: Start
                components:
                  - !SceneTestSpawnPoint {}
              - name: Crate
            """
        );

        var asset = Serializer.Read<SceneAsset>(Assert.Single(result.Artifacts).Content.Span);

        var column = asset.Content.Blocks
            .SelectMany(block => block.Columns)
            .Single(column => column.Component == "SceneTestSpawnPoint");

        Assert.Empty(column.Data);

        using var world = new World();
        var created = new Entity[2];
        asset.Instantiate(world, created);

        Assert.True(world.Has<SpawnPoint>(created[0]));
        Assert.False(world.Has<SpawnPoint>(created[1]));
    }

    [Fact]
    public async Task EntitiesOfOneShapeAreCompiledIntoOneBlock() {
        var (_, result) = await Import("Level1.vxscene", Level);
        var asset = Serializer.Read<SceneAsset>(Assert.Single(result.Artifacts).Content.Span);

        // The camera is one archetype and the two bare entities are the other, so a load is two bulk
        // creates rather than three — which is the point of compiling into blocks at all.
        Assert.Equal(2, asset.Content.Blocks.Length);
        Assert.Equal(3, asset.Content.Blocks.Sum(block => block.Entities.Length));
    }

    /// <summary>
    ///     Doc 12 gates the content build on determinism: two builds of one scene, on two operating
    ///     systems, produce the same bytes — which is what lets a content update ship a diff.
    /// </summary>
    [Fact]
    public async Task CompilingTheSameSceneTwiceProducesTheSameBytes() {
        var (_, first) = await Import("Level1.vxscene", Level);
        var (_, second) = await Import("Level1.vxscene", Level);

        Assert.Equal(
            Assert.Single(first.Artifacts).Content.ToArray(),
            Assert.Single(second.Artifacts).Content.ToArray()
        );
    }

    [Fact]
    public async Task APrefabIsCompiledAsOneAndBecomesATemplate() {
        var (_, result) = await Import("Turret.vxprefab", Level);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(SceneImporter.PrefabType, artifact.Type);

        using var world = new World();
        using var prefab = Serializer.Read<PrefabAsset>(artifact.Content.Span).ToPrefab();

        prefab.Instantiate(world);
        prefab.Instantiate(world);

        Assert.Equal(3, prefab.EntityCount);
        Assert.Equal(6, world.EntityCount);
    }

    [Fact]
    public async Task APrefabWithTwoRootsIsRefusedAtBuildTime() {
        var (_, result) = await Import(
            "Turret.vxprefab",
            "version: 1\nroots:\n  - name: One\n  - name: Two\n"
        );

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains("2 roots", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A per-target decision the <c>.meta</c> format's overrides already express: a phone build
    ///     drops what a desktop debug build keeps.
    /// </summary>
    [Fact]
    public async Task NamesAreDroppedWhenTheSettingsSayTo() {
        var (_, result) = await Import("Level1.vxscene", Level, new SceneImportSettings { KeepEntityNames = false });
        var asset = Serializer.Read<SceneAsset>(Assert.Single(result.Artifacts).Content.Span);

        Assert.Empty(asset.Content.Names);
        Assert.Equal(string.Empty, asset.Content.NameOf(0));

        // The ids stay, because a reference into a scene is expressed in them and a build that
        // dropped them would break every prefab override and every scene-placed network object.
        Assert.Equal(3, asset.Content.Ids.Length);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task AComponentNothingRegisteredIsRefusedByName() {
        var (_, result) = await Import(
            "Level1.vxscene",
            "version: 1\nroots:\n  - name: Crate\n    components:\n      - !SceneEntity\n        name: Nested\n"
        );

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains("SceneEntityData", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The file already says where the entity is. A second answer in its components is a merge
    ///     that took both sides, and picking one silently is how a level ends up somewhere else.
    /// </summary>
    [Fact]
    public async Task ALocalTransformAmongTheComponentsIsRefused() {
        var (_, result) = await Import(
            "Level1.vxscene",
            "version: 1\nroots:\n  - name: Crate\n    position: 1 0 0\n    components:\n      - !LocalTransform\n        position: 9 9 9\n"
        );

        Assert.False(result.Succeeded);
        Assert.Contains("LocalTransform", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoComponentsOfOneTypeOnOneEntityAreRefused() {
        var (_, result) = await Import(
            "Level1.vxscene",
            "version: 1\nroots:\n  - name: Crate\n    components:\n      - !Camera\n        order: 1\n      - !Camera\n        order: 2\n"
        );

        Assert.False(result.Succeeded);
        Assert.Contains("two Camera", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     What copying an entity's block by hand produces. An id is what a reference into the scene
    ///     means, so two entities cannot share one.
    /// </summary>
    [Fact]
    public async Task TwoEntitiesWithOneIdAreRefused() {
        var (_, result) = await Import(
            "Level1.vxscene",
            """
            version: 1
            roots:
              - id: 0123456789abcdef0123456789abcdef
                name: Crate
              - id: 0123456789abcdef0123456789abcdef
                name: Copy of Crate
            """
        );

        Assert.False(result.Succeeded);
        Assert.Contains("both have the id", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASceneFromANewerEditorIsRefusedRatherThanHalfCompiled() {
        var (_, result) = await Import("Level1.vxscene", Level.Replace("version: 1", "version: 99", StringComparison.Ordinal));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task AMalformedReferenceFailsBeforeAnythingIsCompiled() {
        var (_, result) = await Import("Level1.vxscene", "version: 1\nroots:\n  - name: Crate\n    asset: vx:notaguid\n");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task YamlThatDoesNotParseIsReportedRatherThanThrown() {
        var (_, result) = await Import("Level1.vxscene", "roots:\n  - name: Crate\n\t tab: here\n");

        Assert.False(result.Succeeded);
        Assert.Equal(ImportSeverity.Error, Assert.Single(result.Diagnostics).Severity);
    }

    [Fact]
    public async Task AnEmptySceneCompilesToAnEmptyAsset() {
        var (_, result) = await Import("Empty.vxscene", "version: 1\nname: Empty\nroots: []\n");
        var asset = Serializer.Read<SceneAsset>(Assert.Single(result.Artifacts).Content.Span);

        using var world = new World();

        Assert.Empty(asset.Instantiate(world));
        Assert.Equal(0, world.EntityCount);
    }

    static async Task<(ImportContext Context, ImportResult Result)> Import(
        string name,
        string text,
        SceneImportSettings? settings = null
    ) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();
        files.Seed(path, text);

        var importer = new SceneImporter();

        var context = new ImportContext(
            AssetId.New(),
            path,
            settings ?? new SceneImportSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return (context, await importer.ImportAsync(context, TestContext.Current.CancellationToken));
    }
}
