// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Prefabs;
using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.Inspector;
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
        var instances = new PrefabInstances();
        var prefab = Turret();

        var root = Prefab.Instantiate(scene, instances, AssetId.New(), prefab);

        Assert.Equal(2, instances.Count);
        Assert.True(instances.TryGet(root, out var link));
        Assert.Equal(prefab.Roots[0].Id, link.Source);
    }

    /// <summary>⚠ Two instances of one prefab do not share the file's identities.</summary>
    [Fact]
    public void TwoInstancesDoNotShareIdentities() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var instances = new PrefabInstances();
        var asset = AssetId.New();

        var first = Prefab.Instantiate(scene, instances, asset, Turret());
        var second = Prefab.Instantiate(scene, instances, asset, Turret());

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
            () => Prefab.Instantiate(scene, new(), AssetId.New(), two)
        );
    }

    /// <summary>The hierarchy comes across with the instance.</summary>
    [Fact]
    public void ChildrenComeAcross() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");

        var root = Prefab.Instantiate(scene, new(), AssetId.New(), Turret());

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
        var instances = new PrefabInstances();

        var root = Prefab.Instantiate(scene, instances, AssetId.New(), Turret());

        Assert.True(instances.Forget(root));
        Assert.False(instances.TryGet(root, out _));
        Assert.Equal(1, instances.Count);
    }

    /// <summary>Pruning forgets links whose entity has gone.</summary>
    [Fact]
    public void PruningForgetsDeadEntities() {
        using var fixture = new EditorFixture();

        var world = new World("Scene");
        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Level");
        var instances = new PrefabInstances();

        var root = Prefab.Instantiate(scene, instances, AssetId.New(), Turret());
        scene.Delete([root]);

        Assert.Equal(2, instances.Prune(world));
        Assert.Equal(0, instances.Count);
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

/// <summary>The override marks the inspector draws, and what a revert reads.</summary>
public class PrefabSourceTests {
    static readonly InspectorMember Name = new InspectorMember<Sample, string>(
        nameof(Sample.Name),
        static sample => sample.Name,
        static (sample, value) => sample.Name = value
    );

    /// <summary>An object nothing was made from overrides nothing.</summary>
    [Fact]
    public void AnUnlinkedObjectOverridesNothing() {
        var source = new PrefabSource();

        Assert.False(source.IsOverridden(new Sample { Name = "a" }, Name));
        Assert.False(source.TryGetPrefabValue(new Sample(), Name, out _));
    }

    /// <summary>An object that matches its template overrides nothing either.</summary>
    [Fact]
    public void AMatchingObjectOverridesNothing() {
        var source = new PrefabSource();
        var instance = new Sample { Name = "Turret" };

        source.Link(instance, new Sample { Name = "Turret" });

        Assert.False(source.IsOverridden(instance, Name));
    }

    /// <summary>One that differs does, and the prefab's value is what a revert would write.</summary>
    [Fact]
    public void ADifferingObjectIsAnOverride() {
        var source = new PrefabSource();
        var instance = new Sample { Name = "Turret (renamed)" };

        source.Link(instance, new Sample { Name = "Turret" });

        Assert.True(source.IsOverridden(instance, Name));
        Assert.True(source.TryGetPrefabValue(instance, Name, out var value));
        Assert.Equal("Turret", value);
    }

    /// <summary>Unpacking an object stops it being compared at all.</summary>
    [Fact]
    public void UnlinkingStopsTheComparison() {
        var source = new PrefabSource();
        var instance = new Sample { Name = "Changed" };

        source.Link(instance, new Sample { Name = "Turret" });

        Assert.True(source.Unlink(instance));
        Assert.False(source.IsOverridden(instance, Name));
    }

    /// <summary>⚠ Pairing is by identity, so two equal objects are two pairings.</summary>
    [Fact]
    public void PairingIsByIdentity() {
        var source = new PrefabSource();

        var first = new Sample { Name = "Turret" };
        var second = new Sample { Name = "Turret" };

        source.Link(first, new Sample { Name = "Original" });

        Assert.True(source.IsOverridden(first, Name));
        Assert.False(source.IsOverridden(second, Name));
    }

    sealed class Sample {
        public string Name { get; set; } = string.Empty;
    }
}

/// <summary>What a prefab writer refuses, and why it refuses it there.</summary>
public class PrefabWriterTests {
    /// <summary>A single-root document writes.</summary>
    [Fact]
    public void OneRootWrites() {
        using var fixture = new EditorFixture();

        var world = new World("Prefab");
        var path = fixture.Paths.Absolute("Assets/Turret.vxprefab");

        var document = new SceneDocument(fixture.Project, world, AssetId.Empty, "Turret") {
            Writer = new PrefabFileWriter(path)
        };

        document.Add("Turret", LocalTransform.Identity);
        document.Save();

        Assert.Contains("Turret", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>⚠ Two roots are refused at the save, which is the moment work would be lost.</summary>
    [Fact]
    public void TwoRootsAreRefused() {
        using var fixture = new EditorFixture();

        var world = new World("Prefab");

        var document = new SceneDocument(fixture.Project, world, AssetId.Empty, "Turret") {
            Writer = new PrefabFileWriter(fixture.Paths.Absolute("Assets/Turret.vxprefab"))
        };

        document.Add("One", LocalTransform.Identity);
        document.Add("Two", LocalTransform.Identity);

        Assert.Throws<InvalidOperationException>(document.Save);
    }

    /// <summary>And so is none.</summary>
    [Fact]
    public void NoRootsAreRefused() {
        using var fixture = new EditorFixture();

        var world = new World("Prefab");

        var document = new SceneDocument(fixture.Project, world, AssetId.Empty, "Turret") {
            Writer = new PrefabFileWriter(fixture.Paths.Absolute("Assets/Turret.vxprefab"))
        };

        Assert.Throws<InvalidOperationException>(document.Save);
    }
}
