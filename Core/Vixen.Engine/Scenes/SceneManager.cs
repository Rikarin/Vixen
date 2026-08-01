// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
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
    /// <summary>How far one world's scene ids have been handed out.</summary>
    sealed class Counter {
        public int Next = 1;
    }

    /// <summary>
    ///     ⚠ <b>The id space belongs to the <i>world</i> rather than to this object, and that is the
    ///     whole of why this is static.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="SceneTag" /> lives on an entity in the world, so two managers over one
    ///         world are two allocators writing into one namespace — and with a counter each they
    ///         both hand out 1. Everything downstream then reads as a duplicate: the editor's own
    ///         scene and a <c>.vxscene</c> opened as an asset both claim scene 1, so each document's
    ///         entity list — which filters by exactly that tag — returns the other's entities as well
    ///         as its own. It presents as a hierarchy holding every entity twice, once named and once
    ///         as <c>Entity 4</c>, because a document knows only the names it loaded itself. It is
    ///         also a save that writes the other document's entities into this one's file, and a
    ///         compiled-scene pane reporting twice the blocks a build would produce.
    ///     </para>
    ///     <para>
    ///         Sharing one manager between the documents over a world fixes it and relies on every
    ///         caller remembering to; there are twenty-eight places that construct one. Keying the
    ///         counter on the world makes the mistake unavailable instead, which is the difference
    ///         between a rule and a convention.
    ///     </para>
    ///     <para>
    ///         A <see cref="ConditionalWeakTable{TKey,TValue}" /> rather than a dictionary, so a
    ///         counter dies with the world it counts for: a static map of every world a process ever
    ///         made would keep all of them alive, which in an editor that opens and closes projects
    ///         is a leak measured in whole scenes.
    ///     </para>
    /// </remarks>
    static readonly ConditionalWeakTable<World, Counter> Counters = [];

    readonly World world;
    readonly Dictionary<int, string> names = [];
    readonly List<SceneHandle> loaded = [];
    readonly QueryDescription tagged = new QueryDescription().WithAll<SceneTag>();
    readonly List<Entity> scratch = [];

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
    /// <remarks>
    ///     The id comes from the world's counter and not this manager's, so two managers over one
    ///     world never name one scene — see <see cref="Counters" />, which is where the reasoning is.
    ///     Everything else here is per manager and rightly so: which scenes <i>this</i> view has
    ///     loaded, and what it calls them, are properties of the view.
    /// </remarks>
    public SceneHandle Create(string name) {
        var counter = Counters.GetOrCreateValue(world);
        int id;

        // Locked because the table is shared and a world may be reached from more than one thread
        // before anything has been scheduled on it — an editor opening a document on a background
        // task is the ordinary case. The contention is one increment per scene opened.
        lock (counter) {
            id = counter.Next++;
        }

        var handle = new SceneHandle(id);

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
