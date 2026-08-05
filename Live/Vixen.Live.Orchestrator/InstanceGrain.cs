// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>One saved instance, as a state machine a test can drive.</summary>
/// <remarks>
///     <para>
///         Grains over state machines, a sixth time. No lock, because
///         <see cref="InstanceGrain" /> takes one turn at a time.
///     </para>
///     <para>
///         ⚠ <b>There is no clock in this type.</b> Every method that needs to know whether the
///         lockout has lifted is handed the time, for <c>HousePlot</c>'s reason one tier up: a grain
///         that ticked would be a grain the cluster has to keep activated, and an instance nobody has
///         asked about for a week is one whose reset can be noticed the next time somebody does.
///     </para>
///     <para>
///         ⚠ <b>The expiry is an absolute boundary the caller computed.</b> Doc 28's
///         <c>LockoutPolicy.NextResetAfter</c> is the only place a reset time comes from — a timer
///         from when somebody entered would drift every player's reset to wherever their first run
///         happened to fall, and a guild that raids on Wednesdays would find half its roster locked.
///     </para>
/// </remarks>
public sealed class InstanceState {
    readonly List<InstanceBinding> bindings = [];
    readonly HashSet<string> defeated = new(StringComparer.Ordinal);
    readonly HashSet<PlayerKey> roster = [];

    /// <summary>Which instance, by address. Empty until opened.</summary>
    public string Instance { get; private set; } = "";

    /// <summary>Which difficulty.</summary>
    public string Difficulty { get; private set; } = "";

    /// <summary>How many may be bound.</summary>
    public int Capacity { get; private set; }

    /// <summary>When it was opened.</summary>
    public DateTimeOffset Opened { get; private set; }

    /// <summary>When the lockout lifts.</summary>
    public DateTimeOffset Expires { get; private set; }

    /// <summary>Whether it has ended.</summary>
    public bool Closed { get; private set; }

    /// <summary>How many times it has changed.</summary>
    public uint Revision { get; private set; }

    /// <summary>How many are bound.</summary>
    public int Count => bindings.Count;

    /// <summary>Whether it has been opened.</summary>
    public bool Exists => Instance.Length > 0;

    /// <summary>What it looks like.</summary>
    /// <returns>The record.</returns>
    public InstanceRecord Read() =>
        Exists
            ? new(
                Instance,
                Difficulty,
                [.. bindings],
                [.. defeated.Order(StringComparer.Ordinal)],
                Opened,
                Expires,
                Closed,
                Revision
            )
            : InstanceRecord.None;

    /// <summary>Whether somebody is saved to it.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they are.</returns>
    public bool IsBound(PlayerKey player) => bindings.Any(binding => binding.Player == player);

    /// <summary>Whether something is dead.</summary>
    /// <param name="encounter">Which, by address.</param>
    /// <returns>Whether it is.</returns>
    public bool IsDefeated(string encounter) => defeated.Contains(encounter);

    /// <summary>Opens it.</summary>
    /// <param name="instance">Which instance.</param>
    /// <param name="difficulty">Which difficulty.</param>
    /// <param name="access">Who may enter. Empty admits anybody.</param>
    /// <param name="capacity">How many may be bound.</param>
    /// <param name="now">The clock.</param>
    /// <param name="expires">When the lockout lifts.</param>
    /// <returns>The outcome.</returns>
    public InstanceOutcome Open(
        string instance,
        string difficulty,
        ImmutableArray<PlayerKey> access,
        int capacity,
        DateTimeOffset now,
        DateTimeOffset expires
    ) {
        if (Exists) {
            return Outcome(InstanceWrite.Open);
        }

        if (string.IsNullOrEmpty(instance)) {
            return Outcome(InstanceWrite.NotOpen);
        }

        Instance = instance;
        Difficulty = difficulty ?? "";
        Capacity = Math.Max(1, capacity);
        Expires = expires;
        Opened = now;

        foreach (var player in access) {
            if (player.IsValid) {
                roster.Add(player);
            }
        }

        return Changed();
    }

    /// <summary>Saves somebody to it.</summary>
    /// <param name="player">Who.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The outcome.</returns>
    public InstanceOutcome Bind(PlayerKey player, DateTimeOffset now) {
        if (!Exists) {
            return Outcome(InstanceWrite.NotOpen);
        }

        if (!player.IsValid) {
            return Outcome(InstanceWrite.NotAdmitted);
        }

        // Already saved. A retried bind, which is ordinary — and it must not be a second row, or the
        // capacity check starts counting one player twice.
        if (IsBound(player)) {
            return Outcome(InstanceWrite.Unchanged);
        }

        if (Closed || now >= Expires) {
            return Outcome(InstanceWrite.Expired);
        }

        // An empty roster admits anybody, which is what a public dungeon finder wants and what an
        // access list is the exception to.
        if (roster.Count > 0 && !roster.Contains(player)) {
            return Outcome(InstanceWrite.NotAdmitted);
        }

        if (bindings.Count >= Capacity) {
            return Outcome(InstanceWrite.Full);
        }

        bindings.Add(new(player, now));

        return Changed();
    }

    /// <summary>Records that something is dead.</summary>
    /// <param name="encounter">Which.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     ⚠ <b>Idempotent on the encounter, and a boss reported twice is <c>Unchanged</c> rather
    ///     than an error.</b> A realm whose grain call was lost retries, and the retry must not be a
    ///     second kill — which for a loot-bearing encounter is the duplication this whole layer
    ///     exists to prevent.
    /// </remarks>
    public InstanceOutcome Defeat(string encounter, DateTimeOffset now) {
        if (!Exists) {
            return Outcome(InstanceWrite.NotOpen);
        }

        if (string.IsNullOrEmpty(encounter)) {
            return Outcome(InstanceWrite.NotOpen);
        }

        if (Closed || now >= Expires) {
            return Outcome(InstanceWrite.Expired);
        }

        return defeated.Add(encounter) ? Changed() : Outcome(InstanceWrite.Unchanged);
    }

    /// <summary>Ends it early.</summary>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     ⚠ <b>Nobody's lockout is released.</b> The shard goes away; the save does not, until its
    ///     reset. Otherwise disbanding is how a group runs a raid twice.
    /// </remarks>
    public InstanceOutcome Close() {
        if (!Exists) {
            return Outcome(InstanceWrite.NotOpen);
        }

        if (Closed) {
            return Outcome(InstanceWrite.Unchanged);
        }

        Closed = true;

        return Changed();
    }

    /// <summary>Puts a saved instance back, as it was, with no checks.</summary>
    /// <param name="saved">What it was.</param>
    /// <param name="capacity">How many it allows now.</param>
    /// <param name="access">Who may enter, which is not stored on the record because it is the group's rather than the instance's.</param>
    public void Restore(InstanceRecord saved, int capacity, ImmutableArray<PlayerKey> access) {
        ArgumentNullException.ThrowIfNull(saved);

        bindings.Clear();
        defeated.Clear();
        roster.Clear();

        Instance = saved.Instance;
        Difficulty = saved.Difficulty;
        Capacity = Math.Max(1, capacity);
        Opened = saved.Opened;
        Expires = saved.Expires;
        Closed = saved.Closed;
        Revision = saved.Revision;

        bindings.AddRange(saved.Bindings);

        foreach (var encounter in saved.Defeated) {
            defeated.Add(encounter);
        }

        foreach (var player in access) {
            if (player.IsValid) {
                roster.Add(player);
            }
        }
    }

    InstanceOutcome Changed() {
        Revision++;

        return new(InstanceWrite.Applied, Revision);
    }

    InstanceOutcome Outcome(InstanceWrite write) => new(write, Revision);
}

/// <summary>One saved instance, keyed by its own guid.</summary>
public sealed class InstanceGrain : Grain, IInstanceGrain {
    readonly InstanceState instance = new();

    /// <inheritdoc />
    public Task<InstanceRecord> Read() => Task.FromResult(instance.Read());

    /// <inheritdoc />
    public Task<InstanceOutcome> Open(
        string instance,
        string difficulty,
        ImmutableArray<PlayerKey> roster,
        int capacity,
        DateTimeOffset now,
        DateTimeOffset expires
    ) =>
        Task.FromResult(this.instance.Open(instance, difficulty, roster, capacity, now, expires));

    /// <inheritdoc />
    public Task<InstanceOutcome> Bind(PlayerKey player, DateTimeOffset now) =>
        Task.FromResult(instance.Bind(player, now));

    /// <inheritdoc />
    public Task<InstanceOutcome> Defeat(string encounter, DateTimeOffset now) =>
        Task.FromResult(instance.Defeat(encounter, now));

    /// <inheritdoc />
    public Task<InstanceOutcome> Close() => Task.FromResult(instance.Close());
}
