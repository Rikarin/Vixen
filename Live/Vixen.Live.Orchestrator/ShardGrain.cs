// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>What makes a shard <see cref="ShardState.Lost" /> rather than merely quiet.</summary>
/// <param name="Interval">How often a realm is expected to say something.</param>
/// <param name="MissesBeforeLost">
///     How many it may miss. Doc 27 § Grains says three, which at the default interval is six
///     seconds — long enough to survive a garbage collection and short enough that a crashed shard's
///     players are placed again before they give up.
/// </param>
/// <param name="Now">The clock.</param>
public sealed record HealthOptions(TimeSpan Interval, int MissesBeforeLost, Func<DateTimeOffset> Now) {
    /// <summary>Doc 27's defaults.</summary>
    public static HealthOptions Default { get; } = new(TimeSpan.FromSeconds(2), 3, () => DateTimeOffset.UtcNow);

    /// <summary>How long silence has to last before a shard is declared lost.</summary>
    public TimeSpan Patience => Interval * MissesBeforeLost;
}

/// <summary>One realm process's life, as the cluster records it. Doc 27 § Grains' spine.</summary>
/// <remarks>
///     <para>
///         <c>Requested → Starting → Ready → Draining → Stopping → Stopped</c>, with <c>Failed</c>
///         and <c>Lost</c> off the side. Each transition is one method, and the ones that are not
///         edges are refused — a shard going <c>Stopped → Ready</c> would be a process the cluster
///         had already written off answering a heartbeat, which is a real thing that happens when a
///         supervisor restarts something it should not have.
///     </para>
///     <para>
///         ⚠ <b>Draining is one-way, and that is what makes a rollout finish.</b> Doc 27 § Upgrades
///         drains old-version shards and waits; a shard that could be talked out of draining would
///         make the rollout's completion a race rather than an invariant.
///     </para>
/// </remarks>
public sealed class ShardLifecycle {
    readonly HealthOptions options;

    ShardKey key;
    ShardCapacity capacity;
    RealmEndpoint endpoint;
    RealmInstanceId instance;
    int population;
    DateTimeOffset startedAt;
    DateTimeOffset lastHeartbeat;

    /// <summary>Stands one up.</summary>
    /// <param name="shard">Which shard this is.</param>
    /// <param name="options">What counts as healthy, or null for doc 27's defaults.</param>
    public ShardLifecycle(ShardId shard, HealthOptions? options = null) {
        Shard = shard;
        this.options = options ?? HealthOptions.Default;
        startedAt = this.options.Now();
        lastHeartbeat = startedAt;
    }

    /// <summary>Which shard.</summary>
    public ShardId Shard { get; }

    /// <summary>Where it is in its life.</summary>
    public ShardState State { get; private set; } = ShardState.Requested;

    /// <summary>A decision that this shard should exist.</summary>
    /// <param name="shardKey">What it is for.</param>
    /// <param name="shardCapacity">How full it may get.</param>
    public void Requested(ShardKey shardKey, ShardCapacity shardCapacity) {
        key = shardKey;
        capacity = shardCapacity;
        State = ShardState.Requested;
        startedAt = options.Now();
        lastHeartbeat = startedAt;
    }

    /// <summary>The placement backend created something.</summary>
    /// <param name="realmInstance">Its handle.</param>
    /// <param name="at">Where it will be.</param>
    public void Starting(RealmInstanceId realmInstance, RealmEndpoint at) {
        if (State != ShardState.Requested) {
            return;
        }

        instance = realmInstance;
        endpoint = at;
        State = ShardState.Starting;
        lastHeartbeat = options.Now();
    }

    /// <summary>The realm loaded its map and is accepting sessions.</summary>
    /// <param name="at">Where it actually bound.</param>
    /// <remarks>
    ///     The realm's word wins over what it was told to bind: they agree in every ordinary case,
    ///     and where they do not, the realm is right because it is the one holding the socket.
    ///     <para>
    ///         ⚠ A <c>Ready</c> from a shard the cluster has stopped or lost is ignored. That is a
    ///         process which came back from the dead — a supervisor that restarted it, a partition
    ///         that healed after its players were placed elsewhere — and doc 27 § Health is explicit
    ///         that recovery is a placement rather than a resurrection.
    ///     </para>
    /// </remarks>
    public void Ready(RealmEndpoint at) {
        if (State is not (ShardState.Starting or ShardState.Requested)) {
            return;
        }

        endpoint = at.IsValid ? at : endpoint;
        State = ShardState.Ready;
        lastHeartbeat = options.Now();
    }

    /// <summary>Records a heartbeat and answers with the state the realm should be in.</summary>
    /// <param name="sample">What the shard is costing.</param>
    /// <returns>What the cluster thinks this shard is.</returns>
    /// <remarks>
    ///     ⚠ <b>The answer is the point.</b> A realm learns it should be draining from the reply to a
    ///     heartbeat it was sending anyway, so nothing in the control plane ever needs to call
    ///     <em>into</em> a realm — a whole direction of connectivity, authentication and firewall
    ///     rules that does not have to exist.
    /// </remarks>
    public ShardState Heartbeat(ShardHeartbeat sample) {
        ArgumentNullException.ThrowIfNull(sample);

        if (State is ShardState.Stopped or ShardState.Lost or ShardState.Failed) {
            return State;
        }

        population = sample.Population;
        lastHeartbeat = options.Now();

        return State;
    }

    /// <summary>Stop taking arrivals and move everyone out at safe moments.</summary>
    public void Drain() {
        if (State is ShardState.Ready or ShardState.Starting or ShardState.Requested) {
            State = ShardState.Draining;
        }
    }

    /// <summary>It ended the way it was asked to.</summary>
    public void Stopped() {
        State = ShardState.Stopped;
        population = 0;
    }

    /// <summary>It went away without being asked.</summary>
    /// <remarks>
    ///     <see cref="ShardState.Failed" /> and <see cref="ShardState.Lost" /> are the same event at
    ///     different ages: a shard that never reported ready never came up, and one that had is gone.
    ///     Telling them apart is what makes "the map is broken" distinguishable from "that machine
    ///     died" in a fleet view.
    /// </remarks>
    public void Lost() {
        State = State is ShardState.Requested or ShardState.Starting ? ShardState.Failed : ShardState.Lost;
        population = 0;
    }

    /// <summary>Declares a silent shard lost, if it has been silent long enough.</summary>
    /// <returns>Whether this call changed anything.</returns>
    /// <remarks>
    ///     Driven from the map's tick rather than from a timer of its own: a thousand shard grains
    ///     each holding a two-second timer is a thousand wake-ups a second across the cluster, to
    ///     answer a question their map is already asking on their behalf.
    /// </remarks>
    public bool Expire() {
        if (State is not (ShardState.Ready or ShardState.Draining or ShardState.Stopping)) {
            return false;
        }

        if (options.Now() - lastHeartbeat < options.Patience) {
            return false;
        }

        State = ShardState.Lost;
        population = 0;

        return true;
    }

    /// <summary>What this shard is, for anybody who asks.</summary>
    /// <returns>The report.</returns>
    public ShardReport Report() =>
        new(Shard, key, State, endpoint, instance, population, capacity, startedAt, lastHeartbeat);
}

/// <summary>The grain around <see cref="ShardLifecycle" />.</summary>
public sealed class ShardGrain : Grain, IShardGrain {
    readonly HealthOptions? options;

    ShardLifecycle? shard;

    /// <summary>Stands one up.</summary>
    /// <param name="options">What counts as healthy, or null for doc 27's defaults.</param>
    public ShardGrain(HealthOptions? options = null) => this.options = options;

    /// <summary>
    ///     The lifecycle, built on first use because the grain key is not available in a constructor.
    /// </summary>
    ShardLifecycle Lifecycle => shard ??= new(new(this.GetPrimaryKey()), options);

    /// <inheritdoc />
    public Task<ShardReport> Report() {
        Lifecycle.Expire();

        return Task.FromResult(Lifecycle.Report());
    }

    /// <inheritdoc />
    public Task Requested(ShardKey key, ShardCapacity capacity) {
        Lifecycle.Requested(key, capacity);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Starting(RealmInstanceId instance, RealmEndpoint endpoint) {
        Lifecycle.Starting(instance, endpoint);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Ready(RealmEndpoint endpoint) {
        Lifecycle.Ready(endpoint);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ShardState> Heartbeat(ShardHeartbeat sample) => Task.FromResult(Lifecycle.Heartbeat(sample));

    /// <inheritdoc />
    public Task Drain(string reason) {
        Lifecycle.Drain();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stopped() {
        Lifecycle.Stopped();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Lost(string detail) {
        Lifecycle.Lost();

        return Task.CompletedTask;
    }
}
