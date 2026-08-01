// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>A behaviour written into a scene and read back out of it.</summary>
/// <remarks>
///     ⚠ <b>The third seam, and the one that makes the other two worth having.</b> A behaviour the
///     inspector can attach and cannot save is one somebody loses by closing the editor. It goes into
///     the same alias-tagged <c>Components</c> list a component does — a file with two ways of saying
///     "this entity carries a thing called X" would be one the loader has to guess about.
/// </remarks>
public class BehaviorSceneTests {
    static BehaviorSceneTests() => SceneBehaviorRegistry.Register<Patrol>();

    static SceneDocument Document(World world) =>
        new(new EditorProject(new ProjectPaths(Path.Combine(Path.GetTempPath(), "vixen-behaviour-scene"))),
            world,
            AssetId.Empty,
            "Untitled");

    [Fact]
    public void A_behaviour_survives_a_round_trip_with_its_values() {
        using var world = new World("Scene");

        var scene = Document(world);
        var entity = scene.Add("Guard", LocalTransform.Identity);

        scene.Behaviors.Add(entity, new Patrol { Speed = 7.5f, Distance = 24f });

        var yaml = SceneSerializer.ToYaml(scene);

        // Written by its alias, the same way a component is — `- !Patrol` and the keys under it.
        Assert.Contains("Patrol", yaml, StringComparison.Ordinal);

        using var other = new World("Reloaded");

        var reloaded = Document(other);

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(yaml));

        var restored = reloaded.Behaviors.Get<Patrol>(Assert.Single(reloaded.Roots));

        Assert.NotNull(restored);
        Assert.Equal(7.5f, restored.Speed);
        Assert.Equal(24f, restored.Distance);

        // ⚠ And it is attached, not merely constructed: the loader goes through the store, so the
        // entity's link holds it and anything asking the store finds it.
        Assert.Same(restored, reloaded.Behaviors.AllOn(Assert.Single(reloaded.Roots)).ToArray().Single());
    }

    /// <summary>
    ///     ⚠ <b>The base class's transform façade must not reach the file.</b> <c>Behavior.Position</c>
    ///     is settable and public, so without <c>[DataMemberIgnore]</c> being honoured by the mapper
    ///     it would be written beside the transform that already holds it — and assigned back through
    ///     an object not yet attached to an entity, which throws.
    /// </summary>
    [Fact]
    public void The_bases_plumbing_is_not_written_into_the_file() {
        using var world = new World("Scene");

        var scene = Document(world);
        var entity = scene.Add("Guard", LocalTransform.At(new(3f, 4f, 5f)));

        scene.Behaviors.Add(entity, new Patrol());

        var yaml = SceneSerializer.ToYaml(scene);

        Assert.DoesNotContain("localPosition", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isAwake", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("world", yaml, StringComparison.OrdinalIgnoreCase);

        // The entity's own transform is still there — it is the authored one, written where a scene
        // has always written it.
        Assert.Contains("3", yaml, StringComparison.Ordinal);
    }

    /// <summary>A scene with both kinds on one entity keeps both.</summary>
    [Fact]
    public void A_component_and_a_behaviour_on_one_entity_both_come_back() {
        using var world = new World("Scene");

        var scene = Document(world);
        var entity = scene.Add("Guard", LocalTransform.Identity);

        world.Add(entity, Vixen.Engine.Cameras.Camera.Perspective);
        scene.Behaviors.Add(entity, new Patrol { Speed = 2f });

        using var other = new World("Reloaded");

        var reloaded = Document(other);

        SceneSerializer.Load(reloaded, SceneSerializer.FromYaml(SceneSerializer.ToYaml(scene)));

        var root = Assert.Single(reloaded.Roots);

        Assert.True(other.Has<Vixen.Engine.Cameras.Camera>(root));
        Assert.Equal(2f, reloaded.Behaviors.Get<Patrol>(root)!.Speed);
    }
}

/// <summary>A behaviour a scene may name, standing in for a game's own.</summary>
[DataContract("Patrol")]
public sealed class Patrol : Behavior {
    /// <summary>How fast it walks.</summary>
    public float Speed { get; set; } = 3f;

    /// <summary>How far it goes before turning round.</summary>
    public float Distance { get; set; } = 10f;
}
