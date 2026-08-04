// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Orchestration;

/// <summary>Why a candidate was not scored at all. Doc 27 § Placement's hard filters.</summary>
/// <remarks>
///     Separate values rather than one "excluded", because the whole use of this type is answering
///     "why did I not end up with my guild" — and "the shard your guild is on is running last week's
///     build" and "the shard your guild is on is full" are different conversations.
/// </remarks>
public enum PlacementFilter : byte {
    /// <summary>It was scored.</summary>
    None = 0,

    /// <summary>A different map.</summary>
    Map = 1,

    /// <summary>A different latency zone.</summary>
    Region = 2,

    /// <summary>A different build (ADR-022).</summary>
    Build = 3,

    /// <summary>A different catalog. This is the one a client that has not updated hits.</summary>
    Content = 4,

    /// <summary>Not <see cref="ShardState.Ready" /> — starting, draining, or gone.</summary>
    NotReady = 5,

    /// <summary>At its hard cap.</summary>
    Full = 6,

    /// <summary>Its access list does not admit this player.</summary>
    Access = 7
}

/// <summary>One term of a score, named so that a total can be read rather than trusted.</summary>
/// <param name="Name">What it is — <c>party</c>, <c>guild</c>, <c>fill</c>, <c>antiflap</c>.</param>
/// <param name="Value">What it contributed. Negative terms are penalties.</param>
public readonly record struct ScoreTerm(string Name, double Value) {
    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} {Value:+0.##;-0.##;0}");
}

/// <summary>What placement decided about one candidate, and why.</summary>
/// <param name="Shard">Which candidate.</param>
/// <param name="Excluded">The filter that rejected it, or <see cref="PlacementFilter.None" />.</param>
/// <param name="Score">Its total, when it was scored.</param>
/// <param name="Terms">Where the total came from.</param>
public sealed record CandidateVerdict(
    ShardId Shard,
    PlacementFilter Excluded,
    double Score,
    IReadOnlyList<ScoreTerm> Terms
) {
    /// <summary>Whether this candidate was scored at all.</summary>
    public bool WasScored => Excluded == PlacementFilter.None;

    /// <inheritdoc />
    public override string ToString() =>
        WasScored
            ? string.Create(CultureInfo.InvariantCulture, $"{Shard} scored {Score:0.##} — {string.Join(", ", Terms)}")
            : string.Create(CultureInfo.InvariantCulture, $"{Shard} excluded: {Excluded}");
}

/// <summary>Whether there was anywhere to go.</summary>
public enum PlacementOutcome : byte {
    /// <summary>There was. <see cref="PlacementDecision.Shard" /> says where.</summary>
    Placed = 0,

    /// <summary>
    ///     There was not — every candidate was filtered out, or there were none. Not an error:
    ///     it is the input <see cref="MapFleet" /> turns into a spawn.
    /// </summary>
    NoCandidate = 1
}

/// <summary>Where a player is going, and the whole argument for it.</summary>
/// <param name="Outcome">Whether anywhere.</param>
/// <param name="Shard">Which shard, when placed.</param>
/// <param name="Endpoint">Where that shard is, so nothing has to look it up again.</param>
/// <param name="Score">What it scored.</param>
/// <param name="Verdicts">Every candidate, in the order they were considered.</param>
public sealed record PlacementDecision(
    PlacementOutcome Outcome,
    ShardId Shard,
    RealmEndpoint Endpoint,
    double Score,
    IReadOnlyList<CandidateVerdict> Verdicts
) {
    /// <summary>The explanation, as a person reads it. Doc 27 § Diagnostics' `placement explain`.</summary>
    /// <returns>One line per candidate, best first among those that were scored.</returns>
    public string Explain() {
        var lines = Verdicts
            .OrderByDescending(verdict => verdict.WasScored)
            .ThenByDescending(verdict => verdict.Score)
            .Select(verdict => "  " + verdict);

        var headline = Outcome == PlacementOutcome.Placed
            ? string.Create(CultureInfo.InvariantCulture, $"placed on {Shard} at {Endpoint}, scoring {Score:0.##}")
            : $"no candidate out of {Verdicts.Count}";

        return string.Join(Environment.NewLine, lines.Prepend(headline));
    }
}

/// <summary>The megaserver, as a function. Doc 27 § Placement.</summary>
/// <remarks>
///     <para>
///         Hard filters, then a score, then the highest. That is the whole of it, and the value of
///         writing it as a pure function of numbers is what doc 27 § Testing asks for: property tests
///         over the scoring — a party is never split, a shard above its hard cap is never chosen,
///         scoring is total and deterministic for a given fleet — run a million times on a laptop.
///     </para>
///     <para>
///         ⚠ <b>Every placement explains itself, and it is not optional.</b> Doc 27 § Diagnostics:
///         "without it, placement complaints are unanswerable". A verdict per candidate is a handful
///         of small objects on the control plane, once per zone-in — the frame budget this would be
///         unaffordable on is one nothing here runs on.
///     </para>
///     <para>
///         ⚠ <b>Ties break on the shard id, ordinally.</b> Not for fairness — for determinism. A
///         placement that depended on the order candidates happened to be enumerated in would make
///         every property test flaky and every complaint unreproducible.
///     </para>
/// </remarks>
public sealed class PlacementDirector {
    readonly PlacementWeights weights;

    /// <summary>Stands one up.</summary>
    /// <param name="weights">The game's weights, or null for doc 27's defaults.</param>
    public PlacementDirector(PlacementWeights? weights = null) => this.weights = weights ?? PlacementWeights.Default;

    /// <summary>The weights in force.</summary>
    public PlacementWeights Weights => weights;

    /// <summary>Picks a shard.</summary>
    /// <param name="request">Who is asking, and for what.</param>
    /// <param name="candidates">Every shard the map has. Filtered here rather than by the caller.</param>
    /// <returns>Where they are going, and why.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public PlacementDecision Place(PlacementRequest request, IReadOnlyList<ShardCandidate> candidates) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);

        var verdicts = new List<CandidateVerdict>(candidates.Count);

        ShardCandidate? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var candidate in candidates) {
            var filter = Reject(request, candidate);

            if (filter != PlacementFilter.None) {
                verdicts.Add(new(candidate.Shard, filter, 0, []));

                continue;
            }

            var terms = Score(request, candidate);
            var score = terms.Sum(term => term.Value);

            verdicts.Add(new(candidate.Shard, PlacementFilter.None, score, terms));

            if (score > bestScore
                || (score == bestScore && best is not null && Prefer(candidate.Shard, best.Shard))) {
                best = candidate;
                bestScore = score;
            }
        }

        return best is null
            ? new(PlacementOutcome.NoCandidate, ShardId.None, RealmEndpoint.None, 0, verdicts)
            : new(PlacementOutcome.Placed, best.Shard, best.Endpoint, bestScore, verdicts);
    }

    /// <summary>The first hard filter a candidate fails, or none.</summary>
    /// <param name="request">Who is asking.</param>
    /// <param name="candidate">The shard.</param>
    /// <returns>Why it cannot be scored.</returns>
    /// <remarks>
    ///     Ordered cheapest and most-explanatory first. A player whose catalog is stale should be
    ///     told <see cref="PlacementFilter.Content" /> rather than <see cref="PlacementFilter.Full" />
    ///     when both are true, because only one of them is something they can do anything about.
    /// </remarks>
    public static PlacementFilter Reject(PlacementRequest request, ShardCandidate candidate) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(candidate.Key.Map, request.Key.Map, StringComparison.Ordinal)) {
            return PlacementFilter.Map;
        }

        if (!string.Equals(candidate.Key.Region, request.Key.Region, StringComparison.Ordinal)) {
            return PlacementFilter.Region;
        }

        if (!string.Equals(candidate.Key.Version.Build, request.Key.Version.Build, StringComparison.Ordinal)) {
            return PlacementFilter.Build;
        }

        if (candidate.Key.Version.Content != request.Key.Version.Content) {
            return PlacementFilter.Content;
        }

        if (candidate.State != ShardState.Ready) {
            return PlacementFilter.NotReady;
        }

        if (!candidate.Admits) {
            return PlacementFilter.Access;
        }

        return candidate.Capacity.Admits(candidate.Population) ? PlacementFilter.None : PlacementFilter.Full;
    }

    IReadOnlyList<ScoreTerm> Score(PlacementRequest request, ShardCandidate candidate) {
        var terms = new List<ScoreTerm>(6);

        if (request.Party != Guid.Empty && candidate.PartyMembers > 0) {
            // Not per member: one party member present is the whole of the pull. Scaling it would
            // mean a party of five outranking a party of two for no reason anybody could defend.
            terms.Add(new("party", weights.Party));
        }

        if (request.Guild != Guid.Empty && candidate.GuildMembers > 0) {
            terms.Add(new("guild", weights.GuildMember * Math.Min(candidate.GuildMembers, weights.GuildCap)));
        }

        if (candidate.Friends > 0) {
            terms.Add(new("friends", weights.Friend * Math.Min(candidate.Friends, weights.FriendCap)));
        }

        if (request.Locale.Length > 0
            && string.Equals(candidate.Locale, request.Locale, StringComparison.OrdinalIgnoreCase)) {
            terms.Add(new("locale", weights.Locale));
        }

        var fill = candidate.FillPercent;

        if (fill > weights.HealthyTo) {
            terms.Add(new("overfull", -(fill - weights.HealthyTo) * weights.Overfull));
        } else if (fill >= weights.HealthyFrom) {
            terms.Add(new("fill", weights.HealthyFill));
        }

        if (candidate.Age > weights.MaxAge) {
            terms.Add(new("age", weights.Aged));
        }

        if (candidate.Shard == request.CameFrom && request.CameFrom.IsValid) {
            terms.Add(new("antiflap", weights.AntiFlap));
        }

        return terms;
    }

    /// <summary>Breaks a tie, deterministically and for no other reason.</summary>
    static bool Prefer(ShardId candidate, ShardId incumbent) =>
        candidate.Value.CompareTo(incumbent.Value) < 0;
}
