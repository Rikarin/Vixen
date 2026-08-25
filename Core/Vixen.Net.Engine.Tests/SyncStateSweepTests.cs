// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Frames;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>A behaviour that writes its own state from <c>Update</c>, and never marks itself.</summary>
/// <remarks>
///     The shape the whole feature is for: a game writes a <c>SyncVar</c> from ordinary behaviour
///     code and expects it to arrive. Nothing here calls <c>MarkChanged</c> — that is the point.
/// </remarks>
public sealed class ScoringBehaviour : NetworkBehaviour {
    public PlayerState Sync { get; } = new();

    /// <summary>What to set the score to on the next <c>Update</c>, or null to leave it alone.</summary>
    public int? Pending { get; set; }

    /// <summary>How many times <c>Update</c> has run, so a test can tell the loop actually ran it.</summary>
    public int UpdateCount { get; private set; }

    /// <inheritdoc />
    protected override NetworkModule Build() => Sync;

    /// <inheritdoc />
    protected override void Update() {
        UpdateCount++;

        if (Pending is { } score) {
            Sync.Score.Value = score;
            Pending = null;
        }
    }
}

/// <summary>A behaviour with a list, written from <c>Update</c> and never marked by hand.</summary>
public sealed class LootBehaviour : NetworkBehaviour {
    public SyncList<int> Items { get; }

    readonly LootState state = new();

    public LootBehaviour() => Items = DeclareList(new SyncList<int>(), nameof(Items));

    /// <summary>What to append on the next <c>Update</c>, or null.</summary>
    public int? Pending { get; set; }

    /// <inheritdoc />
    protected override NetworkModule Build() => state;

    /// <inheritdoc />
    protected override void Update() {
        if (Pending is { } item) {
            Items.Add(item);
            Pending = null;
        }
    }

    /// <summary>A module with one field, because a behaviour's root module must have a layout.</summary>
    public sealed class LootState : NetworkModule {
        public SyncVar<int> Version { get; }

        public LootState() => Version = Declare(new SyncVar<int>(0), nameof(Version));
    }
}

/// <summary>
///     <see cref="SyncStateSweepSystem" />: that a write reaches the wire without a hand-written
///     <c>MarkChanged</c>, that it does not without the system, and that the system runs where it
///     says it does.
/// </summary>
/// <remarks>
///     Driven through a real <see cref="EngineLoop" /> rather than a hand-rolled runner, because the
///     claim being tested is about scheduling: a system that only works when a test calls its
///     <c>Update</c> directly is the defect this system exists to fix, one level up.
/// </remarks>
public sealed class SyncStateSweepTests : IDisposable {
    static readonly PlayerId Player = new(1);

    readonly EngineLoop loop = new();
    readonly World client = new("client");
    readonly BehaviorStore clientStore;
    readonly ReplicationRegistry registry = new();
    readonly ReplicationServer sender;
    readonly ReplicationClient receiver;
    readonly byte[] buffer = new byte[8192];

    uint tick = 1;

    public SyncStateSweepTests() {
        clientStore = new(client);
        registry.Register(new SyncStateReplicator<ScoringBehaviour>(loop.Behaviors));
        registry.Register(new SyncListReplicator<LootBehaviour>(loop.Behaviors));
        sender = new(registry);

        var clientRegistry = new ReplicationRegistry();
        clientRegistry.Register(new SyncStateReplicator<ScoringBehaviour>(clientStore));
        clientRegistry.Register(new SyncListReplicator<LootBehaviour>(clientStore));
        receiver = new(clientRegistry);
    }

    public void Dispose() {
        loop.Dispose();
        client.Dispose();
    }

    /// <summary>Without the system, a <c>SyncVar</c> written from <c>Update</c> never leaves the server.</summary>
    /// <remarks>
    ///     ⚠ <b>The sabotage half, and it fails silently in exactly the way that makes it worth a
    ///     test.</b> Nothing throws, nothing is logged, and the server's own copy is correct — the
    ///     entity simply never acquires a <see cref="SyncStateVersion" />, so
    ///     <see cref="SyncStateReplicator{T}" /> does not consider it and the client never hears
    ///     about the object at all.
    /// </remarks>
    [Fact]
    public void WithoutTheSweep_AWriteFromUpdateNeverReachesTheClient() {
        var id = new NetworkId(1);
        var behaviour = Attach<ScoringBehaviour>(id);
        behaviour.Pending = 42;

        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, behaviour.UpdateCount);
        Assert.Equal(42, behaviour.Sync.Score.Value);

        // The server's own state moved and nothing about the entity says so.
        Assert.False(loop.World.Has<SyncStateVersion>(behaviour.Entity));

        Replicate();

        Assert.False(receiver.TryGetEntity(id, out _));
    }

    /// <summary>With it, the same write arrives, and nothing in the game called <c>MarkChanged</c>.</summary>
    [Fact]
    public void WithTheSweep_AWriteFromUpdateReachesTheClient() {
        var sweep = new SyncStateSweepSystem(loop.Behaviors);
        loop.Add(sweep);

        var id = new NetworkId(1);
        var behaviour = Attach<ScoringBehaviour>(id);
        behaviour.Pending = 42;

        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, sweep.MarkedStateCount);
        Assert.True(loop.World.Has<SyncStateVersion>(behaviour.Entity));

        Replicate();

        Assert.True(receiver.TryGetEntity(id, out var mirrored));

        var mirror = clientStore.Get<ScoringBehaviour>(mirrored);

        Assert.NotNull(mirror);
        Assert.Equal(42, mirror.Sync.Score.Value);
    }

    /// <summary>A list appended from <c>Update</c> arrives too, and on its own component.</summary>
    [Fact]
    public void WithTheSweep_AListAppendedFromUpdateReachesTheClient() {
        var sweep = new SyncStateSweepSystem(loop.Behaviors);
        loop.Add(sweep);

        var id = new NetworkId(2);
        var behaviour = Attach<LootBehaviour>(id);
        behaviour.Pending = 7;

        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, sweep.MarkedListCount);

        // The list moved and the state did not, so only the list's counter exists — which is the
        // whole reason the two are different components.
        Assert.True(loop.World.Has<SyncListVersion>(behaviour.Entity));
        Assert.False(loop.World.Has<SyncStateVersion>(behaviour.Entity));

        Replicate();

        Assert.True(receiver.TryGetEntity(id, out var mirrored));

        var mirror = clientStore.Get<LootBehaviour>(mirrored);

        Assert.NotNull(mirror);
        Assert.Equal(7, Assert.Single(mirror.Items));
    }

    /// <summary>Once the change has been captured, further frames mark nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         The failure this guards is not a crash: a sweep that marked unconditionally would bump
    ///         the change version of every networked behaviour every frame, and the delta encoder's
    ///         whole saving — an object that did not change costs nothing — would quietly become an
    ///         object that costs a hash comparison every tick for ever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"Captured" and not "swept" is the condition, and the capture in the middle of
    ///         this test is load-bearing.</b> A field's dirt is cleared by
    ///         <see cref="SyncStateReplicator{T}" /> writing it, so a sweep that runs twice between
    ///         two captures marks twice — deliberately, because that is what makes a capture which
    ///         skipped a frame still see the change.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OnceTheChangeHasBeenCaptured_FurtherFramesMarkNothing() {
        var sweep = new SyncStateSweepSystem(loop.Behaviors);
        loop.Add(sweep);

        var behaviour = Attach<ScoringBehaviour>(new(1));
        behaviour.Pending = 42;

        loop.Frame(TimeSpan.FromMilliseconds(16));
        Replicate();

        Assert.Equal(1, sweep.MarkedStateCount);

        var wasAt = loop.World.Get<SyncStateVersion>(behaviour.Entity).Value;

        loop.Frame(TimeSpan.FromMilliseconds(16));
        Replicate();
        loop.Frame(TimeSpan.FromMilliseconds(16));
        Replicate();

        Assert.Equal(1, sweep.MarkedStateCount);
        Assert.Equal(wasAt, loop.World.Get<SyncStateVersion>(behaviour.Entity).Value);
    }

    /// <summary>Behaviours on entities the wire has never heard of are not visited.</summary>
    /// <remarks>
    ///     The cost claim, asserted rather than described. The query is <c>BehaviorRef</c> and
    ///     <see cref="NetworkId" /> together, so a scene full of behaviours that are not networked
    ///     costs the sweep one archetype test and no per-behaviour work at all.
    /// </remarks>
    [Fact]
    public void ABehaviourOnAnEntityWithNoNetworkId_IsNotVisited() {
        var sweep = new SyncStateSweepSystem(loop.Behaviors);

        var offline = loop.World.Create();
        var networked = loop.World.Create(new NetworkId(1));

        loop.Behaviors.Add<ScoringBehaviour>(offline);
        loop.Behaviors.Add<ScoringBehaviour>(networked);

        sweep.Sweep(loop.World);

        Assert.Equal(1, sweep.LastVisitedCount);
    }

    /// <summary>A store built for another world is refused rather than reporting a clean frame.</summary>
    /// <remarks>
    ///     The mistake is cheap to make — a listen server holds two worlds — and its natural outcome
    ///     is silence: <c>AllOn</c> reads the entity's <c>BehaviorRef</c> out of the store's own
    ///     world, finds nothing for every entity, and the sweep reports that nothing changed, for
    ///     ever. Which is this system's own bug, one level up.
    /// </remarks>
    [Fact]
    public void ASweepOverAWorldTheStoreDoesNotOwn_IsRefused() {
        var sweep = new SyncStateSweepSystem(loop.Behaviors);

        Assert.Throws<ArgumentException>(() => sweep.Sweep(client));
    }

    void Replicate() {
        var at = new Tick(tick);
        sender.Capture(loop.World, at);

        if (sender.TryWriteSnapshot(loop.World, Player, at, buffer, out var snapshot)) {
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(Player, at);
        }

        loop.World.AdvanceVersion();
        tick++;
    }

    /// <summary>Attaches a behaviour and runs the frame that starts it.</summary>
    /// <remarks>
    ///     ⚠ <b>The settling frame is not padding.</b> <c>Start</c> is deferred to the drain after
    ///     the one that ran <c>Awake</c> — doc 04's ordering — and <c>RunUpdate</c> only walks
    ///     started behaviours, so a behaviour attached and immediately framed has not had its
    ///     <c>Update</c> called even once. Every test here sets its value <i>after</i> this returns,
    ///     so the frame under test is one in which the behaviour is fully running.
    /// </remarks>
    T Attach<T>(NetworkId id) where T : NetworkBehaviour, new() {
        var entity = loop.World.Create(id);
        var behaviour = loop.Behaviors.Add<T>(entity);
        behaviour.State.Seal();
        behaviour.IsServer = true;

        loop.Frame(TimeSpan.FromMilliseconds(16));

        return behaviour;
    }
}

/// <summary>
///     Where <see cref="SyncStateSweepSystem" /> lands in a frame, asserted off the scheduler rather
///     than described in a comment.
/// </summary>
/// <remarks>
///     ⚠ <b>Getting this wrong is invisible.</b> A sweep placed before the behaviour passes marks
///     the previous frame's writes, so everything still arrives and everything arrives one frame
///     late — which reads as interpolation being a little heavy, not as a bug, and would survive
///     every other test in this file.
/// </remarks>
public sealed class SyncStateSweepOrderTests {
    /// <summary>It is in <c>LateUpdate</c>, after the last pass that can set a <c>SyncVar</c>.</summary>
    [Fact]
    public void TheSweepRunsAfterTheLastBehaviourPass() {
        using var loop = new EngineLoop();
        loop.Add(new SyncStateSweepSystem(loop.Behaviors));

        var late = loop.Systems.Graph.InPhase(SystemPhase.LateUpdate);
        var behaviours = IndexOf(late, typeof(BehaviorLateUpdateSystem));
        var sweep = IndexOf(late, typeof(SyncStateSweepSystem));

        Assert.True(behaviours >= 0, "BehaviorLateUpdateSystem is not in LateUpdate any more.");
        Assert.True(sweep >= 0, "The sweep is not in LateUpdate any more.");
        Assert.True(behaviours < sweep, "The sweep runs before the behaviours it is meant to sweep after.");
    }

    /// <summary>Registration order does not decide it; the attribute does.</summary>
    /// <remarks>
    ///     Registered first, which without the <c>[UpdateAfter]</c> would put it first — the graph
    ///     breaks ties by registration order, so a tie is exactly what this must not be.
    /// </remarks>
    [Fact]
    public void RegisteringItFirstDoesNotMoveIt() {
        var plan = SystemGraph.Plan([typeof(SyncStateSweepSystem), typeof(BehaviorLateUpdateSystem)]);
        var late = plan.InPhase(SystemPhase.LateUpdate);

        Assert.Equal(
            [nameof(BehaviorLateUpdateSystem), nameof(SyncStateSweepSystem)],
            late.Select(placement => placement.Name)
        );

        // ⚠ An `[UpdateAfter]` naming a system that is not in the set is dropped in silence. An
        // empty `Unsatisfied` is what says the edge above is real rather than a typo that happened
        // to sort the right way.
        Assert.Empty(plan.Unsatisfied);
    }

    static int IndexOf(IReadOnlyList<SystemNode> nodes, Type system) {
        for (var index = 0; index < nodes.Count; index++) {
            if (nodes[index].System.GetType() == system) {
                return index;
            }
        }

        return -1;
    }
}
