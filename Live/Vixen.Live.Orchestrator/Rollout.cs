// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>How a fleet moves from one build to another. Doc 27 § Upgrades' six steps.</summary>
/// <remarks>
///     ⚠ <b>Nothing about the mechanism is directional.</b> Rolling back is pointing at the old pair,
///     and it goes through exactly the same states — which is the property that makes a rollback
///     something an operator can do at three in the morning without a second procedure to remember.
/// </remarks>
public enum RolloutState : byte {
    /// <summary>Everything is on one version. Nothing to do.</summary>
    Settled = 0,

    /// <summary>New-version shards are coming up and old ones are draining.</summary>
    Rolling = 1,

    /// <summary>
    ///     Past <see cref="RolloutPolicy.Grace" />. Old-version shards are no longer created at all.
    /// </summary>
    /// <remarks>
    ///     Doc 27 § Upgrades' first bound on fragmentation. A client that has not fetched the catalog
    ///     is sent to the gate's update flow instead of to a shard, which is the moment the rollout
    ///     stops being invisible to anybody still on the old build.
    /// </remarks>
    Forcing = 2
}

/// <summary>The three bounds doc 27 puts on population fragmentation.</summary>
public sealed record RolloutPolicy {
    /// <summary>How long old-version shards keep being created for clients that have not updated.</summary>
    /// <remarks>
    ///     ⚠ <b>Fine for an hour and corrosive for a day</b>, which is why the default is a day and
    ///     not a week. Version-filtered placement means players on the old catalog can only meet
    ///     players on the old catalog; past this, they meet the update flow instead.
    /// </remarks>
    public TimeSpan Grace { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How many old-version shards may be draining at once.</summary>
    /// <remarks>
    ///     ⚠ <b>The one number that stops a rollout being an outage.</b> Draining every old shard at
    ///     once asks every player in the region to transfer inside one window — which is a thundering
    ///     herd against the new-version shards that have not finished starting, and it presents as a
    ///     rollout that "made the game unplayable" rather than as a capacity mistake.
    /// </remarks>
    public int DrainWidth { get; init; } = 2;

    /// <summary>Below this spread the rollout is treated as finished.</summary>
    /// <remarks>
    ///     Exactly zero, deliberately: a rollout that stopped at 2 % would leave a handful of shards
    ///     on the old build for ever, and "for ever" is how a fleet ends up running four versions.
    /// </remarks>
    public double CompleteBelow { get; init; }
}

/// <summary>What a fleet should do next about its version.</summary>
/// <param name="State">Where the rollout is.</param>
/// <param name="Drain">Which shards to start draining now.</param>
/// <param name="Spread">The fraction of shards not on the target — doc 27's watched number.</param>
/// <param name="Explain">Why, in a sentence, for `vixen live status`.</param>
public sealed record RolloutDecision(
    RolloutState State,
    ImmutableArray<ShardId> Drain,
    double Spread,
    string Explain
) {
    /// <summary>Whether the fleet is entirely on the target.</summary>
    public bool Complete => State == RolloutState.Settled;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{State}: spread {Spread:P1} — {Explain}");
}

/// <summary>The rolling upgrade, as a function of the fleet and the clock.</summary>
/// <remarks>
///     <para>
///         A plain class the grain drives, which is the pattern <c>MapCoordinator</c>,
///         <c>ShardLifecycle</c> and <c>PlayerLeaseState</c> established: § Testing asks for
///         <i>"a rollout from version A to B with players in flight, asserting nobody is disconnected
///         and <c>VersionSpread</c> reaches zero"</i>, and that is a test over a list of reports
///         rather than an afternoon with a cluster.
///     </para>
///     <para>
///         ⚠ <b>It never kills anything.</b> Every step it produces is <c>Drain</c>, and a drain
///         moves players out at safe moments (§ Drain). Doc 27 is explicit that nothing is
///         force-disconnected and that an escalation ends in a person; a rollout that could
///         disconnect would be the one live-ops action able to undo that promise.
///     </para>
/// </remarks>
public sealed class Rollout {
    readonly RolloutPolicy policy;

    /// <summary>Starts one.</summary>
    /// <param name="target">The version to move to.</param>
    /// <param name="since">When it was pointed there.</param>
    /// <param name="policy">The three bounds.</param>
    public Rollout(RealmVersion target, DateTimeOffset since, RolloutPolicy? policy = null) {
        Target = target;
        Since = since;

        this.policy = policy ?? new();
    }

    /// <summary>What new shards are started on.</summary>
    public RealmVersion Target { get; private set; }

    /// <summary>When the target was last changed.</summary>
    public DateTimeOffset Since { get; private set; }

    /// <summary>Points the fleet somewhere else. A rollback is this with the old pair.</summary>
    /// <param name="version">The new target.</param>
    /// <param name="now">The cluster's clock.</param>
    /// <remarks>
    ///     ⚠ <b>The grace restarts.</b> A rollback inherits the elapsed grace of the rollout it is
    ///     undoing otherwise, which would put a fleet straight into <see cref="RolloutState.Forcing" />
    ///     against the version everybody is already on — turning a rollback into an outage.
    /// </remarks>
    public void PointAt(RealmVersion version, DateTimeOffset now) {
        if (version == Target) {
            return;
        }

        Target = version;
        Since = now;
    }

    /// <summary>Decides what to do next.</summary>
    /// <param name="shards">Every shard in the region.</param>
    /// <param name="now">The cluster's clock.</param>
    /// <returns>The decision, with its reason attached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shards" /> is null.</exception>
    public RolloutDecision Observe(IReadOnlyCollection<ShardReport> shards, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(shards);

        var live = shards
            .Where(shard => shard.State is ShardState.Ready or ShardState.Starting or ShardState.Requested)
            .ToList();

        if (live.Count == 0) {
            return new(RolloutState.Settled, [], 0, "there are no shards to move");
        }

        var stale = live.Where(shard => shard.Key.Version != Target).ToList();
        var spread = (double)stale.Count / live.Count;

        if (spread <= policy.CompleteBelow) {
            return new(RolloutState.Settled, [], spread, $"every shard is on {Target}");
        }

        var draining = shards.Count(shard => shard.State == ShardState.Draining);
        var room = Math.Max(0, policy.DrainWidth - draining);

        // Emptiest first: a shard with four people on it finishes its drain in a minute and gives its
        // capacity back, where the busiest would hold a slot in the width for an hour. Rolling from
        // the bottom means the fleet's version spread falls fastest for the same number of transfers.
        var next = stale
            .Where(shard => shard.State == ShardState.Ready)
            .OrderBy(shard => shard.Population)
            .ThenBy(shard => shard.Shard.Value)
            .Take(room)
            .Select(shard => shard.Shard)
            .ToImmutableArray();

        var forcing = now - Since >= policy.Grace;

        var explain = room == 0
            ? $"{draining} shard(s) already draining, which is the width"
            : next.Length == 0
                ? "the remaining old-version shards are not ready to drain yet"
                : $"draining {next.Length} of {stale.Count} shard(s) still on an old version";

        return new(
            forcing ? RolloutState.Forcing : RolloutState.Rolling,
            next,
            spread,
            forcing ? explain + $"; past the {policy.Grace.TotalHours:F0} h grace, so no more old-version shards are started" : explain
        );
    }

    /// <summary>Whether a shard may still be started on a version that is not the target.</summary>
    /// <param name="version">Which version.</param>
    /// <param name="now">The cluster's clock.</param>
    /// <returns>Whether to allow it.</returns>
    /// <remarks>
    ///     This is the placement-side half of the grace: inside it, a client that has not fetched the
    ///     catalog is still given somewhere to play; past it, the gate's update flow is the answer.
    /// </remarks>
    public bool Admits(RealmVersion version, DateTimeOffset now) =>
        version == Target || now - Since < policy.Grace;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"rolling to {Target} since {Since:u}");
}
