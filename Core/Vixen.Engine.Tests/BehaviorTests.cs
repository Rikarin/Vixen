// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Frames;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Engine.Tests;

public sealed class BehaviorTests {
    /// <summary>
    ///     The golden ordering test [04](../../../docs/plan/04-ecs-and-scripting.md) § Tests asks
    ///     for: every <c>Awake</c> before every <c>OnEnable</c> before every <c>Start</c>, and
    ///     <c>Start</c> a frame behind <c>Awake</c>.
    /// </summary>
    [Fact]
    public void TheLifecycleRunsInTheOrderTheDesignFixes() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var one = loop.World.Create();
        var other = loop.World.Create();

        loop.Behaviors.Add(one, new Recorder("a", log));
        loop.Behaviors.Add(other, new Recorder("b", log));

        loop.Frame(TimeSpan.FromMilliseconds(16));
        Assert.Equal(["a.Awake", "b.Awake", "a.OnEnable", "b.OnEnable"], log);

        log.Clear();
        loop.Frame(TimeSpan.FromMilliseconds(16));
        Assert.Equal(["a.Start", "b.Start", "a.Update", "b.Update", "a.LateUpdate", "b.LateUpdate"], log);

        log.Clear();
        loop.Frame(TimeSpan.FromMilliseconds(16));
        Assert.Equal(["a.Update", "b.Update", "a.LateUpdate", "b.LateUpdate"], log);
    }

    [Fact]
    public void ABehaviourDoesNotUpdateBeforeItHasStarted() {
        var log = new List<string>();
        using var loop = new EngineLoop();

        loop.Behaviors.Add(loop.World.Create(), new Recorder("a", log));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.DoesNotContain("a.Update", log);
    }

    [Fact]
    public void DisablingABehaviourStopsItsUpdateAndRunsOnDisable() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var behavior = loop.Behaviors.Add(loop.World.Create(), new Recorder("a", log));

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        log.Clear();
        behavior.Enabled = false;
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(["a.OnDisable"], log);
    }

    [Fact]
    public void EnablingItAgainRunsOnEnableAndResumesUpdate() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var behavior = loop.Behaviors.Add(loop.World.Create(), new Recorder("a", log));

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));
        behavior.Enabled = false;
        loop.Frame(TimeSpan.FromMilliseconds(16));

        log.Clear();
        behavior.Enabled = true;
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(["a.OnEnable", "a.Update", "a.LateUpdate"], log);
    }

    /// <summary>
    ///     A behaviour that spawns disabled and is enabled later must still get its <c>Start</c>,
    ///     which means the queue holds it rather than dropping it when its turn comes round early.
    /// </summary>
    [Fact]
    public void ABehaviourThatStartsDisabledStillGetsStartWhenItIsEnabled() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var behavior = loop.Behaviors.Add(loop.World.Create(), new Recorder("a", log));
        behavior.Enabled = false;

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));
        Assert.DoesNotContain("a.Start", log);

        behavior.Enabled = true;
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Contains("a.Start", log);
    }

    [Fact]
    public void DestroyingABehaviourRunsOnDisableThenOnDestroy() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var behavior = loop.Behaviors.Add(loop.World.Create(), new Recorder("a", log));

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        log.Clear();
        behavior.Destroy();
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(["a.OnDisable", "a.OnDestroy"], log);
        Assert.Equal(0, loop.Behaviors.Count);
    }

    /// <summary>
    ///     <c>AllOn</c> reads the entity's <c>BehaviorRef</c>, which is one component however many
    ///     stores share the world — so anything that walks an entity's behaviours and destroys them
    ///     (a scene unload, play mode's teardown) is handed the other stores' behaviours too. Before
    ///     this, <c>Destroy</c> queued whatever it was given and the next drain indexed a bucket the
    ///     store had never had: a <c>KeyNotFoundException</c> out of the middle of an unrelated call.
    /// </summary>
    [Fact]
    public void DestroyingABehaviourFromAnotherStoreIsRefused() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var elsewhere = new BehaviorStore(loop.World);
        var entity = loop.World.Create();
        var theirs = elsewhere.Add(entity, new Recorder("theirs", log));

        Assert.False(loop.Behaviors.Destroy(theirs));

        // The drain that used to throw.
        loop.Frame(TimeSpan.FromMilliseconds(16));

        // And it is still whole: not marked, not detached, still its own store's to destroy.
        Assert.False(theirs.IsDestroyed);
        Assert.DoesNotContain("theirs.OnDestroy", log);
        Assert.Same(theirs, elsewhere.Get<Recorder>(entity));

        elsewhere.RunLifecycle();
        Assert.True(elsewhere.Destroy(theirs));
        elsewhere.RunLifecycle();

        Assert.Contains("theirs.OnDestroy", log);
        Assert.Equal(0, elsewhere.Count);
    }

    /// <summary>
    ///     A behaviour taken off with <c>Remove</c> belongs to nobody, and a later <c>Destroy</c>
    ///     from the store that used to hold it would otherwise detach it a second time — off a
    ///     bucket that no longer has it.
    /// </summary>
    [Fact]
    public void DestroyingADetachedBehaviourIsRefused() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var behavior = loop.Behaviors.Add(loop.World.Create(), new Recorder("a", log));

        Assert.True(loop.Behaviors.Remove(behavior));
        Assert.False(loop.Behaviors.Destroy(behavior));

        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.False(behavior.IsDestroyed);
        Assert.DoesNotContain("a.OnDestroy", log);
    }

    /// <summary>
    ///     Nothing tells the store when an entity dies — the ECS has no destruction event that is on
    ///     by default. The store notices on its own, or a behaviour outlives its entity and its next
    ///     <c>Update</c> throws on a stale handle.
    /// </summary>
    [Fact]
    public void DestroyingTheEntityDestroysItsBehaviours() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var entity = loop.World.Create();
        loop.Behaviors.Add(entity, new Recorder("a", log));

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        log.Clear();
        loop.World.Destroy(entity);
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(["a.OnDisable", "a.OnDestroy"], log);
        Assert.Equal(0, loop.Behaviors.Count);
    }

    [Fact]
    public void ABehaviourAddedDuringUpdateDoesNotDisturbTheLoopWalkingIt() {
        var log = new List<string>();
        using var loop = new EngineLoop();

        for (var index = 0; index < 8; index++) {
            loop.Behaviors.Add(loop.World.Create(), new Recorder($"a{index}", log));
        }

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        var spawner = loop.Behaviors.Add(loop.World.Create(), new Spawner(loop));
        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, spawner.Spawned);
        Assert.Equal(10, loop.Behaviors.Count);
    }

    [Fact]
    public void BehavioursAreFoundByTypeOnTheirEntity() {
        var log = new List<string>();
        using var loop = new EngineLoop();
        var entity = loop.World.Create();

        var recorder = loop.Behaviors.Add(entity, new Recorder("a", log));
        var mover = loop.Behaviors.Add(entity, new Mover());

        Assert.Same(recorder, loop.Behaviors.Get<Recorder>(entity));
        Assert.Same(mover, loop.Behaviors.Get<Mover>(entity));
        Assert.Equal(2, loop.Behaviors.AllOn(entity).Length);
        Assert.Same(mover, recorder.GetBehavior<Mover>());
    }

    [Fact]
    public void ADisabledBehaviourCostsNothingToSkip() {
        var log = new List<string>();
        using var loop = new EngineLoop();

        for (var index = 0; index < 100; index++) {
            var behavior = loop.Behaviors.Add(loop.World.Create(), new Recorder($"a{index}", log));

            if (index % 2 == 0) {
                behavior.Enabled = false;
            }
        }

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        log.Clear();
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(100, log.Count(entry => entry.EndsWith("Update", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- detaching lets go

    /// <summary>
    ///     ⚠ The editor's authored store never drains, so a queue entry it makes is for ever. A
    ///     behaviour added and detached again — every undo of an "add script", every reload — stayed
    ///     in <c>pendingAwake</c>, and with it the type, and with the type the collectible
    ///     <c>PluginLoadContext</c> the type was compiled into. A project that reloads its scripts
    ///     twenty times has twenty assemblies it can never collect.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The assertion is on the instance, not on the load context, and that is deliberate.</b>
    ///     The end of the chain — emit a behaviour into a collectible context, detach it, unload, and
    ///     watch a <see cref="WeakReference" /> to the context die — was written and does show the
    ///     defect: it fails against the store as it was and passes against the store as it is. It is
    ///     not here because it is not stable enough to gate on. An in-process context unload wants a
    ///     quiet process, and a full test run is not one: with the rest of this assembly's classes
    ///     running in parallel it failed about half the time on a fix that is demonstrably correct,
    ///     which is a test that reports the machine's load rather than the code. The instance is the
    ///     first link of the same chain and its collection is deterministic, so that is what is
    ///     asserted; nothing can pin the type if nothing holds an instance of it.
    /// </remarks>
    [Fact]
    public void RemovingABehaviourBeforeItHasWokenLetsGoOfIt() {
        using var world = new World();
        var store = new BehaviorStore(world);
        var weak = AddAndRemove(store, world.Create());

        Collect();

        Assert.False(weak.IsAlive, "the store still holds a detached behaviour");
    }

    /// <summary>The same, one drain later, where the entry is in the <c>Start</c> queue instead.</summary>
    [Fact]
    public void RemovingAWokenBehaviourLetsGoOfIt() {
        using var world = new World();
        var store = new BehaviorStore(world);
        var entity = world.Create();
        var weak = AddWakeAndRemove(store, entity);

        Collect();

        Assert.False(weak.IsAlive, "the store still holds a detached behaviour");
    }

    /// <summary>And with an enable queued but not yet drained.</summary>
    [Fact]
    public void RemovingABehaviourWithAQueuedEnableChangeLetsGoOfIt() {
        using var world = new World();
        var store = new BehaviorStore(world);
        var entity = world.Create();
        var weak = AddToggleAndRemove(store, entity);

        Collect();

        Assert.False(weak.IsAlive, "the store still holds a detached behaviour");
    }

    /// <summary>
    ///     Attaches and detaches in a frame that returns, so the only reference left when the caller
    ///     collects is one the store kept. Inlining this would leave the behaviour in a live stack
    ///     slot and the test would pass whether or not the queues let go.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference AddAndRemove(BehaviorStore store, Entity entity) {
        var behavior = store.Add(entity, new Detachable());

        Assert.True(store.Remove(behavior));
        return new(behavior);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference AddWakeAndRemove(BehaviorStore store, Entity entity) {
        var behavior = store.Add(entity, new Detachable());

        store.RunLifecycle();
        Assert.True(store.Remove(behavior));

        return new(behavior);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference AddToggleAndRemove(BehaviorStore store, Entity entity) {
        var behavior = store.Add(entity, new Detachable());

        store.RunLifecycle();
        behavior.Enabled = false;
        behavior.Enabled = true;
        Assert.True(store.Remove(behavior));

        return new(behavior);
    }

    /// <summary>
    ///     ⚠ The index-shift hazard, written down. The drain walks <c>awakening</c> by index; a
    ///     detach from inside an <c>Awake</c> that shortened the list would move everything after the
    ///     hole down one, and the element immediately after the cursor would never get its callback —
    ///     silently, with nothing to see but a behaviour that did not wake. Ordered here so the
    ///     victim is <i>before</i> the detacher, which is the arrangement that skips.
    /// </summary>
    [Fact]
    public void DetachingASiblingFromInsideAwakeSkipsNobodyElse() {
        var log = new List<string>();
        using var world = new World();
        var store = new BehaviorStore(world);

        store.Add(world.Create(), new Recorder("first", log));

        var victim = store.Add(world.Create(), new Recorder("victim", log));

        store.Add(world.Create(), new Detacher(store, victim, log, true));
        store.Add(world.Create(), new Recorder("last", log));

        store.RunLifecycle();

        // ⚠ `last` is the assertion. It sits after the detacher, and a queue that shortened itself
        // when the victim came out would have moved it under the cursor and never called it.
        Assert.Equal(["first.Awake", "victim.Awake", "detacher.Awake", "last.Awake"], log.Where(IsAwake));

        // And the survivors are whole: still this store's, still on their way to Start.
        log.Clear();
        store.RunLifecycle();

        Assert.Contains("first.Start", log);
        Assert.Contains("last.Start", log);
        Assert.DoesNotContain("victim.Awake", log);
        Assert.Equal(3, store.Count);
    }

    /// <summary>
    ///     The same from a <c>Flush</c> — <c>OnDestroy</c> is the callback most likely to take
    ///     something else off, and the queue it is being drained from was copied out before it ran.
    /// </summary>
    [Fact]
    public void DetachingASiblingFromInsideOnDestroyIsNotADoubleDetach() {
        var log = new List<string>();
        using var world = new World();
        var store = new BehaviorStore(world);
        var victim = store.Add(world.Create(), new Recorder("victim", log));
        var detacher = store.Add(world.Create(), new Detacher(store, victim, log, false));

        store.RunLifecycle();
        store.RunLifecycle();
        Assert.Equal(2, store.Count);

        log.Clear();
        Assert.True(store.Destroy(detacher));
        store.RunLifecycle();

        // The detacher went out through `Destroy` and the victim through `Remove`, from inside the
        // former's `OnDestroy` — so one gets its callback and the other, being merely detached,
        // does not. Neither is left in the store, and neither drains twice.
        Assert.Contains("detacher.OnDestroy", log);
        Assert.DoesNotContain("victim.OnDestroy", log);
        Assert.Equal(0, store.Count);
        Assert.Null(store.Get<Recorder>(victim.Entity));

        // The drain after, which is where a queue entry nobody drained would have surfaced.
        store.RunLifecycle();
    }

    static bool IsAwake(string entry) => entry.EndsWith(".Awake", StringComparison.Ordinal);

    static void Collect() {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ---------------------------------------------------------------- the transform façade

    [Fact]
    public void ABehaviourReachesItsEntitysTransform() {
        using var loop = new EngineLoop();
        var entity = Hierarchy.CreateTransform(loop.World, LocalTransform.At(new(1, 2, 3)));
        var mover = loop.Behaviors.Add(entity, new Mover());

        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(new Vector3(2, 2, 3), loop.World.Read<LocalTransform>(entity).Position);
        Assert.Equal(new Vector3(2, 2, 3), loop.World.Read<WorldTransform>(entity).Position);
        Assert.Equal(1, mover.Moves);
    }

    /// <summary>A behaviour with no callbacks at all, so nothing but the store can be holding it.</summary>
    sealed class Detachable : Behavior;

    /// <summary>Takes another behaviour off from inside one of its own callbacks.</summary>
    sealed class Detacher(BehaviorStore store, Behavior target, List<string> log, bool whenAwake) : Behavior {
        protected override void Awake() {
            log.Add("detacher.Awake");

            if (whenAwake) {
                store.Remove(target);
            }
        }

        protected override void OnDestroy() {
            log.Add("detacher.OnDestroy");

            if (!whenAwake) {
                store.Remove(target);
            }
        }
    }

    sealed class Recorder(string name, List<string> log) : Behavior {
        protected override void Awake() => log.Add($"{name}.Awake");

        protected override void OnEnable() => log.Add($"{name}.OnEnable");

        protected override void Start() => log.Add($"{name}.Start");

        protected override void Update() => log.Add($"{name}.Update");

        protected override void LateUpdate() => log.Add($"{name}.LateUpdate");

        protected override void OnDisable() => log.Add($"{name}.OnDisable");

        protected override void OnDestroy() => log.Add($"{name}.OnDestroy");
    }

    sealed class Spawner(EngineLoop loop) : Behavior {
        public int Spawned { get; private set; }

        protected override void Update() {
            if (Spawned > 0) {
                return;
            }

            Spawned++;
            loop.Behaviors.Add(loop.World.Create(), new Mover());
        }
    }

    sealed class Mover : Behavior {
        public int Moves { get; private set; }

        protected override void Update() {
            if (Moves > 0) {
                return;
            }

            Moves++;
            LocalPosition += Vector3.UnitX;
        }
    }
}
