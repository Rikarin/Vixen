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
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>
///     Placing a prefab into a scene, and what a scene does when the prefab has changed underneath it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The verb doc 47 § 7 names as the blocker.</b> Until something in the shell placed a
///         prefab, <c>PrefabInstances</c> had no caller outside its own tests and the format's three
///         keys had nothing to hold — so a serializer that wrote them would have been plumbing for a
///         caller that did not exist. Every test here goes through a real project on disk, because the
///         GUID-to-path step is the whole reason reconciliation is the editor's job and not a build's.
///     </para>
///     <para>
///         ⚠ <b><c>SceneScalars.Register()</c> before anything is written.</b> A <c>Vector3</c> in one
///         of these files reads back as <c>(0, 0, 0)</c> unless the converter is registered first, and
///         <see cref="SceneSerializer" />'s static constructor is what usually does it — so a fixture
///         that builds a <c>SceneFile</c> and calls <c>ToYaml</c> on it before touching the serializer
///         writes a file whose positions are silently a different shape.
///     </para>
/// </remarks>
public class PrefabPlacementTests {
    static PrefabPlacementTests() => SceneScalars.Register();

    const string TemplateName = "Turret";

    static readonly EntityId Source = EntityId.New();
    static readonly EntityId ChildSource = EntityId.New();

    /// <summary>The prefab as its file holds it: one root, one child, one light.</summary>
    static SceneFile Template(Vector3 position, float intensity) =>
        new() {
            Name = TemplateName,
            Roots = [
                new() {
                    Id = Source,
                    Name = TemplateName,
                    Position = position,
                    Components = [Lamp(intensity)],
                    Children = [
                        new() { Id = ChildSource, Name = "Barrel", Position = new(0f, 1f, 0f) }
                    ]
                }
            ]
        };

    static Light Lamp(float intensity) {
        var light = Lights.Default(LightKind.Point);
        light.Intensity = intensity;

        return light;
    }

    /// <summary>Puts a prefab into a project and hands back its GUID.</summary>
    static AssetId Publish(EditorFixture fixture, SceneFile prefab, string name = "turret") {
        var asset = AssetId.New();

        fixture.WriteAsset(
            $"Assets/{name}{SceneFile.PrefabExtension}",
            prefab.ToYaml(),
            $"guid: {asset}\nmetaVersion: 1\n"
        );

        fixture.Project.Open();

        return asset;
    }

    static SceneDocument Scene(EditorFixture fixture, World world) =>
        new(fixture.Project, world, AssetId.Empty, "Level");

    // ── Placing one ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A prefab dropped into a scene becomes an instance whose entities remember where they came from.</summary>
    [Fact]
    public void PlacingRecordsTheLinkOnEveryNode() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = Scene(fixture, world);
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));

        // ⚠ Both, because the format writes the link on every node of an instance rather than on its
        // root alone — a root that is unpacked would otherwise leave its children with a `source` and
        // nothing above them to read it against.
        Assert.Equal(2, scene.Prefabs.Count);
        Assert.True(scene.Prefabs.TryGet(root, out var link));
        Assert.Equal(asset, link.Prefab);
        Assert.Equal(Source, link.Source);
    }

    /// <summary>Placing writes the three keys into the scene file.</summary>
    [Fact]
    public void PlacingReachesTheFile() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = Scene(fixture, world);
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out _, out _));

        var written = SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene));
        var instances = written.All().Count(PrefabOverrides.IsInstance);

        Assert.Equal(2, instances);
        Assert.Equal(new AssetReference(asset).ToString(), written.Roots[0].Prefab);
        Assert.Equal(Source, written.Roots[0].Source);
    }

    /// <summary>⚠ One Ctrl+Z takes the whole instance back, and a redo brings it back linked.</summary>
    [Fact]
    public void PlacingIsOneUndoStep() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = Scene(fixture, world);
        var asset = Publish(fixture, Template(new(5f, 0f, 0f), 7f));

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));
        Assert.True(scene.Stack.Undo());

        Assert.False(world.IsAlive(root));
        Assert.Equal(0, scene.Prefabs.Count);

        Assert.True(scene.Stack.Redo());

        Assert.True(world.IsAlive(root));
        Assert.Equal(2, scene.Prefabs.Count);
        Assert.True(scene.Prefabs.TryGet(root, out var link));
        Assert.Equal(Source, link.Source);
    }

    /// <summary>⚠ A prefab the project does not have places nothing and says why.</summary>
    [Fact]
    public void AnUnknownPrefabIsReportedRatherThanThrown() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = Scene(fixture, world);

        Assert.False(
            Prefab.TryPlace(scene, fixture.Project.Assets, AssetId.New(), Entity.Null, out var root, out var why)
        );

        Assert.True(root.IsNull);
        Assert.Equal(PrefabUnresolvedKind.NotInProject, why.Kind);
        Assert.Equal(0, world.EntityCount);
    }

    /// <summary>⚠ A two-rooted prefab is refused as a report rather than as an exception out of a drop.</summary>
    [Fact]
    public void ATwoRootedPrefabIsRefusedWithoutThrowing() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = Scene(fixture, world);
        var two = Template(Vector3.Zero, 1f);

        two.Roots.Add(new() { Id = EntityId.New(), Name = "Second" });

        var asset = Publish(fixture, two);

        Assert.False(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out _, out var why));
        Assert.Equal(PrefabUnresolvedKind.Unreadable, why.Kind);
    }

    /// <summary>Only a <c>.vxprefab</c> means "place an instance".</summary>
    [Fact]
    public void OnlyAPrefabIsClaimed() {
        Assert.True(Prefab.Claims("Assets/turret.vxprefab"));
        Assert.False(Prefab.Claims("Assets/level.vxscene"));
        Assert.False(Prefab.Claims(string.Empty));
    }

    // ── Reconciling on open ─────────────────────────────────────────────────────────────────────

    /// <summary>Writes a level with one instance in it, and hands back where it is and what names it.</summary>
    static (string Path, AssetId Asset, EntityId Instance) Level(
        EditorFixture fixture,
        AssetId prefab,
        Vector3 position,
        float intensity,
        params string[] claimed
    ) {
        var instance = EntityId.New();
        var asset = AssetId.New();

        var file = new SceneFile {
            Name = "Level",
            Roots = [
                new() {
                    Id = instance,
                    Name = TemplateName,
                    Position = position,
                    Prefab = new AssetReference(prefab).ToString(),
                    Source = Source,
                    Overrides = [.. claimed],
                    Components = [Lamp(intensity)]
                }
            ]
        };

        var path = fixture.WriteAsset(
            "Assets/level" + SceneFile.Extension,
            file.ToYaml(),
            $"guid: {asset}\nmetaVersion: 1\n"
        );

        fixture.Project.Open();

        return (path, asset, instance);
    }

    static SceneDocument Open(EditorFixture fixture, World world, string path, AssetId asset) =>
        (SceneDocument) new SceneEditorFactory(_ => world).Open(new(fixture.Project, asset, path));

    /// <summary>A member the instance does not claim takes the template's value when the scene opens.</summary>
    [Fact]
    public void AnUnclaimedMemberFollowsTheTemplate() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var prefab = Publish(fixture, Template(new(5f, 0f, 0f), 7f));
        var level = Level(fixture, prefab, new(1f, 0f, 0f), 7f);

        var document = Open(fixture, world, level.Path, level.Asset);

        Assert.True(document.TryGetEntity(level.Instance, out var entity));
        Assert.Equal(new Vector3(5f, 0f, 0f), world.Read<LocalTransform>(entity).Position);
        Assert.True(document.Reconciled is { Written: > 0 });
    }

    /// <summary>⚠⚠ A member overridden to zero keeps its zero when the template says otherwise.</summary>
    /// <remarks>
    ///     The single failure doc 47 exists to prevent, at the one moment it would happen: a reconcile
    ///     whose notion of "overridden" were "differs from the template" or "is not the default" would
    ///     read <c>0</c>, decide nothing was claimed, and turn the author's lamp back on.
    /// </remarks>
    [Fact]
    public void AnOverrideToZeroSurvivesOpening() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var prefab = Publish(fixture, Template(new(5f, 0f, 0f), 7f));
        var level = Level(fixture, prefab, new(1f, 0f, 0f), 0f, "Light.Intensity");

        var document = Open(fixture, world, level.Path, level.Asset);

        Assert.True(document.TryGetEntity(level.Instance, out var entity));
        Assert.Equal(0f, world.Read<Light>(entity).Intensity);

        // And the claim itself survives, so the next open answers the same way.
        Assert.True(document.Prefabs.IsOverridden(entity, "Light.Intensity"));

        // The unclaimed member still followed, which is what makes this an override rather than a
        // reconcile that did nothing.
        Assert.Equal(new Vector3(5f, 0f, 0f), world.Read<LocalTransform>(entity).Position);
    }

    /// <summary>A claimed member is left exactly as the level had it.</summary>
    [Fact]
    public void AClaimedMemberIsLeftAlone() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var prefab = Publish(fixture, Template(new(5f, 0f, 0f), 7f));
        var level = Level(fixture, prefab, new(1f, 0f, 0f), 7f, "Position");

        var document = Open(fixture, world, level.Path, level.Asset);

        Assert.True(document.TryGetEntity(level.Instance, out var entity));
        Assert.Equal(new Vector3(1f, 0f, 0f), world.Read<LocalTransform>(entity).Position);
    }

    /// <summary>⚠ A prefab that has gone costs the level nothing, and is reported.</summary>
    [Fact]
    public void AMissingPrefabLeavesTheInstanceIntact() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        // Never published, so the level names a GUID the project has never heard of — a renamed or
        // not-yet-imported asset, which must not be a data loss.
        var level = Level(fixture, AssetId.New(), new(1f, 0f, 0f), 4f);

        var document = Open(fixture, world, level.Path, level.Asset);

        Assert.True(document.TryGetEntity(level.Instance, out var entity));
        Assert.Equal(new Vector3(1f, 0f, 0f), world.Read<LocalTransform>(entity).Position);
        Assert.Equal(4f, world.Read<Light>(entity).Intensity);

        // The link is kept, so the instance comes back the moment the asset does.
        Assert.True(document.Prefabs.TryGet(entity, out _));

        Assert.NotNull(document.Reconciled);
        Assert.Equal(PrefabUnresolvedKind.NotInProject, document.Reconciled.Unresolved[0].Kind);
    }

    /// <summary>A scene with no instances reconciles to nothing and says nothing.</summary>
    [Fact]
    public void ASceneWithNoInstancesReportsNothing() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var file = new SceneFile {
            Name = "Level",
            Roots = [new() { Id = EntityId.New(), Name = "Crate", Position = new(1f, 0f, 0f) }]
        };

        var asset = AssetId.New();

        var path = fixture.WriteAsset(
            "Assets/empty" + SceneFile.Extension,
            file.ToYaml(),
            $"guid: {asset}\nmetaVersion: 1\n"
        );

        fixture.Project.Open();

        var document = Open(fixture, world, path, asset);

        Assert.NotNull(document.Reconciled);
        Assert.False(document.Reconciled.Changed);
    }

    /// <summary>⚠ A prefab whose file has gone is reported by that name rather than as "not indexed".</summary>
    /// <remarks>
    ///     The difference matters to a person: an asset the index has never heard of is a rename or an
    ///     unfinished import, and one the index knows with nothing behind it is a file somebody deleted
    ///     without its sidecar.
    /// </remarks>
    [Fact]
    public void APrefabWhoseFileHasGoneSaysSo() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = Scene(fixture, world);
        var asset = Publish(fixture, Template(Vector3.Zero, 1f));

        File.Delete(fixture.Paths.Absolute("Assets/turret" + SceneFile.PrefabExtension));

        Assert.False(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out _, out var why));
        Assert.Equal(PrefabUnresolvedKind.NoFile, why.Kind);
    }

    /// <summary>⚠ A hand-edited <c>prefab</c> key that is not a reference is reported, not thrown.</summary>
    [Fact]
    public void APrefabKeyThatIsNotAReferenceIsReported() {
        using var fixture = new EditorFixture();

        Assert.False(PrefabReconcile.TryOpen("Turret.vxprefab", fixture.Project.Assets, out _, out var why));
        Assert.Equal(PrefabUnresolvedKind.NotAReference, why.Kind);
    }

    // ── Nesting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>⚠ Placing an outer prefab does not overwrite the link its file already carried.</summary>
    /// <remarks>
    ///     R7 allows one level of nesting, and the format needs no new syntax for it because the link
    ///     is written on every node. What it does need is a placement that leaves an inner link alone —
    ///     recording the outer one over the top would flatten a level of nesting on every placement,
    ///     silently, and the subtree would answer to the wrong template for ever after.
    /// </remarks>
    [Fact]
    public void AnInnerLinkSurvivesAnOuterPlacement() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var scene = Scene(fixture, world);
        var inner = AssetId.New();
        var innerSource = EntityId.New();

        var outer = Template(new(5f, 0f, 0f), 7f);

        // The outer prefab's child is itself an instance of another prefab.
        outer.Roots[0].Children[0].Prefab = new AssetReference(inner).ToString();
        outer.Roots[0].Children[0].Source = innerSource;

        var asset = Publish(fixture, outer);

        Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, asset, Entity.Null, out var root, out _));

        var child = Entity.Null;

        foreach (var candidate in Hierarchy.ChildrenOf(world, root)) {
            child = candidate;
        }

        Assert.False(child.IsNull);

        Assert.True(scene.Prefabs.TryGet(child, out var link));
        Assert.Equal(inner, link.Prefab);
        Assert.Equal(innerSource, link.Source);

        // And the root is still the outer one's.
        Assert.True(scene.Prefabs.TryGet(root, out var outerLink));
        Assert.Equal(asset, outerLink.Prefab);
    }

    // ── Nested reconciliation, and add-back ─────────────────────────────────────────────────────

    /// <summary>The inner prefab: one entity, dim, low.</summary>
    static SceneFile Bulb(EntityId source, Vector3 position, float intensity) =>
        new() {
            Name = "Bulb",
            Roots = [
                new() {
                    Id = source,
                    Name = "Bulb",
                    Position = position,
                    Components = [Lamp(intensity)]
                }
            ]
        };

    /// <summary>The outer prefab, holding an instance of the inner one with its intensity claimed.</summary>
    static SceneFile Housing(AssetId inner, EntityId innerSource, float claimed) =>
        new() {
            Name = TemplateName,
            Roots = [
                new() {
                    Id = Source,
                    Name = TemplateName,
                    Children = [
                        new() {
                            Id = EntityId.New(),
                            Name = "Bulb",
                            Prefab = new AssetReference(inner).ToString(),
                            Source = innerSource,
                            Overrides = ["Light.Intensity"],
                            Components = [Lamp(claimed)]
                        }
                    ]
                }
            ]
        };

    /// <summary>A level holding one instance of the outer prefab, nested node and all.</summary>
    static (string Path, AssetId Asset, EntityId Nested) NestedLevel(
        EditorFixture fixture,
        AssetId outer,
        AssetId inner,
        EntityId innerSource
    ) {
        var nested = EntityId.New();
        var asset = AssetId.New();

        var file = new SceneFile {
            Name = "Level",
            Roots = [
                new() {
                    Id = EntityId.New(),
                    Name = TemplateName,
                    Prefab = new AssetReference(outer).ToString(),
                    Source = Source,
                    Children = [
                        new() {
                            Id = nested,
                            Name = "Bulb",
                            Prefab = new AssetReference(inner).ToString(),
                            Source = innerSource,
                            Components = [Lamp(0f)]
                        }
                    ]
                }
            ]
        };

        var path = fixture.WriteAsset(
            "Assets/level" + SceneFile.Extension,
            file.ToYaml(),
            $"guid: {asset}\nmetaVersion: 1\n"
        );

        fixture.Project.Open();

        return (path, asset, nested);
    }

    /// <summary>⚠⚠ A nested instance follows the outer prefab's overrides and the inner prefab's rest.</summary>
    /// <remarks>
    ///     <para>
    ///         Doc 47 row 5, and both halves of "outer over inner" at once. The scene node carries the
    ///         <i>inner</i> prefab's link, so the obvious reading — reconcile it against the inner
    ///         prefab — is available and wrong: it discards every override the outer prefab's author
    ///         made over the inner one, silently, on every open.
    ///     </para>
    ///     <para>
    ///         The intensity is the outer's claim and must survive; the position is a member the outer
    ///         did not claim, so it has to come from the inner prefab <i>through</i> the outer — which
    ///         is the template composition that runs before the scene is touched at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ANestedInstanceTakesTheOuterPrefabsClaimAndTheInnersRest() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var innerSource = EntityId.New();
        var inner = AssetId.New();

        fixture.WriteAsset(
            $"Assets/bulb{SceneFile.PrefabExtension}",
            Bulb(innerSource, new(0f, 3f, 0f), 1f).ToYaml(),
            $"guid: {inner}\nmetaVersion: 1\n"
        );

        var outer = Publish(fixture, Housing(inner, innerSource, 42f), "housing");
        var level = NestedLevel(fixture, outer, inner, innerSource);

        var document = Open(fixture, world, level.Path, level.Asset);

        Assert.True(document.TryGetEntity(level.Nested, out var entity));

        // The outer prefab claimed the intensity, so the inner prefab's 1 must not reach it.
        Assert.Equal(42f, world.Read<Light>(entity).Intensity);

        // The outer prefab did not claim the position, so the inner prefab's is what it shows.
        Assert.Equal(new Vector3(0f, 3f, 0f), world.Read<LocalTransform>(entity).Position);
    }

    /// <summary>⚠ A child the prefab gained arrives in every level that uses it — doc 47 row 4.</summary>
    /// <remarks>
    ///     The level on disk holds the instance root and nothing under it; the prefab holds a child.
    ///     Propagation over structure, which is the half a prefab existed for and did not have.
    /// </remarks>
    [Fact]
    public void AChildTheTemplateGainedReachesTheLevelOnOpen() {
        using var fixture = new EditorFixture();
        using var world = new World("Scene");

        var prefab = Publish(fixture, Template(new(5f, 0f, 0f), 7f));
        var level = Level(fixture, prefab, new(1f, 0f, 0f), 7f);

        var document = Open(fixture, world, level.Path, level.Asset);

        Assert.True(document.TryGetEntity(level.Instance, out var entity));

        var child = Entity.Null;

        foreach (var candidate in Hierarchy.ChildrenOf(world, entity)) {
            child = candidate;
        }

        Assert.False(child.IsNull);
        Assert.Equal("Barrel", document.NameOf(child));

        // ⚠ Linked in its own right, so the next open finds it rather than adding it again.
        Assert.True(document.Prefabs.TryGet(child, out var link));
        Assert.Equal(prefab, link.Prefab);
        Assert.Equal(ChildSource, link.Source);
        Assert.Empty(document.Prefabs.OverridesOf(child));
    }

    /// <summary>⚠⚠ A child the designer deleted stays deleted across the template gaining it back.</summary>
    /// <remarks>
    ///     <para>
    ///         The interaction doc 47 § 6 demands before add-back is allowed to exist, run through the
    ///         whole pipe: a designer deletes a child of an instance, the level is saved, the level is
    ///         opened again — and the template still has that child, because a designer deleting one
    ///         from <i>their</i> level is not the prefab losing it.
    ///     </para>
    ///     <para>
    ///         ⚠ Every step is real. The removal is written by the delete command, carried through the
    ///         save into <c>removed:</c>, and read back before the world exists. A break anywhere along
    ///         that chain is a level that regrows the entity on every open, for ever.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AChildTheDesignerDeletedIsNotRegrownOnOpen() {
        using var fixture = new EditorFixture();

        var prefab = Publish(fixture, Template(new(5f, 0f, 0f), 7f));
        string yaml;
        EntityId instance;

        using (var world = new World("Authoring")) {
            var scene = Scene(fixture, world);

            Assert.True(Prefab.TryPlace(scene, fixture.Project.Assets, prefab, Entity.Null, out var root, out _));

            instance = scene.IdOf(root);

            var child = Entity.Null;

            foreach (var candidate in Hierarchy.ChildrenOf(world, root)) {
                child = candidate;
            }

            Assert.False(child.IsNull);
            scene.Delete([child]);

            yaml = SceneSerializer.ToYaml(scene);
        }

        // The removal reached the file, which is the only place the next open can read it from.
        Assert.Contains(ChildSource.ToString(), yaml, StringComparison.Ordinal);

        var asset = AssetId.New();

        var path = fixture.WriteAsset(
            "Assets/level" + SceneFile.Extension,
            yaml,
            $"guid: {asset}\nmetaVersion: 1\n"
        );

        fixture.Project.Open();

        using var reopened = new World("Scene");
        var document = Open(fixture, reopened, path, asset);

        Assert.True(document.TryGetEntity(instance, out var entity));

        var regrown = 0;

        foreach (var _ in Hierarchy.ChildrenOf(reopened, entity)) {
            regrown++;
        }

        Assert.Equal(0, regrown);

        Assert.DoesNotContain(
            document.Reconciled?.Reports ?? [],
            report => report.Kind == PrefabReportKind.AddedByTemplate
        );
    }
}
