// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>Why one player went where they went, kept so it can be asked about afterwards.</summary>
/// <param name="Player">Who.</param>
/// <param name="Status">What they were told.</param>
/// <param name="Shard">Where they went, when they went anywhere.</param>
/// <param name="Explanation">
///     The candidate-by-candidate account — the filter that excluded each one and the score of each
///     survivor.
/// </param>
/// <param name="At">When.</param>
public sealed record PlacementRecord(
    PlayerKey Player,
    PlaceStatus Status,
    ShardId Shard,
    string Explanation,
    DateTimeOffset At
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{At:u} {Player} → {Status} {Shard}");
}

/// <summary>The last placement decision per player, bounded. Doc 27 § Diagnostics' `explain`.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The explanation already existed and had nowhere to live, which is the whole reason
///         this type does.</b> <c>PlacementDecision.Explain()</c> has produced the full account since
///         L1 — the filter that excluded each candidate and the score of each survivor — and
///         <c>PlaceResult.Reason</c> carries it back to whoever asked. But that is the only moment it
///         exists: the gate turns it into an HTTP response and it is gone. § Diagnostics asks for
///         <c>explain &lt;player&gt;</c>, which is a question asked <em>after</em> a complaint, by
///         somebody who was not there.
///     </para>
///     <para>
///         ⚠ <b>Bounded, and that is not a detail.</b> This lives in a grain, one per map, in a
///         process that is meant to run for weeks. An unbounded record of every placement is a memory
///         leak with a plausible excuse, and the leak would be worst on exactly the busiest map. What
///         is kept is the <em>last</em> decision per player and at most <see cref="Capacity" /> of
///         those, oldest evicted first — because the question is always "why am I here <em>now</em>",
///         never "where was I on Tuesday".
///     </para>
///     <para>
///         The eviction is by insertion order rather than by recency of lookup: a player nobody has
///         asked about is not more disposable than one somebody has, and an LRU would make the
///         support tool's own reads change what the tool can see next.
///     </para>
/// </remarks>
public sealed class PlacementLog {
    readonly Lock gate = new();
    readonly Dictionary<PlayerKey, PlacementRecord> byPlayer = [];
    readonly Queue<PlayerKey> order = new();

    /// <summary>How many players' decisions are kept.</summary>
    public int Capacity { get; init; } = 256;

    /// <summary>How many are held now.</summary>
    public int Count {
        get {
            lock (gate) {
                return byPlayer.Count;
            }
        }
    }

    /// <summary>Notes a decision, replacing whatever was known about that player.</summary>
    /// <param name="record">It.</param>
    /// <exception cref="ArgumentNullException"><paramref name="record" /> is null.</exception>
    public void Record(PlacementRecord record) {
        ArgumentNullException.ThrowIfNull(record);

        lock (gate) {
            // Only enqueue a player who is not already held, or the queue would grow without bound
            // for one player asking repeatedly — which is precisely what a client retrying a
            // `Starting` answer does, several times a second.
            if (byPlayer.TryAdd(record.Player, record)) {
                order.Enqueue(record.Player);
            } else {
                byPlayer[record.Player] = record;
            }

            while (order.Count > Capacity && order.TryDequeue(out var evicted)) {
                byPlayer.Remove(evicted);
            }
        }
    }

    /// <summary>What this map last decided about somebody.</summary>
    /// <param name="player">Who.</param>
    /// <param name="record">The decision, if it is still held.</param>
    /// <returns>Whether it was.</returns>
    public bool TryGet(PlayerKey player, out PlacementRecord? record) {
        lock (gate) {
            return byPlayer.TryGetValue(player, out record);
        }
    }

    /// <summary>The explanation for somebody, or a sentence saying there is none.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Something printable either way.</returns>
    /// <remarks>
    ///     ⚠ <b>"Nothing is held" and "they were refused" must not read the same.</b> An operator
    ///     seeing an empty answer needs to know whether the fleet has no memory of this player or has
    ///     a memory of turning them away, because those send them to two different places next.
    /// </remarks>
    public string Explain(PlayerKey player) {
        lock (gate) {
            return byPlayer.TryGetValue(player, out var record)
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{record.At:u} — {record.Status}{Environment.NewLine}{record.Explanation}"
                )
                : $"Nothing is held about {player} on this map. Either they were never placed here, or "
                + $"the last {Capacity} placements have pushed theirs out.";
        }
    }

    /// <summary>Everything held, newest first.</summary>
    /// <returns>The records.</returns>
    public IReadOnlyList<PlacementRecord> Recent() {
        lock (gate) {
            return [.. byPlayer.Values.OrderByDescending(record => record.At)];
        }
    }
}
