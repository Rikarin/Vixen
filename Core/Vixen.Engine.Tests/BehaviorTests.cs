// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
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
