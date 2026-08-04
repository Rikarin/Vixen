// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;

namespace Vixen.Live.Orchestration;

/// <summary>One map's fleet and roster: the thing that turns counts into a placement.</summary>
/// <remarks>
///     <para>
///         Slice one built <see cref="PlacementDirector" /> as a pure function of <em>counts</em> —
///         how many of the requester's party, guild and friends are on each candidate — and left
///         open who computes them. This is the answer: the map keeps the affinity attributes of the
///         players on it, so scoring never touches a database and the property tests stay possible.
///     </para>
///     <para>
///         ⚠ <b>A roster of attributes, not of people.</b> What is kept per player is their party,
///         their guild, their language and which shard they are on — four fields, supplied by the
///         gate at placement time. Keeping anything more would make this a cache of the account
///         database, with the invalidation problem that implies.
///     </para>
///     <para>
///         ⚠ <b>Friends are not counted yet, and the term is therefore always zero.</b> A friend list
///         is a social-graph query the gate owns and doc 27 puts on the service plane; the weight and
///         the plumbing are here so that supplying it later is a parameter rather than a redesign.
///     </para>
/// </remarks>
public sealed class MapCoordinator {
    readonly Dictionary<ShardId, ShardReport> shards = [];
    readonly Dictionary<PlayerKey, Occupant> occupants = [];
    readonly PlacementDirector director;
    readonly MapFleet fleet;

    /// <summary>Stands one up.</summary>
    /// <param name="key">Which map, region and version pair.</param>
    /// <param name="weights">The game's placement weights, or null for doc 27's defaults.</param>
    /// <param name="policy">The fleet's thresholds, or null for doc 27's defaults.</param>
    public MapCoordinator(ShardKey key, PlacementWeights? weights = null, FleetPolicy? policy = null) {
        Key = key;
        director = new(weights);
        fleet = new(key, policy);
    }

    /// <summary>What this map is.</summary>
    public ShardKey Key { get; }

    /// <summary>Every shard, in any state.</summary>
    public IReadOnlyCollection<ShardReport> Shards => shards.Values;

    /// <summary>How many players the map believes it is holding.</summary>
    public int Population => occupants.Count;

    /// <summary>The last placement's full argument, for <c>vixen live explain</c>.</summary>
    public PlacementDecision? LastDecision { get; private set; }

    /// <summary>Puts a player somewhere, or says a shard is on its way.</summary>
    /// <param name="request">Who is asking.</param>
    /// <param name="now">The cluster's clock.</param>
    /// <returns>Where they are going.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public PlaceResult Place(PlaceRequest request, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(request);

        fleet.Arrived(now);

        var decision = director.Place(Ask(request), Candidates(request, now));

        LastDecision = decision;

        if (decision.Outcome == PlacementOutcome.Placed) {
            occupants[request.Player] = new(decision.Shard, request.Party, request.Guild, request.Locale);

            // Counted here rather than waiting for the shard's next heartbeat. Two hundred people
            // zoning in inside one heartbeat interval would otherwise all be scored against a
            // population of zero and all be sent to the same shard — the fill term would be reading
            // two seconds of history at the exact moment it matters most.
            Bump(decision.Shard, +1);

            return new(PlaceStatus.Placed, decision.Shard, decision.Endpoint, decision.Explain());
        }

        // Nowhere yet. Whether that is a wait or a refusal is the difference between a client showing
        // a progress bar and a client showing an error, so it is answered rather than inferred.
        var coming = shards.Values.Count(shard => shard.State is ShardState.Requested or ShardState.Starting);

        return coming > 0
            ? new(PlaceStatus.Starting, ShardId.None, RealmEndpoint.None, $"{coming} shard(s) starting")
            : new(PlaceStatus.Refused, ShardId.None, RealmEndpoint.None, decision.Explain());
    }

    /// <summary>Records what a shard now is.</summary>
    /// <param name="report">The shard.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report" /> is null.</exception>
    public void ShardChanged(ShardReport report) {
        ArgumentNullException.ThrowIfNull(report);

        if (report.State is ShardState.Stopped or ShardState.Lost or ShardState.Failed) {
            shards.Remove(report.Shard);

            // ⚠ Everybody on a lost shard is forgotten, and that is not a leak — it is doc 27
            // § Health's "recovery is a placement, not a resurrection". Their volatile state went
            // with the process; they will be placed again from scratch, and a roster that remembered
            // them would score a shard that no longer exists.
            foreach (var gone in occupants.Where(entry => entry.Value.Shard == report.Shard).ToList()) {
                occupants.Remove(gone.Key);
            }

            return;
        }

        shards[report.Shard] = report;
    }

    /// <summary>Forgets a player who has left a shard.</summary>
    /// <param name="player">Who.</param>
    /// <param name="shard">Where from.</param>
    public void PlayerLeft(PlayerKey player, ShardId shard) {
        if (occupants.TryGetValue(player, out var occupant) && occupant.Shard == shard) {
            occupants.Remove(player);
            Bump(shard, -1);
        }
    }

    /// <summary>One turn of the spawn and merge heuristics.</summary>
    /// <param name="now">The cluster's clock.</param>
    /// <returns>What the fleet decided.</returns>
    public FleetAction Tick(DateTimeOffset now) => fleet.Observe(now, Snapshot(now));

    /// <summary>Arrivals per second, as the projection sees them.</summary>
    /// <param name="now">The cluster's clock.</param>
    /// <returns>The rate.</returns>
    public double ArrivalRate(DateTimeOffset now) => fleet.ArrivalRate(now);

    PlacementRequest Ask(PlaceRequest request) =>
        new() {
            Player = request.Player,
            Key = Key,
            Party = request.Party,
            Guild = request.Guild,
            Locale = request.Locale,
            CameFrom = request.CameFrom
        };

    IReadOnlyList<ShardCandidate> Candidates(PlaceRequest request, DateTimeOffset now) {
        var candidates = new List<ShardCandidate>(shards.Count);

        foreach (var shard in shards.Values) {
            var party = 0;
            var guild = 0;
            var locales = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var occupant in occupants.Values) {
                if (occupant.Shard != shard.Shard) {
                    continue;
                }

                if (request.Party != Guid.Empty && occupant.Party == request.Party) {
                    party++;
                }

                if (request.Guild != Guid.Empty && occupant.Guild == request.Guild) {
                    guild++;
                }

                if (occupant.Locale.Length > 0) {
                    locales[occupant.Locale] = locales.GetValueOrDefault(occupant.Locale) + 1;
                }
            }

            candidates.Add(
                new() {
                    Shard = shard.Shard,
                    Key = shard.Key,
                    State = shard.State,
                    Endpoint = shard.Endpoint,
                    Population = shard.Population,
                    Capacity = shard.Capacity,
                    Age = now - shard.StartedAt,
                    PartyMembers = party,
                    GuildMembers = guild,

                    // The majority language, which is what "same language" can honestly mean about a
                    // shard holding a hundred people. A shard nobody has declared a locale on scores
                    // the term for nobody, which is right: it is not everybody's shard, it is
                    // nobody's yet.
                    Locale = locales.Count == 0
                        ? ""
                        : locales.OrderByDescending(entry => entry.Value)
                            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                            .First()
                            .Key
                }
            );
        }

        return candidates;
    }

    IReadOnlyList<ShardCandidate> Snapshot(DateTimeOffset now) =>
        [
            .. shards.Values.Select(shard => new ShardCandidate {
                    Shard = shard.Shard,
                    Key = shard.Key,
                    State = shard.State,
                    Endpoint = shard.Endpoint,
                    Population = shard.Population,
                    Capacity = shard.Capacity,
                    Age = now - shard.StartedAt
                }
            )
        ];

    void Bump(ShardId shard, int by) {
        if (shards.TryGetValue(shard, out var report)) {
            shards[shard] = report with { Population = Math.Max(0, report.Population + by) };
        }
    }

    readonly record struct Occupant(ShardId Shard, Guid Party, Guid Guild, string Locale);
}
