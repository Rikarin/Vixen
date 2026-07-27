// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Cameras;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class SceneAndPrefabTests {
    // ---------------------------------------------------------------- scenes

    [Fact]
    public void EntitiesCreatedInASceneCarryItsTag() {
        using var world = new World();
        var scenes = new SceneManager(world);
        var level = scenes.Create("level");

        var entity = scenes.CreateEntity(level);

        Assert.Equal(level, scenes.SceneOf(entity));
        Assert.Equal(1, scenes.CountIn(level));
    }

    [Fact]
    public void ScenesLoadAdditivelyIntoOneWorld() {
        using var world = new World();
        var scenes = new SceneManager(world);
        var level = scenes.Create("level");
        var ui = scenes.Create("ui");

        scenes.CreateEntity(level);
        scenes.CreateEntity(level);
        scenes.CreateEntity(ui);

        Assert.Equal(3, world.EntityCount);
        Assert.Equal(2, scenes.CountIn(level));
        Assert.Equal(1, scenes.CountIn(ui));
    }

    [Fact]
    public void UnloadingASceneTakesItsEntitiesAndLeavesTheOthers() {
        using var world = new World();
        var scenes = new SceneManager(world);
        var level = scenes.Create("level");
        var ui = scenes.Create("ui");

        var kept = scenes.CreateEntity(ui);
        scenes.CreateEntity(level);
        scenes.CreateEntity(level);

        Assert.Equal(2, scenes.Unload(level));

        Assert.False(scenes.IsLoaded(level));
        Assert.True(world.IsAlive(kept));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void UnloadingTakesWholeSubtrees() {
        using var world = new World();
        var scenes = new SceneManager(world);
        var level = scenes.Create("level");

        var root = scenes.CreateTransform(level, LocalTransform.Identity);
        var child = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        Hierarchy.SetParent(world, child, root);
        scenes.Adopt(level, root);

        Assert.Equal(2, scenes.Unload(level));
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void UnloadingRunsOnDestroyForTheBehavioursItTakes() {
        var log = new List<string>();
        using var world = new World();
        var scenes = new SceneManager(world);
        var behaviors = new BehaviorStore(world);
        var level = scenes.Create("level");

        behaviors.Add(scenes.CreateEntity(level), new Marker(log));
        behaviors.RunLifecycle();
        log.Clear();

        scenes.Unload(level, behaviors);

        Assert.Equal(["OnDisable", "OnDestroy"], log);
        Assert.Equal(0, behaviors.Count);
    }

    [Fact]
    public void CreatingInAnUnloadedSceneIsRefused() {
        using var world = new World();
        var scenes = new SceneManager(world);
        var level = scenes.Create("level");
        scenes.Unload(level);

        Assert.Throws<ArgumentException>(() => scenes.CreateEntity(level));
    }

    // ---------------------------------------------------------------- prefabs

    [Fact]
    public void AnInstanceHasTheSameShapeAndValuesAsWhatWasCaptured() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 0, 0)));
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 2, 0)));
        var grandchild = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 0, 3)));

        Hierarchy.SetParent(world, child, root);
        Hierarchy.SetParent(world, grandchild, child);

        using var prefab = Prefab.CaptureFrom(world, root, "tower");
        var instance = prefab.Instantiate(world);
        var system = new TransformSystem();
        system.Resolve(world);

        Assert.Equal(3, prefab.EntityCount);
        Assert.NotEqual(root, instance);
        Assert.Equal(new Vector3(1, 0, 0), world.Read<LocalTransform>(instance).Position);

        var instanceChild = Assert.Single(Children(world, instance));
        var instanceGrandchild = Assert.Single(Children(world, instanceChild));

        Assert.Equal(new Vector3(1, 2, 3), world.Read<WorldTransform>(instanceGrandchild).Position);
        Assert.Equal(2, Hierarchy.DepthOf(world, instanceGrandchild));
    }

    [Fact]
    public void AnInstanceCanBePlacedSomewhereElse() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 0, 0)));
        var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 2, 0)));
        Hierarchy.SetParent(world, child, root);

        using var prefab = Prefab.CaptureFrom(world, root);
        var instance = prefab.Instantiate(world, LocalTransform.At(new(10, 0, 0)));
        new TransformSystem().Resolve(world);

        Assert.Equal(new Vector3(10, 2, 0), world.Read<WorldTransform>(Assert.Single(Children(world, instance))).Position);
    }

    /// <summary>
    ///     The whole reason a prefab is a plan rather than a walk: entities of one archetype are
    ///     created in one go.
    /// </summary>
    [Fact]
    public void InstantiationIsOneBulkCreatePerArchetype() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        world.Add(root, new Camera());

        for (var index = 0; index < 20; index++) {
            var child = Hierarchy.CreateTransform(world, LocalTransform.At(new(index, 0, 0)));
            Hierarchy.SetParent(world, child, root);
        }

        using var prefab = Prefab.CaptureFrom(world, root);

        Assert.Equal(21, prefab.EntityCount);

        // The root carries a Camera and the leaves do not, so two archetypes and no more: the
        // hierarchy components are stripped at capture and rebuilt, or every depth would be its own.
        Assert.Equal(2, prefab.ArchetypeCount);
    }

    [Fact]
    public void ChangingTheSourceAfterCaptureDoesNotChangeThePrefab() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.At(new(1, 0, 0)));

        using var prefab = Prefab.CaptureFrom(world, root);
        world.Get<LocalTransform>(root).Position = new(99, 0, 0);
        world.Destroy(root);

        var instance = prefab.Instantiate(world);

        Assert.Equal(new Vector3(1, 0, 0), world.Read<LocalTransform>(instance).Position);
    }

    [Fact]
    public void AManagedComponentSurvivesCaptureAndInstantiation() {
        using var world = new World();
        var root = Hierarchy.CreateTransform(world, LocalTransform.Identity);
        world.Add(root, new Nameplate { Text = "turret" });

        using var prefab = Prefab.CaptureFrom(world, root);
        var instance = prefab.Instantiate(world);

        Assert.Equal("turret", world.Read<Nameplate>(instance).Text);

        // The *same* object, not a copy of it. A managed component is a reference — a Mesh, a
        // Material, a Texture — and stamping out a hundred instances of a prefab must not clone the
        // mesh a hundred times. Anything that genuinely wants per-instance state puts it in an
        // unmanaged component, which is the whole rule.
        Assert.Same(world.Read<Nameplate>(root), world.Read<Nameplate>(instance));
    }

    // ---------------------------------------------------------------- cameras

    [Fact]
    public void ACamerasProjectionIsReverseZ() {
        var camera = Camera.Perspective;
        var projection = CameraMath.Projection(in camera, aspectRatio: 16f / 9f);

        // A point on the near plane must map to depth 1 and one on the far plane to depth 0, which
        // is what the rest of the engine is built for: clear to 0, test GREATER.
        Assert.True(Depth(projection, camera.NearPlane) > 0.99f);
        Assert.True(Depth(projection, camera.FarPlane) < 0.01f);
    }

    [Fact]
    public void ACameraWithNoAspectRatioNeedsToBeToldOne() {
        var camera = Camera.Perspective;

        Assert.Throws<ArgumentOutOfRangeException>(() => CameraMath.Projection(in camera));
    }

    [Fact]
    public void AZeroedCameraIsNotAUsableOne() {
        // The trap that cost a whole afternoon in Phase 1: a `default` struct whose documented
        // defaults live in a property nobody called.
        Assert.NotEqual(default, Camera.Perspective);
        Assert.True(Camera.Perspective.FarPlane > 0f);
    }

    [Fact]
    public void TheViewMatrixIsTheInverseOfWhereTheCameraIs() {
        using var world = new World();
        var entity = Hierarchy.CreateTransform(world, LocalTransform.At(new(0, 5, 10)));
        world.Add(entity, Camera.Perspective);
        new TransformSystem().Resolve(world);

        var view = CameraMath.View(world.Read<WorldTransform>(entity));

        // Where the camera is becomes the origin, and the origin becomes where the camera is not.
        Assert.Equal(Vector3.Zero, Round(Matrix4x4.TransformPosition(new(0, 5, 10), view)));
        Assert.Equal(new Vector3(0, -5, -10), Round(Matrix4x4.TransformPosition(Vector3.Zero, view)));
    }

    static float Depth(Matrix4x4 projection, float distance) {
        // -Z is forward, so a point `distance` in front of the camera is at z = -distance. Written
        // out rather than through a helper because Vixen.Core.Mathematics has no Vector4-by-matrix
        // transform: everything that has needed one so far wanted the perspective divide too, and
        // this is the first caller that wants the w it divides by.
        var z = (-distance * projection.M33) + projection.M43;
        var w = (-distance * projection.M34) + projection.M44;
        return z / w;
    }

    static Vector3 Round(Vector3 value) =>
        new(MathF.Round(value.X, 3), MathF.Round(value.Y, 3), MathF.Round(value.Z, 3));

    static List<Entity> Children(World world, Entity entity) {
        var children = new List<Entity>();

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            children.Add(child);
        }

        return children;
    }

    sealed class Marker(List<string> log) : Behavior {
        protected override void OnDisable() => log.Add("OnDisable");

        protected override void OnDestroy() => log.Add("OnDestroy");
    }

    sealed class Nameplate {
        public string Text { get; init; } = "";
    }
}
