// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Orchestration;

/// <summary>When a map grows a shard and when it gives one back. Doc 27 § Placement.</summary>
/// <remarks>
///     ⚠ <b>The asymmetry and the dwell are the design, not the tuning.</b> Spawning at the soft cap
///     and merging below a quarter of it, with two minutes of dwell before a merge, is what stops the
///     fleet oscillating — and doc 27 names it as the same lesson <c>InterestChain</c>'s
///     leave-hysteresis is, at a different scale. Setting <see cref="MergeBelow" /> anywhere near 1.0
///     gives a fleet that spawns and merges the same shard for as long as anybody is playing.
/// </remarks>
public sealed record FleetPolicy {
    /// <summary>Doc 27's defaults.</summary>
    public static FleetPolicy Default { get; } = new();

    /// <summary>How far ahead the arrival rate is projected when deciding to spawn.</summary>
    /// <remarks>
    ///     What turns "we are full" into "we will be full by the time a shard has finished starting".
    ///     A shard takes seconds to load its map, so a fleet that only spawned at saturation would
    ///     spend those seconds refusing people.
    /// </remarks>
    public TimeSpan SpawnLeadTime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The shortest gap between two spawns for one map.</summary>
    /// <remarks>
    ///     ⚠ <b>Two hundred people zoning in at once must not produce twenty shards.</b> Every one of
    ///     them sees a fleet with no headroom, because none of the shards being started has reported
    ///     ready yet. The cooldown is what makes the answer "one more, and ask again in a moment".
    /// </remarks>
    public TimeSpan SpawnCooldown { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The window arrivals are counted over to get a rate.</summary>
    /// <remarks>
    ///     ⚠ <b>The same as <see cref="SpawnLeadTime" />, on purpose: measure over the horizon you
    ///     project over.</b> A window twice the lead time keeps predicting growth for a minute after
    ///     a burst has ended — the crowd is still in the window, so the rate stays high, and the
    ///     fleet spawns shards for people who already arrived. <c>FleetSimulation</c>'s flash crowd
    ///     over-provisioned by a whole shard on a sixty-second window and does not on this one.
    /// </remarks>
    public TimeSpan ArrivalWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The shortest span a rate is inferred from.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rate is arrivals over the span they actually landed in, not over
    ///         <see cref="ArrivalWindow" />.</b> Dividing by the nominal window makes a flash crowd
    ///         read as a trickle for its first minute — ten people a second reads as 0.17/s until the
    ///         window fills — so the fleet spawns after saturation instead of before it, and the
    ///         difference is players being refused while capacity they were promised is still
    ///         loading. Found by <c>FleetSimulation</c>'s flash-crowd trace, which refused twenty of
    ///         two hundred.
    ///     </para>
    ///     <para>
    ///         This is the floor under that span, and it is what stops the other failure: a party of
    ///         ten arriving in one instant is not ten a second, and extrapolating from a single
    ///         moment would spawn a shard for every group that zones in together.
    ///     </para>
    /// </remarks>
    public TimeSpan MinimumRateSpan { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The fill, as a fraction of the soft cap, below which a shard is a merge candidate.</summary>
    public double MergeBelow { get; init; } = 0.25;

    /// <summary>How long the merge condition has to hold before anything is drained.</summary>
    public TimeSpan MergeDwell { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How many shards a map may have at once.</summary>
    /// <remarks>
    ///     Not in doc 27, and it belongs here anyway: every elastic system wants a number that says
    ///     "if this is where we have got to, something is wrong and a human should hear about it".
    ///     Reaching it stops the spawning rather than raising an error, because a map at its ceiling
    ///     is still a map full of people playing.
    /// </remarks>
    public int MaxShards { get; init; } = 32;
}

/// <summary>What the fleet should do about a map.</summary>
public enum FleetActionKind : byte {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Start a shard.</summary>
    Spawn = 1,

    /// <summary>Move a shard's players out and stop it. Never a kill — see doc 27 § Drain.</summary>
    Drain = 2
}

/// <summary>One decision about one map.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Shard">Which shard, on <see cref="FleetActionKind.Drain" />.</param>
/// <param name="Reason">Why, in a sentence, for the log and the fleet view.</param>
public readonly record struct FleetAction(FleetActionKind Kind, ShardId Shard, string Reason) {
    /// <summary>Do nothing.</summary>
    public static FleetAction None => new(FleetActionKind.None, ShardId.None, "");

    /// <inheritdoc />
    public override string ToString() =>
        Kind == FleetActionKind.None
            ? "nothing to do"
            : string.Create(CultureInfo.InvariantCulture, $"{Kind} {Shard}: {Reason}");
}

/// <summary>One map's shards over time, and the hysteresis that keeps the count sane.</summary>
/// <remarks>
///     <para>
///         The stateful half of doc 27 § Placement, and it is stateful for exactly two reasons: a
///         spawn is debounced against the last one, and a merge has to have been true for a while.
///         Everything else it decides is a function of the shards it is shown.
///     </para>
///     <para>
///         ⚠ <b>One action per observation, and a spawn beats a drain.</b> A fleet that returned both
///         would be asked to grow and shrink in the same breath; and a map that is saturating while
///         one of its shards is nearly empty wants the shard, not the tidiness.
///     </para>
///     <para>
///         ⚠ <b>The clock is a parameter.</b> Every method takes <c>now</c>, so a test can run a
///         flash crowd, a slow bleed and a sawtooth through half an hour of simulated traffic in
///         milliseconds — which is what doc 27 § Testing asks of this.
///     </para>
/// </remarks>
public sealed class MapFleet {
    readonly Queue<(DateTimeOffset At, int Count)> arrivals = new();
    readonly FleetPolicy policy;

    DateTimeOffset lastSpawn = DateTimeOffset.MinValue;
    DateTimeOffset? mergeableSince;
    int arrivalsInWindow;

    /// <summary>Stands one up.</summary>
    /// <param name="key">Which map, region and version this fleet is for.</param>
    /// <param name="policy">The thresholds, or null for doc 27's defaults.</param>
    public MapFleet(ShardKey key, FleetPolicy? policy = null) {
        Key = key;
        this.policy = policy ?? FleetPolicy.Default;
    }

    /// <summary>What this fleet is for.</summary>
    public ShardKey Key { get; }

    /// <summary>The thresholds in force.</summary>
    public FleetPolicy Policy => policy;

    /// <summary>How many arrivals are inside the window, as of the last call.</summary>
    public int ArrivalsInWindow => arrivalsInWindow;

    /// <summary>Records people arriving, which is what the spawn projection is made of.</summary>
    /// <param name="now">When.</param>
    /// <param name="count">How many.</param>
    public void Arrived(DateTimeOffset now, int count = 1) {
        if (count <= 0) {
            return;
        }

        arrivals.Enqueue((now, count));
        arrivalsInWindow += count;
        Expire(now);
    }

    /// <summary>Arrivals per second, over the span they landed in.</summary>
    /// <param name="now">When.</param>
    /// <returns>The rate.</returns>
    /// <remarks>
    ///     See <see cref="FleetPolicy.MinimumRateSpan" /> for why this is not simply the count over
    ///     the window — it is the difference between spawning before saturation and spawning after.
    /// </remarks>
    public double ArrivalRate(DateTimeOffset now) {
        Expire(now);

        if (arrivalsInWindow == 0 || !arrivals.TryPeek(out var oldest)) {
            return 0;
        }

        var span = now - oldest.At;

        if (span > policy.ArrivalWindow) {
            span = policy.ArrivalWindow;
        }

        if (span < policy.MinimumRateSpan) {
            span = policy.MinimumRateSpan;
        }

        return arrivalsInWindow / span.TotalSeconds;
    }

    /// <summary>Decides what to do about the map, given what it currently is.</summary>
    /// <param name="now">When.</param>
    /// <param name="shards">Every shard of this map, in any state.</param>
    /// <returns>One action, or none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shards" /> is null.</exception>
    public FleetAction Observe(DateTimeOffset now, IReadOnlyList<ShardCandidate> shards) {
        ArgumentNullException.ThrowIfNull(shards);

        Expire(now);

        // Starting shards count against the ceiling and toward the headroom, which is the whole of
        // the debounce's job: a shard that has been asked for is capacity that is coming.
        var pending = shards.Count(shard => shard.State is ShardState.Requested or ShardState.Starting);
        var ready = shards.Where(shard => shard.State == ShardState.Ready).ToList();

        if (Spawns(now, ready, pending) is { Length: > 0 } why) {
            lastSpawn = now;

            // A spawn resets the merge dwell rather than leaving it running. The fleet has just said
            // it is short of capacity; draining a shard two minutes later because it was quiet
            // before the crowd arrived is the oscillation this class exists to prevent.
            mergeableSince = null;

            return new(FleetActionKind.Spawn, ShardId.None, why);
        }

        return Merges(now, ready, shards.Any(shard => shard.State == ShardState.Draining));
    }

    string Spawns(DateTimeOffset now, List<ShardCandidate> ready, int pending) {
        if (ready.Count + pending >= policy.MaxShards) {
            return "";
        }

        if (now - lastSpawn < policy.SpawnCooldown) {
            return "";
        }

        if (ready.Count == 0 && pending == 0) {
            return "the map has no shard";
        }

        // Headroom counts to the SOFT cap, not the hard one. The gap above it is what doc 27 reserves
        // for parties arriving together, and a fleet that spent it on ordinary arrivals would have
        // no room left for the one thing placement scores at ten thousand.
        var headroom = ready.Sum(shard => Math.Max(0, shard.Capacity.SoftCap - shard.Population));

        if (headroom == 0 && pending == 0) {
            return "every shard is at its soft cap";
        }

        var projected = ArrivalRate(now) * policy.SpawnLeadTime.TotalSeconds;

        // Pending shards are counted as a soft cap each: capacity that has been asked for and is on
        // its way. Without this a flash crowd spawns one shard per observation until the first one
        // finishes loading, which is the twenty-shards failure doc 27 names.
        var soft = ready.Count > 0 ? ready[0].Capacity.SoftCap : 0;

        return projected > headroom + (pending * soft)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{ArrivalRate(now):0.##}/s over {policy.SpawnLeadTime.TotalSeconds:0}s exceeds {headroom} free"
            )
            : "";
    }

    FleetAction Merges(DateTimeOffset now, List<ShardCandidate> ready, bool alreadyDraining) {
        var quiet = ready
            .Where(shard => shard.Population < shard.Capacity.SoftCap * policy.MergeBelow)
            .ToList();

        if (ready.Count < 2 || quiet.Count < 2) {
            mergeableSince = null;

            return FleetAction.None;
        }

        mergeableSince ??= now;

        if (now - mergeableSince < policy.MergeDwell) {
            return FleetAction.None;
        }

        // ⚠ One merge in flight at a time, and this is the guard rather than a dwell reset. A drained
        // shard's players have not moved yet at the next observation, so the fill it is about to
        // relieve has not recovered — draining a second one on that evidence would empty the map into
        // one shard in a few seconds.
        //
        // But once the previous merge HAS finished, no new evidence is needed: the map has already
        // been quiet for the dwell. Resetting the dwell after every drain was the first version, and
        // FleetSimulation's sawtooth trace found what it costs — a map that spawns every cycle and
        // merges once every two minutes grows a shard per cycle and never gives it back.
        if (alreadyDraining) {
            return FleetAction.None;
        }

        // The lowest population, and the lowest shard id when two are equal — for determinism, so
        // that the same fleet always retires the same shard and a test can say which.
        var going = quiet
            .OrderBy(shard => shard.Population)
            .ThenBy(shard => shard.Shard.Value)
            .First();

        return new(
            FleetActionKind.Drain,
            going.Shard,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{quiet.Count} shards under {policy.MergeBelow:P0} for {policy.MergeDwell.TotalSeconds:0}s"
            )
        );
    }

    void Expire(DateTimeOffset now) {
        var cutoff = now - policy.ArrivalWindow;

        while (arrivals.TryPeek(out var oldest) && oldest.At < cutoff) {
            arrivals.Dequeue();
            arrivalsInWindow -= oldest.Count;
        }
    }
}
