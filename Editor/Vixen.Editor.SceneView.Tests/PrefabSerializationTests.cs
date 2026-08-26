// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>
///     The prefab link, as a scene file carries it: doc 47's three keys, written by
///     <see cref="SceneSerializer" /> from the document's table and read back into it.
/// </summary>
/// <remarks>
///     ⚠ <b>These are the tests the format could not have before there was a table to write from.</b>
///     Doc 47 § 7 landed the keys and the pure functions over them and deliberately did not touch the
///     serializer, because nothing in the editor placed a prefab — so the writer would have been
///     plumbing for a caller that did not exist. This is the other end of that pipe.
/// </remarks>
public class PrefabSerializationTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-prefab-link-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Scene");
    readonly SceneDocument scene;

    public PrefabSerializationTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Level");
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    static readonly AssetId Turret = AssetId.New();
    static readonly string TurretReference = new AssetReference(Turret).ToString();

    /// <summary>A second document over a second world, for reading a file back into.</summary>
    (World World, SceneDocument Scene) Fresh() {
        var other = new World("Reloaded");

        return (other, new SceneDocument(project, other, AssetId.Empty, "Level"));
    }

    /// <summary>One lit entity that says it came from a prefab.</summary>
    Entity Linked(EntityId source, float intensity, params string[] claimed) {
        var entity = scene.Add("Lamp", LocalTransform.At(new Vector3(1f, 2f, 3f)));

        Lights.Attach(world, entity, LightKind.Point);
        scene.World.Get<Light>(entity).Intensity = intensity;
        scene.Prefabs.Record(entity, new(Turret, source), claimed);

        return entity;
    }

    // ── What the writer says ────────────────────────────────────────────────────────────────────

    /// <summary>All three keys, on every node of an instance.</summary>
    [Fact]
    public void TheThreeKeysAreWrittenForALinkedEntity() {
        var source = EntityId.New();
        Linked(source, 3f, "Position", "Light.Intensity");

        var yaml = SceneSerializer.ToYaml(scene);

        Assert.Contains($"prefab: {TurretReference}", yaml, StringComparison.Ordinal);
        Assert.Contains($"source: {source}", yaml, StringComparison.Ordinal);
        Assert.Contains("Light.Intensity", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The reference text and not a bare id, which is what makes the prefab findable.
    /// </summary>
    /// <remarks>
    ///     <c>ReferenceIndex</c> answers "what breaks if I delete this" by scanning for <c>vx:</c>
    ///     followed by thirty-two hex digits. A scene whose prefab the index could not see is one the
    ///     editor would offer to delete the prefab out from under.
    /// </remarks>
    [Fact]
    public void ThePrefabIsWrittenAsAReference() {
        Linked(EntityId.New(), 1f);

        var yaml = SceneSerializer.ToYaml(scene);
        var file = SceneSerializer.FromYaml(yaml);
        var entity = file.Roots[0];

        Assert.StartsWith("vx:", entity.Prefab, StringComparison.Ordinal);
        Assert.True(AssetReference.TryParse(entity.Prefab, out var parsed));
        Assert.Equal(Turret, parsed.Asset);
    }

    /// <summary>An entity that came from nowhere says so, by saying nothing.</summary>
    [Fact]
    public void AnEntityWithNoLinkIsNotAnInstance() {
        scene.Add("Crate", LocalTransform.Identity);

        var file = SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene));

        Assert.False(PrefabOverrides.IsInstance(file.Roots[0]));
        Assert.Empty(file.Roots[0].Overrides);
        Assert.True(file.Roots[0].Source.IsNone);
    }

    // ── What the reader does ────────────────────────────────────────────────────────────────────

    /// <summary>The link and its overrides come back into the document's table.</summary>
    [Fact]
    public void TheLinkAndItsOverridesComeBackOnLoad() {
        var source = EntityId.New();
        var placed = Linked(source, 3f, "Light.Intensity", "Position");
        var id = scene.IdOf(placed);

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            Assert.True(reloaded.TryGetEntity(id, out var entity));
            Assert.True(reloaded.Prefabs.TryGet(entity, out var link));
            Assert.Equal(Turret, link.Prefab);
            Assert.Equal(source, link.Source);
            Assert.True(reloaded.Prefabs.IsOverridden(entity, "Light.Intensity"));
            Assert.True(reloaded.Prefabs.IsOverridden(entity, "Position"));
            Assert.False(reloaded.Prefabs.IsOverridden(entity, "Scale"));
        }
    }

    /// <summary>Opening and saving a scene changes nothing about it.</summary>
    [Fact]
    public void SaveLoadSaveIsTheSameBytes() {
        Linked(EntityId.New(), 3f, "Light.Intensity", "Position");

        var first = SceneSerializer.ToYaml(scene);
        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(first));
            Assert.Equal(first, SceneSerializer.ToYaml(reloaded), StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     ⚠⚠ An override to zero survives, and this is the failure the whole design exists to
    ///     prevent.
    /// </summary>
    /// <remarks>
    ///     If overridden-ness were "differs from the template" or "is not the default", an author who
    ///     turns a lamp off has said something the file cannot hold — and the value would come back as
    ///     the template's on the next open. Presence in the list <i>is</i> the override.
    /// </remarks>
    [Fact]
    public void AnOverrideToZeroSurvivesTheRoundTrip() {
        var placed = Linked(EntityId.New(), 0f, "Light.Intensity");
        var id = scene.IdOf(placed);

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            Assert.True(reloaded.TryGetEntity(id, out var entity));
            Assert.True(reloaded.Prefabs.IsOverridden(entity, "Light.Intensity"));
            Assert.Equal(0f, reloaded.World.Get<Light>(entity).Intensity);
        }
    }

    /// <summary>⚠ An override naming a member nothing has is kept rather than pruned.</summary>
    /// <remarks>
    ///     A component taken off and put back leaves exactly this. The entry is the author's statement
    ///     and outlives the shape it was made against — a round trip that lost it would be silent.
    /// </remarks>
    [Fact]
    public void AnOverrideNamingNothingIsNotPruned() {
        var placed = Linked(EntityId.New(), 1f, "Rigidbody.Mass");
        var id = scene.IdOf(placed);

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            Assert.True(reloaded.TryGetEntity(id, out var entity));
            Assert.True(reloaded.Prefabs.IsOverridden(entity, "Rigidbody.Mass"));
            Assert.Contains("Rigidbody.Mass", SceneSerializer.ToYaml(reloaded), StringComparison.Ordinal);
        }
    }

    /// <summary>⚠ Half a link is a half-written file, and is not read as an instance.</summary>
    [Fact]
    public void HalfALinkIsNotReadAsAnInstance() {
        var half = new SceneFile {
            Name = "Level",
            Roots = [new() { Id = EntityId.New(), Name = "Lamp", Prefab = TurretReference }]
        };

        var second = new SceneFile {
            Name = "Level",
            Roots = [new() { Id = EntityId.New(), Name = "Lamp", Source = EntityId.New() }]
        };

        foreach (var file in new[] { half, second }) {
            var (other, reloaded) = Fresh();

            using (other) {
                SceneSerializer.Load(reloaded, file);
                Assert.Equal(0, reloaded.Prefabs.Count);
            }
        }
    }

    // ── The tables that travel ──────────────────────────────────────────────────────────────────

    /// <summary>⚠ A delete and an undo do not quietly unpack the instance.</summary>
    /// <remarks>
    ///     <c>PruneNames</c> drops the links of dead handles, so without the snapshot carrying them the
    ///     subtree would come back correct in every respect except that it no longer came from
    ///     anywhere — which nothing in the editor would show.
    /// </remarks>
    [Fact]
    public void DeletingAndUndoingKeepsTheLink() {
        var source = EntityId.New();
        var placed = Linked(source, 3f, "Light.Intensity");

        scene.Delete([placed]);
        Assert.Equal(0, scene.Prefabs.Count);

        Assert.True(scene.Stack.Undo());

        Assert.True(scene.Prefabs.TryGet(placed, out var link));
        Assert.Equal(source, link.Source);
        Assert.True(scene.Prefabs.IsOverridden(placed, "Light.Intensity"));
    }

    /// <summary>⚠ The links travel across a play-mode restore's translation table.</summary>
    [Fact]
    public void RemappingCarriesTheLinks() {
        var source = EntityId.New();
        var placed = Linked(source, 3f, "Position");
        var moved = scene.Add("Elsewhere", LocalTransform.Identity);

        scene.Remap(new Dictionary<Entity, Entity> { [placed] = moved });

        Assert.False(scene.Prefabs.TryGet(placed, out _));
        Assert.True(scene.Prefabs.TryGet(moved, out var link));
        Assert.Equal(source, link.Source);
        Assert.True(scene.Prefabs.IsOverridden(moved, "Position"));
    }

    /// <summary>⚠ An override on an entity with no link is refused rather than silently kept.</summary>
    /// <remarks>
    ///     A list with no <c>prefab</c> key beside it would be written and read back as nothing, which
    ///     is a marking that appeared to work and did not.
    /// </remarks>
    [Fact]
    public void MarkingRefusesAnEntityWithNoLink() {
        var loose = scene.Add("Crate", LocalTransform.Identity);

        Assert.False(scene.Prefabs.Mark(loose, "Position"));
        Assert.Empty(scene.Prefabs.OverridesOf(loose));
    }

    // ── The removed list ────────────────────────────────────────────────────────────────────────

    /// <summary>An instance root and one child of it, both linked to the same prefab.</summary>
    (Entity Root, Entity Child, EntityId ChildSource) Instance() {
        var rootSource = EntityId.New();
        var childSource = EntityId.New();

        var root = scene.Add("Turret", LocalTransform.Identity);
        var child = scene.Add("Barrel", LocalTransform.Identity, root);

        scene.Prefabs.Record(root, new(Turret, rootSource));
        scene.Prefabs.Record(child, new(Turret, childSource));

        return (root, child, childSource);
    }

    /// <summary>⚠⚠ Deleting a child of an instance is written down, by the id the template gave it.</summary>
    /// <remarks>
    ///     Doc 47 § 6: while nothing adds a template's children back, a deleted child is simply absent
    ///     and absence is unambiguous. The list has to exist <i>first</i>, because the day add-back
    ///     lands is the day absence stops meaning one thing — and a level then regrows the entities its
    ///     designer removed.
    /// </remarks>
    [Fact]
    public void DeletingAChildOfAnInstanceIsRecorded() {
        var instance = Instance();

        scene.Delete([instance.Child]);

        Assert.Equal([instance.ChildSource], scene.Prefabs.RemovedFrom(instance.Root));
    }

    /// <summary>⚠ An undo unsays it, exactly.</summary>
    [Fact]
    public void UndoingTheDeleteTakesTheRemovalBack() {
        var instance = Instance();

        scene.Delete([instance.Child]);
        Assert.True(scene.Stack.Undo());

        Assert.Empty(scene.Prefabs.RemovedFrom(instance.Root));

        // And a redo says it again, so the two directions stay in step.
        Assert.True(scene.Stack.Redo());
        Assert.Equal([instance.ChildSource], scene.Prefabs.RemovedFrom(instance.Root));
    }

    /// <summary>⚠ Deleting a whole instance records nothing, because there is nowhere to say it.</summary>
    [Fact]
    public void DeletingTheWholeInstanceRecordsNothing() {
        var instance = Instance();

        scene.Delete([instance.Root]);

        Assert.Equal(0, scene.Prefabs.Count);
        Assert.Empty(scene.Prefabs.RemovedFrom(instance.Root));
    }

    /// <summary>An ordinary entity's delete has nothing to do with a prefab.</summary>
    [Fact]
    public void DeletingALooseEntityRecordsNothing() {
        var instance = Instance();
        var loose = scene.Add("Crate", LocalTransform.Identity, instance.Root);

        scene.Delete([loose]);

        Assert.Empty(scene.Prefabs.RemovedFrom(instance.Root));
    }

    /// <summary>The removed list survives a save and a load.</summary>
    [Fact]
    public void TheRemovedListRoundTrips() {
        var instance = Instance();
        var id = scene.IdOf(instance.Root);

        scene.Delete([instance.Child]);

        var (other, reloaded) = Fresh();

        using (other) {
            SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

            Assert.True(reloaded.TryGetEntity(id, out var root));
            Assert.Equal([instance.ChildSource], reloaded.Prefabs.RemovedFrom(root));
        }
    }

    /// <summary>Unpacking one entity forgets what it claimed, because there is nothing left to claim against.</summary>
    [Fact]
    public void UnpackingForgetsTheOverrides() {
        var placed = Linked(EntityId.New(), 3f, "Position");

        Assert.True(scene.Prefabs.Forget(placed));
        Assert.Empty(scene.Prefabs.OverridesOf(placed));
        Assert.DoesNotContain("Position", SceneSerializer.ToYaml(scene), StringComparison.Ordinal);
    }
}
