// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Coroutines;

namespace Vixen.Engine.Behaviors;

/// <summary>
///     Where behaviours live, bucketed by concrete type, and the lifecycle queues that drive them.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why buckets.</b> The naive version — one list of <see cref="Behavior" /> and a virtual
///         call per entity — is a cache miss and an indirect call per entity, which is precisely
///         what the ECS below exists to avoid. Behaviours are kept in a <c>T[]</c> per concrete
///         type, so a batch is contiguous and the call site sees one type.
///     </para>
///     <para>
///         <b>And why no generator.</b> [04](../../../docs/plan/04-ecs-and-scripting.md) has
///         <c>Vixen.Ecs.Generators</c> emit a <c>static void Update_PlayerController(Span&lt;…&gt;)</c>
///         per behaviour type to get that. It is not needed: <c>BehaviorBucket&lt;T&gt;</c> is closed
///         at the <c>Add&lt;T&gt;</c> call site, where the concrete type is already known, and its
///         loop is the same monomorphic walk over the same contiguous array. A generator would add
///         build-time machinery to produce code the JIT already gets to specialise. The generator is
///         still owed for the <c>[Inspector]</c> metadata the editor needs, which genuinely cannot
///         be had any other way.
///     </para>
///     <para>
///         <b>Enabled and disabled are partitioned, not filtered.</b> Each bucket keeps its enabled
///         behaviours in a prefix of its array, so the update loop stops at the boundary and a
///         thousand disabled behaviours cost nothing — the <c>[SkipIfDisabled]</c> split from doc 04,
///         without an attribute, because there is no reason not to always do it.
///     </para>
/// </remarks>
public sealed class BehaviorStore {
    readonly World world;
    readonly Dictionary<Type, IBehaviorBucket> buckets = [];
    readonly List<IBehaviorBucket> ordered = [];

    readonly List<Behavior> pendingAwake = [];
    readonly List<Behavior> awakening = [];
    readonly List<Behavior> startQueued = [];
    readonly List<Behavior> startReady = [];
    readonly List<Behavior> pendingEnable = [];
    readonly List<Behavior> pendingDisable = [];
    readonly List<Behavior> pendingDestroy = [];
    readonly List<Behavior> reaped = [];

    /// <summary>The world the behaviours' entities live in.</summary>
    public World World => world;

    /// <summary>
    ///     The clock the behaviours currently running are seeing — the frame's, or the fixed step's
    ///     inside <c>SystemPhase.FixedUpdate</c>.
    /// </summary>
    /// <remarks>
    ///     Held here rather than passed to every callback, because <c>Update()</c> taking no
    ///     parameters is most of what makes this API feel like the one users know. Set by the
    ///     behaviour systems from their <c>SystemContext</c> before each pass, so it is always the
    ///     phase's own time and never last phase's.
    /// </remarks>
    public GameTime Time { get; set; } = GameTime.Zero;

    /// <summary>How many behaviours exist, enabled or not.</summary>
    public int Count {
        get {
            var total = 0;

            foreach (var bucket in ordered) {
                total += bucket.Count;
            }

            return total;
        }
    }

    /// <summary>The scheduler this store's behaviours run their coroutines on.</summary>
    public CoroutineScheduler Coroutines { get; }

    /// <summary>Creates a store for a world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="coroutines">
    ///     The coroutine scheduler behaviours here should use, or <see langword="null" /> to make
    ///     one. <c>EngineLoop</c> passes its own, so that the loop's drain systems and the store's
    ///     behaviours are talking about the same queues.
    /// </param>
    public BehaviorStore(World world, CoroutineScheduler? coroutines = null) {
        ArgumentNullException.ThrowIfNull(world);
        this.world = world;
        Coroutines = coroutines ?? new CoroutineScheduler();
    }

    /// <summary>Attaches a behaviour to an entity.</summary>
    /// <typeparam name="T">The behaviour's concrete type. This is what buckets it.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <param name="behavior">The behaviour.</param>
    /// <returns>The behaviour, so the call reads as a construction.</returns>
    /// <remarks>
    ///     Nothing is called here. <c>Awake</c> and the rest run at the next lifecycle drain, so a
    ///     behaviour attached from inside another behaviour's <c>Update</c> cannot re-enter the loop
    ///     that is walking it.
    /// </remarks>
    public T Add<T>(Entity entity, T behavior) where T : Behavior {
        ArgumentNullException.ThrowIfNull(behavior);

        if (!world.IsAlive(entity)) {
            throw new EntityNotFoundException(entity, "is not alive, so nothing can be attached to it");
        }

        behavior.Entity = entity;
        behavior.World = world;
        behavior.Store = this;
        behavior.BucketKey = typeof(T);

        Bucket<T>().Add(behavior);
        Link(entity, behavior);
        pendingAwake.Add(behavior);
        return behavior;
    }

    /// <summary>Attaches a behaviour constructed with its parameterless constructor.</summary>
    /// <typeparam name="T">The behaviour type.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <returns>The new behaviour.</returns>
    public T Add<T>(Entity entity) where T : Behavior, new() => Add(entity, new T());

    /// <summary>The entity's behaviour of a type, if it has one.</summary>
    /// <typeparam name="T">The behaviour type. A subclass matches.</typeparam>
    /// <param name="entity">The entity.</param>
    /// <returns>It, or <see langword="null" />.</returns>
    public T? Get<T>(Entity entity) where T : Behavior {
        if (!world.IsAlive(entity) || !world.TryGet<BehaviorLink>(entity, out var link) || link.Items is null) {
            return null;
        }

        foreach (var behavior in link.Items) {
            if (behavior is T found && !found.IsDestroyed) {
                return found;
            }
        }

        return null;
    }

    /// <summary>Every behaviour on an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Its behaviours, or an empty span.</returns>
    public ReadOnlySpan<Behavior> AllOn(Entity entity) =>
        world.IsAlive(entity) && world.TryGet<BehaviorLink>(entity, out var link) && link.Items is not null
            ? link.Items
            : [];

    /// <summary>Queues a behaviour for destruction at the next lifecycle drain.</summary>
    /// <param name="behavior">The behaviour.</param>
    public void Destroy(Behavior behavior) {
        ArgumentNullException.ThrowIfNull(behavior);

        if (behavior.IsDestroyed) {
            return;
        }

        behavior.IsDestroyed = true;
        pendingDestroy.Add(behavior);
    }

    internal void QueueEnabledChange(Behavior behavior, bool enabled) =>
        (enabled ? pendingEnable : pendingDisable).Add(behavior);

    /// <summary>
    ///     Runs every queued lifecycle callback, in the order the design fixes:
    ///     destroy, then <c>Awake</c> all, then <c>OnEnable</c> all, then <c>Start</c> all.
    /// </summary>
    /// <remarks>
    ///     <c>Start</c> is deferred to the drain after the one that ran <c>Awake</c> — doc 04's
    ///     ordering, and the one users write code against: everything in a batch has had
    ///     <c>Awake</c> before anything has had <c>Start</c>, so a <c>Start</c> may look up a
    ///     sibling created in the same frame and find it fully constructed.
    /// </remarks>
    public void RunLifecycle() {
        Reap();

        // Everything queued in an earlier drain becomes eligible now; anything queued during this
        // one waits for the next. That single move is the whole of the one-frame deferral.
        startReady.AddRange(startQueued);
        startQueued.Clear();

        Flush(pendingDestroy, behavior => {
                if (behavior.Enabled && behavior.IsAwake) {
                    behavior.InvokeOnDisable();
                }

                behavior.InvokeOnDestroy();
                Detach(behavior);
            }
        );

        awakening.Clear();
        awakening.AddRange(pendingAwake);
        pendingAwake.Clear();

        foreach (var behavior in awakening) {
            if (!behavior.IsDestroyed) {
                behavior.InvokeAwake();
                behavior.IsAwake = true;
            }
        }

        foreach (var behavior in awakening) {
            if (behavior is { IsDestroyed: false, Enabled: true }) {
                Activate(behavior);
                behavior.InvokeOnEnable();
            }

            if (!behavior.IsDestroyed) {
                startQueued.Add(behavior);
            }
        }

        Flush(pendingEnable, behavior => {
                if (behavior is { IsDestroyed: false, Enabled: true, IsAwake: true }) {
                    Activate(behavior);
                    behavior.InvokeOnEnable();
                }
            }
        );

        Flush(pendingDisable, behavior => {
                if (behavior is { IsDestroyed: false, Enabled: false, IsAwake: true }) {
                    Deactivate(behavior);
                    behavior.InvokeOnDisable();
                }
            }
        );

        for (var index = 0; index < startReady.Count; index++) {
            var behavior = startReady[index];

            if (behavior.IsDestroyed) {
                startReady.RemoveAt(index--);
                continue;
            }

            // A behaviour that is disabled when its turn comes keeps its place in the queue rather
            // than losing Start for ever, which is what Unity does and what anything that spawns
            // disabled and enables later depends on.
            if (!behavior.Enabled) {
                continue;
            }

            behavior.InvokeStart();
            behavior.IsStarted = true;
            startReady.RemoveAt(index--);
        }
    }

    /// <summary>Runs <c>Update</c> on every enabled, started behaviour.</summary>
    /// <remarks>
    ///     An index loop, not a <c>foreach</c>: a behaviour that attaches a behaviour of a type
    ///     nobody has used yet appends a bucket to this list from inside it. A new bucket's contents
    ///     have not started, so visiting it does nothing — which is the right answer, and a
    ///     <c>foreach</c> would have thrown instead.
    /// </remarks>
    public void RunUpdate() {
        for (var index = 0; index < ordered.Count; index++) {
            ordered[index].Update();
        }
    }

    /// <summary>Runs <c>LateUpdate</c> on every enabled, started behaviour.</summary>
    public void RunLateUpdate() {
        for (var index = 0; index < ordered.Count; index++) {
            ordered[index].LateUpdate();
        }
    }

    void Reap() {
        // A behaviour whose entity was destroyed by anything at all — a command buffer, a scene
        // unload, a subtree destroy. Nothing tells the store, and nothing should have to: the ECS
        // has no destruction event that is on by default, and adding one would put a branch in the
        // inner loop of everything to serve this.
        reaped.Clear();

        foreach (var bucket in ordered) {
            bucket.CollectOrphans(world, reaped);
        }

        foreach (var behavior in reaped) {
            Destroy(behavior);
        }
    }

    static void Flush(List<Behavior> queue, Action<Behavior> action) {
        if (queue.Count == 0) {
            return;
        }

        // Copied out first: a callback may queue more of the same kind, and those belong to the next
        // drain rather than to a loop that grows while it runs.
        var pending = queue.ToArray();
        queue.Clear();

        foreach (var behavior in pending) {
            action(behavior);
        }
    }

    void Link(Entity entity, Behavior behavior) {
        if (world.TryGet<BehaviorLink>(entity, out var link) && link.Items is not null) {
            var grown = new Behavior[link.Items.Length + 1];
            Array.Copy(link.Items, grown, link.Items.Length);
            grown[^1] = behavior;
            world.Set(entity, new BehaviorLink { Items = grown });
            return;
        }

        var items = new BehaviorLink { Items = [behavior] };

        if (world.Has<BehaviorLink>(entity)) {
            world.Set(entity, items);
        } else {
            world.Add(entity, items);
        }
    }

    void Detach(Behavior behavior) {
        buckets[behavior.BucketKey].Remove(behavior);

        if (!world.IsAlive(behavior.Entity) || !world.TryGet<BehaviorLink>(behavior.Entity, out var link)) {
            return;
        }

        var remaining = link.Items.Where(item => !ReferenceEquals(item, behavior)).ToArray();

        if (remaining.Length == 0) {
            world.Remove<BehaviorLink>(behavior.Entity);
        } else {
            world.Set(behavior.Entity, new BehaviorLink { Items = remaining });
        }
    }

    void Activate(Behavior behavior) => buckets[behavior.BucketKey].Activate(behavior);

    void Deactivate(Behavior behavior) => buckets[behavior.BucketKey].Deactivate(behavior);

    BehaviorBucket<T> Bucket<T>() where T : Behavior {
        if (buckets.TryGetValue(typeof(T), out var existing)) {
            return (BehaviorBucket<T>)existing;
        }

        var bucket = new BehaviorBucket<T>();
        buckets[typeof(T)] = bucket;
        ordered.Add(bucket);
        return bucket;
    }

    /// <summary>The type-erased half of a bucket, so the store can hold a table of them.</summary>
    interface IBehaviorBucket {
        int Count { get; }

        void Update();

        void LateUpdate();

        void Activate(Behavior behavior);

        void Deactivate(Behavior behavior);

        void Remove(Behavior behavior);

        void CollectOrphans(World world, List<Behavior> into);
    }

    /// <summary>One concrete behaviour type's instances, enabled ones first.</summary>
    sealed class BehaviorBucket<T> : IBehaviorBucket where T : Behavior {
        T[] items = new T[8];
        int enabled;

        public int Count { get; private set; }

        public void Add(T behavior) {
            if (Count == items.Length) {
                Array.Resize(ref items, items.Length * 2);
            }

            // Appended past the enabled prefix, so a batch that is mid-Update is not disturbed:
            // everything the loop can reach keeps the index it had.
            behavior.Slot = Count;
            items[Count++] = behavior;
        }

        public void Update() {
            for (var index = 0; index < enabled; index++) {
                var behavior = items[index];

                if (behavior.IsStarted) {
                    behavior.InvokeUpdate();
                }
            }
        }

        public void LateUpdate() {
            for (var index = 0; index < enabled; index++) {
                var behavior = items[index];

                if (behavior.IsStarted) {
                    behavior.InvokeLateUpdate();
                }
            }
        }

        public void Activate(Behavior behavior) {
            if (behavior.Slot >= enabled) {
                Swap(behavior.Slot, enabled++);
            }
        }

        public void Deactivate(Behavior behavior) {
            if (behavior.Slot < enabled) {
                Swap(behavior.Slot, --enabled);
            }
        }

        public void Remove(Behavior behavior) {
            Deactivate(behavior);
            var slot = behavior.Slot;
            Swap(slot, --Count);
            items[Count] = null!;
        }

        public void CollectOrphans(World world, List<Behavior> into) {
            for (var index = 0; index < Count; index++) {
                if (!items[index].IsDestroyed && !world.IsAlive(items[index].Entity)) {
                    into.Add(items[index]);
                }
            }
        }

        void Swap(int left, int right) {
            if (left == right) {
                return;
            }

            (items[left], items[right]) = (items[right], items[left]);
            items[left].Slot = left;
            items[right].Slot = right;
        }
    }
}
