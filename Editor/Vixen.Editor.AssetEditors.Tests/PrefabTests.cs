// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Prefabs;
using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>Placing a prefab in a scene, and knowing afterwards where each entity came from.</summary>
public class PrefabInstanceTests {
    /// <summary>An instance's entities are recorded against the template entity they came from.</summary>
    [Fact]
    public void InstantiatingRecordsWhereEachEntityCameFrom() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var prefab = Turret();

        var root = Prefab.Instantiate(scene, AssetId.New(), prefab);

        Assert.Equal(2, scene.Prefabs.Count);
        Assert.True(scene.Prefabs.TryGet(root, out var link));
        Assert.Equal(prefab.Roots[0].Id, link.Source);
    }

    /// <summary>⚠ Two instances of one prefab do not share the file's identities.</summary>
    [Fact]
    public void TwoInstancesDoNotShareIdentities() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var asset = AssetId.New();

        var first = Prefab.Instantiate(scene, asset, Turret());
        var second = Prefab.Instantiate(scene, asset, Turret());

        Assert.NotEqual(scene.IdOf(first), scene.IdOf(second));
    }

    /// <summary>⚠ A prefab with two roots is refused, because the build refuses it too.</summary>
    [Fact]
    public void APrefabHasOneRoot() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");

        var two = Turret();
        two.Roots.Add(new() { Id = EntityId.New(), Name = "Second" });

        Assert.Throws<ArgumentException>(
            () => Prefab.Instantiate(scene, AssetId.New(), two)
        );
    }

    /// <summary>The hierarchy comes across with the instance.</summary>
    [Fact]
    public void ChildrenComeAcross() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");

        var root = Prefab.Instantiate(scene, AssetId.New(), Turret());

        var children = 0;

        foreach (var _ in Hierarchy.ChildrenOf(world, root)) {
            children++;
        }

        Assert.Equal(1, children);
    }

    /// <summary>One entity of an instance can be unpacked without breaking the rest.</summary>
    [Fact]
    public void OneEntityCanBeUnpacked() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var root = Prefab.Instantiate(scene, AssetId.New(), Turret());

        Assert.True(scene.Prefabs.Forget(root));
        Assert.False(scene.Prefabs.TryGet(root, out _));
        Assert.Equal(1, scene.Prefabs.Count);
    }

    /// <summary>Pruning forgets links whose entity has gone.</summary>
    [Fact]
    public void PruningForgetsDeadEntities() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var root = Prefab.Instantiate(scene, AssetId.New(), Turret());
        scene.Delete([root]);

        // ⚠ Zero, and that is the change rather than a regression: `SceneDocument.Delete` calls
        // `PruneNames`, which now prunes the links with the names. The links a delete leaves behind
        // are the ones that would be inherited by whatever takes those slots.
        Assert.Equal(0, scene.Prefabs.Prune(world));
        Assert.Equal(0, scene.Prefabs.Count);
    }

    /// <summary>A template entity is found again by the id the file gave it.</summary>
    [Fact]
    public void ATemplateIsFoundById() {
        var prefab = Turret();
        var child = prefab.Roots[0].Children[0];

        Assert.True(Prefab.TryFind(prefab, child.Id, out var found));
        Assert.Equal("Barrel", found.Name);
    }

    /// <summary>An id the prefab does not have finds nothing.</summary>
    [Fact]
    public void AnUnknownIdFindsNothing() =>
        Assert.False(Prefab.TryFind(Turret(), EntityId.New(), out _));

    static SceneFile Turret() => new() {
        Name = "Turret",
        Roots = [
            new() {
                Id = EntityId.New(),
                Name = "Turret",
                Position = new(1f, 0f, 0f),
                Children = [new() { Id = EntityId.New(), Name = "Barrel", Position = new(0f, 1f, 0f) }]
            }
        ]
    };
}
