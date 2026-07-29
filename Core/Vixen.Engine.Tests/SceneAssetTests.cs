// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>A component a game might declare, so the tests exercise the path a game's types take.</summary>
[DataContract("SceneTestHealth")]
public struct Health {
    /// <summary>How much is left.</summary>
    public int Value;

    /// <summary>How fast it comes back.</summary>
    public float Regeneration;
}

/// <summary>A tag a game might declare, which has a bit in the archetype and no bytes anywhere.</summary>
[DataContract("SceneTestHostile")]
public struct Hostile : ITagComponent;

public sealed class SceneAssetTests {
    public SceneAssetTests() {
        SceneComponentRegistry.Register<Health>();
        SceneComponentRegistry.Register<Hostile>();
    }

    // ---------------------------------------------------------------- the format

    [Fact]
    public void ACompiledSceneSurvivesTheSerializer() {
        var asset = new SceneAsset { Name = "level", Content = Three() };

        var read = Serializer.Read<SceneAsset>(Serializer.ToBytes(asset));

        Assert.Equal("level", read.Name);
        Assert.Equal(3, read.Content.Count);
        Assert.Equal(asset.Content.Parents, read.Content.Parents);
        Assert.Equal(asset.Content.Positions, read.Content.Positions);
        Assert.Equal(asset.Content.Names, read.Content.Names);
        Assert.Equal(asset.Content.Ids, read.Content.Ids);
    }

    /// <summary>
    ///     The determinism the content build is gated on, at the one layer these tests can see it:
    ///     the same content compiled twice is the same bytes, so an unchanged scene produces an
    ///     unchanged chunk and a content update ships nothing for it.
    /// </summary>
    [Fact]
    public void TheSameSceneCompilesToTheSameBytes() {
        var first = Serializer.ToBytes(new SceneAsset { Name = "level", Content = Three() });
        var second = Serializer.ToBytes(new SceneAsset { Name = "level", Content = Three() });

        Assert.Equal(first, second);
    }

    // ---------------------------------------------------------------- loading

    [Fact]
    public void LoadingMakesTheHierarchyTheSceneWasCompiledWith() {
        using var world = new World();
        var asset = new SceneAsset { Content = Three() };
        var created = new Entity[3];

        var roots = asset.Instantiate(world, created);

        Assert.Single(roots);
        Assert.Equal(created[0], roots[0]);
        Assert.Equal(created[0], Hierarchy.ParentOf(world, created[1]));
        Assert.Equal(created[0], Hierarchy.ParentOf(world, created[2]));
        Assert.Equal(3, world.EntityCount);
    }

    /// <summary>
    ///     ⚠ The failure the reverse loop in <c>SceneContent.Instantiate</c> exists to prevent.
    ///     Linking forwards prepends each child, so the world would hold them in the opposite order
    ///     from the file — which nothing looks wrong about until draw order or a script's walk over
    ///     its children depends on it.
    /// </summary>
    [Fact]
    public void SiblingsComeBackInTheOrderTheSceneHoldsThem() {
        using var world = new World();
        var created = new Entity[3];

        new SceneAsset { Content = Three() }.Instantiate(world, created);

        Assert.Equal([created[1], created[2]], Children(world, created[0]));
    }

    [Fact]
    public void TransformsComeBackAsTheyWereCompiled() {
        using var world = new World();
        var created = new Entity[3];

        new SceneAsset { Content = Three() }.Instantiate(world, created);

        Assert.Equal(new Vector3(1, 2, 3), world.Read<LocalTransform>(created[1]).Position);
        Assert.Equal(new Vector3(2, 2, 2), world.Read<LocalTransform>(created[2]).Scale);
    }

    /// <summary>A zeroed rotation and scale are what an entity written by hand looks like, and both
    /// collapse the entity if they are taken literally.</summary>
    [Fact]
    public void AZeroRotationAndScaleAreReadAsTheIdentity() {
        using var world = new World();
        var content = Three();
        content.Rotations[0] = default;
        content.Scales[0] = default;
        var created = new Entity[3];

        new SceneAsset { Content = content }.Instantiate(world, created);

        Assert.Equal(Quaternion.Identity, world.Read<LocalTransform>(created[0]).Rotation);
        Assert.Equal(Vector3.One, world.Read<LocalTransform>(created[0]).Scale);
    }

    [Fact]
    public void ComponentsComeBackOnTheEntitiesThatCarriedThem() {
        using var world = new World();
        var created = new Entity[3];

        new SceneAsset { Content = Three() }.Instantiate(world, created);

        Assert.False(world.Has<Health>(created[0]));
        Assert.Equal(70, world.Read<Health>(created[1]).Value);
        Assert.Equal(0.5f, world.Read<Health>(created[1]).Regeneration);
        Assert.Equal(12, world.Read<Health>(created[2]).Value);
        Assert.True(world.Has<Hostile>(created[2]));
        Assert.False(world.Has<Hostile>(created[1]));
        Assert.Equal(60f, world.Read<Camera>(created[0]).FieldOfView);
    }

    [Fact]
    public void EntitiesOfOneArchetypeAreCreatedTogether() {
        var content = Three();

        // Three entities, three shapes: a camera, a health, and a health with a tag. The block count
        // is the number of bulk creates a load costs, and every entity is in exactly one of them.
        Assert.Equal(3, content.Blocks.Length);
        Assert.Equal(3, content.Blocks.Sum(block => block.Entities.Length));
    }

    [Fact]
    public void ATagCostsNoBytesAndStillArrives() {
        var content = Three();
        var column = content.Blocks
            .SelectMany(block => block.Columns)
            .Single(column => column.Component == "SceneTestHostile");

        Assert.Empty(column.Data);
    }

    // ---------------------------------------------------------------- scenes

    [Fact]
    public void LoadingThroughTheManagerTagsEveryEntityAndUnloadsThemAgain() {
        using var world = new World();
        var scenes = new SceneManager(world);

        var scene = new SceneAsset { Name = "level", Content = Three() }.Load(scenes);

        Assert.Equal("level", scenes.NameOf(scene));
        Assert.Equal(3, scenes.CountIn(scene));
        Assert.Equal(3, scenes.Unload(scene));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void TwoScenesLoadIntoOneWorldWithoutSeeingEachOther() {
        using var world = new World();
        var scenes = new SceneManager(world);
        var asset = new SceneAsset { Name = "level", Content = Three() };

        var first = asset.Load(scenes);
        var second = asset.Load(scenes);

        Assert.NotEqual(first, second);
        Assert.Equal(6, world.EntityCount);
        Assert.Equal(3, scenes.Unload(first));
        Assert.Equal(3, scenes.CountIn(second));
    }

    // ---------------------------------------------------------------- prefabs

    [Fact]
    public void APrefabAssetBecomesATemplateThatStampsOutInstances() {
        using var world = new World();
        using var prefab = new PrefabAsset { Name = "turret", Content = Three() }.ToPrefab();

        Assert.Equal(3, prefab.EntityCount);

        var first = prefab.Instantiate(world, LocalTransform.At(new(5, 0, 0)));
        prefab.Instantiate(world);

        Assert.Equal(6, world.EntityCount);
        Assert.Equal(new Vector3(5, 0, 0), world.Read<LocalTransform>(first).Position);
        Assert.Equal(2, Children(world, first).Count);
    }

    [Fact]
    public void APrefabInstanceCarriesTheComponentsTheAssetCompiled() {
        using var world = new World();

        var root = new PrefabAsset { Name = "turret", Content = Three() }.Instantiate(world);
        var children = Children(world, root);

        Assert.Equal(60f, world.Read<Camera>(root).FieldOfView);
        Assert.Equal(70, world.Read<Health>(children[0]).Value);
    }

    [Fact]
    public void APrefabWithTwoRootsIsRefusedAsBeingAScene() {
        var content = Three();
        content.Parents[1] = -1;

        var failure = Assert.Throws<InvalidOperationException>(
            () => new PrefabAsset { Name = "turret", Content = content }.ToPrefab()
        );

        Assert.Contains("2 roots", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- capture

    [Fact]
    public void AWorldCapturedAndInstantiatedIsTheWorldItWas() {
        using var source = new World();
        var root = Hierarchy.CreateTransform(source, LocalTransform.At(new(1, 0, 0)));
        source.Add(root, Camera.Perspective);
        var child = Hierarchy.CreateTransform(source, LocalTransform.At(new(0, 2, 0)));
        source.Add(child, new Health { Value = 5 });
        Hierarchy.SetParent(source, child, root);

        var content = SceneContent.Capture(source, [root]);

        using var target = new World();
        var created = new Entity[2];
        content.Instantiate(target, created);

        Assert.Equal(2, content.Count);
        Assert.Equal(new Vector3(1, 0, 0), target.Read<LocalTransform>(created[0]).Position);
        Assert.Equal(5, target.Read<Health>(created[1]).Value);
        Assert.Equal(created[0], Hierarchy.ParentOf(target, created[1]));
    }

    [Fact]
    public void CaptureKeepsTheNamesAndIdsItIsGivenAndNothingMore() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        var id = Guid.NewGuid();

        var named = SceneContent.Capture(world, [root], new Dictionary<Entity, string> { [root] = "Player" });
        var bare = SceneContent.Capture(world, [root]);
        var identified = SceneContent.Capture(world, [root], null, new Dictionary<Entity, Guid> { [root] = id });

        Assert.Equal("Player", named.NameOf(0));
        Assert.Empty(bare.Names);
        Assert.Equal(string.Empty, bare.NameOf(0));
        Assert.Equal(id, identified.Ids[0]);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public void ASceneFromANewerBuildIsRefusedRatherThanPartlyLoaded() {
        using var world = new World();
        var asset = new SceneAsset { Name = "level", Version = SceneAsset.Current + 1, Content = Three() };

        var failure = Assert.Throws<NotSupportedException>(() => asset.Instantiate(world));

        Assert.Contains("level", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void AComponentThisBuildDoesNotHaveIsRefusedByName() {
        using var world = new World();
        var content = Three();
        content.Blocks[0].Columns = [new() { Component = "SomethingFromAPlugin", Data = [] }];

        var failure = Assert.Throws<SceneComponentException>(
            () => new SceneAsset { Content = content }.Instantiate(world)
        );

        Assert.Contains("SomethingFromAPlugin", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntityInNoBlockIsRefusedBeforeAnythingIsCreated() {
        using var world = new World();
        var content = Three();
        content.Blocks = [content.Blocks[0]];

        Assert.Throws<ArgumentException>(() => new SceneAsset { Content = content }.Instantiate(world));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void AParentThatDoesNotComeFirstIsRefused() {
        using var world = new World();
        var content = Three();
        content.Parents[0] = 2;

        Assert.Throws<ArgumentException>(() => new SceneAsset { Content = content }.Instantiate(world));
    }

    [Fact]
    public void ATruncatedAssetIsRefusedRatherThanIndexedPast() {
        using var world = new World();
        var content = Three();
        content.Positions = [content.Positions[0]];

        Assert.Throws<ArgumentException>(() => new SceneAsset { Content = content }.Instantiate(world));
    }

    [Fact]
    public void AskingWhichEntityEachNodeBecameNeedsTheRightLength() {
        using var world = new World();

        Assert.Throws<ArgumentException>(
            () => new SceneAsset { Content = Three() }.Instantiate(world, new Entity[2])
        );
    }

    static List<Entity> Children(World world, Entity entity) {
        var children = new List<Entity>();

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        return children;
    }

    /// <summary>
    ///     A root with a camera, and two children with health — one of them hostile. Three entities
    ///     in three archetypes, which is what makes it worth compiling into blocks at all.
    /// </summary>
    static SceneContent Three() {
        using var world = new World();

        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        world.Add(root, Camera.Perspective with { FieldOfView = 60f });

        var first = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 2, 3)));
        world.Add(first, new Health { Value = 70, Regeneration = 0.5f });

        var second = Hierarchy.CreateTransform(
            world,
            LocalTransform.Identity with { Scale = new(2, 2, 2) }
        );

        world.Add(second, new Health { Value = 12 });
        world.Add<Hostile>(second);

        // Linked back to front, so that the children come out in the order they were made — the same
        // reason the load links backwards.
        Hierarchy.SetParent(world, second, root);
        Hierarchy.SetParent(world, first, root);

        return SceneContent.Capture(
            world,
            [root],
            new Dictionary<Entity, string> { [root] = "Root", [first] = "First", [second] = "Second" },
            new Dictionary<Entity, Guid> { [root] = new("11111111111111111111111111111111") }
        );
    }
}
