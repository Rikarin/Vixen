// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Scenes;

/// <summary>A loaded scene.</summary>
/// <param name="Id">Its id, which is what <see cref="SceneTag" /> carries.</param>
public readonly record struct SceneHandle(int Id) {
    /// <summary>The handle to no scene.</summary>
    public static SceneHandle None => new(0);

    /// <summary>Whether this names a scene.</summary>
    public bool IsValid => Id > 0;
}

/// <summary>
///     Scenes loaded into one world, additively, each unloadable on its own.
/// </summary>
/// <remarks>
///     <para>
///         Several scenes share one world — a level, its lighting, the UI, a streamed chunk of
///         terrain — because the alternative, a world per scene, means every system runs once per
///         scene and every query stops being able to see across them. Membership is a component, so
///         unloading is a query and a destroy.
///     </para>
///     <para>
///         Unloading destroys whole subtrees. An entity in scene A parented to one in scene B is not
///         forbidden — it is sometimes what a designer means — but unloading B destroys A's entity
///         with it, because the alternative is a <see cref="Parent" /> pointing at a dead entity and
///         every walk over the hierarchy throwing.
///     </para>
/// </remarks>
public sealed class SceneManager {
    readonly World world;
    readonly Dictionary<int, string> names = [];
    readonly List<SceneHandle> loaded = [];
    readonly QueryDescription tagged = new QueryDescription().WithAll<SceneTag>();
    readonly List<Entity> scratch = [];

    int nextId = 1;

    /// <summary>The world the scenes live in.</summary>
    public World World => world;

    /// <summary>The scenes that are loaded, in the order they were.</summary>
    public IReadOnlyList<SceneHandle> Loaded => loaded;

    /// <summary>Creates a manager for a world.</summary>
    /// <param name="world">The world.</param>
    public SceneManager(World world) {
        ArgumentNullException.ThrowIfNull(world);
        this.world = world;
    }

    /// <summary>Opens an empty scene.</summary>
    /// <param name="name">Its name, for diagnostics and for the editor's scene list.</param>
    /// <returns>Its handle.</returns>
    public SceneHandle Create(string name) {
        var handle = new SceneHandle(nextId++);
        names[handle.Id] = name;
        loaded.Add(handle);
        return handle;
    }

    /// <summary>A scene's name.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns>Its name, or the empty string if it is not loaded.</returns>
    public string NameOf(SceneHandle scene) => names.GetValueOrDefault(scene.Id, "");

    /// <summary>Whether a scene is loaded.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns>Whether it is.</returns>
    public bool IsLoaded(SceneHandle scene) => names.ContainsKey(scene.Id);

    /// <summary>Creates an entity that belongs to a scene.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns>The entity, tagged and with no other components.</returns>
    public Entity CreateEntity(SceneHandle scene) {
        var entity = world.Create(new SceneTag { SceneId = Require(scene).Id });
        return entity;
    }

    /// <summary>Creates an entity with a transform that belongs to a scene.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="local">Where it starts.</param>
    /// <returns>The entity.</returns>
    public Entity CreateTransform(SceneHandle scene, LocalTransform local) {
        var entity = Hierarchy.CreateTransform(world, local);
        world.Add(entity, new SceneTag { SceneId = Require(scene).Id });
        return entity;
    }

    /// <summary>Puts an existing entity, and everything below it, into a scene.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="root">The subtree root.</param>
    public void Adopt(SceneHandle scene, Entity root) {
        var id = Require(scene).Id;
        Tag(root, id);
    }

    /// <summary>Which scene an entity belongs to.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Its scene, or <see cref="SceneHandle.None" />.</returns>
    public SceneHandle SceneOf(Entity entity) =>
        world.IsAlive(entity) && world.TryGet<SceneTag>(entity, out var tag) ? new(tag.SceneId) : SceneHandle.None;

    /// <summary>How many entities a scene owns.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns>The count.</returns>
    public int CountIn(SceneHandle scene) {
        var total = 0;

        foreach (var chunk in world.Chunks(tagged)) {
            foreach (var tag in chunk.ReadValues<SceneTag>()) {
                if (tag.SceneId == scene.Id) {
                    total++;
                }
            }
        }

        return total;
    }

    /// <summary>Destroys everything a scene owns and forgets the scene.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="behaviors">
    ///     The store to drain, so the entities' behaviours get <c>OnDestroy</c> before the world
    ///     forgets them. Optional: without it they are reaped at the next lifecycle drain instead.
    /// </param>
    /// <returns>How many entities were destroyed.</returns>
    public int Unload(SceneHandle scene, BehaviorStore? behaviors = null) {
        if (!IsLoaded(scene)) {
            return 0;
        }

        Collect(scene);

        foreach (var entity in scratch) {
            if (behaviors is not null) {
                foreach (var behavior in behaviors.AllOn(entity).ToArray()) {
                    behaviors.Destroy(behavior);
                }
            }
        }

        behaviors?.RunLifecycle();

        var destroyed = 0;

        foreach (var entity in scratch) {
            // Subtrees, and only from a root of the set: a child destroyed by its parent's sweep is
            // already gone by the time its own turn comes, and IsAlive is what says so.
            if (world.IsAlive(entity)) {
                destroyed += Destroy(entity);
            }
        }

        names.Remove(scene.Id);
        loaded.Remove(scene);
        return destroyed;
    }

    void Collect(SceneHandle scene) {
        scratch.Clear();

        foreach (var chunk in world.Chunks(tagged)) {
            var tags = chunk.ReadValues<SceneTag>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (tags[index].SceneId == scene.Id) {
                    scratch.Add(entities[index]);
                }
            }
        }
    }

    int Destroy(Entity entity) {
        var destroyed = 1;

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            destroyed += Destroy(child);
        }

        Hierarchy.SetParent(world, entity, Entity.Null);
        world.Destroy(entity);
        return destroyed;
    }

    void Tag(Entity entity, int id) {
        if (world.Has<SceneTag>(entity)) {
            world.Set(entity, new SceneTag { SceneId = id });
        } else {
            world.Add(entity, new SceneTag { SceneId = id });
        }

        foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
            Tag(child, id);
        }
    }

    SceneHandle Require(SceneHandle scene) {
        if (!IsLoaded(scene)) {
            throw new ArgumentException(
                $"Scene {scene.Id} is not loaded. Creating an entity in an unloaded scene would give "
                + "it a tag nothing ever sweeps, which is a leak that looks like an ordinary entity.",
                nameof(scene)
            );
        }

        return scene;
    }
}
