// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;

namespace Vixen.Live.Matchmaking;

/// <summary>One entry in a queue: a party, what they are, and how long they have waited.</summary>
/// <remarks>
///     ⚠ <b>A ticket is a <em>group</em>, never a player, and that is how doc 28's "a party is never
///     split" becomes a property of the types rather than a rule somebody has to remember.</b> A
///     solo player is a party of one. Nothing downstream can separate two people who queued together,
///     because nothing downstream is ever handed one of them.
/// </remarks>
public sealed class MatchTicket {
    readonly PlayerId[] players;
    readonly float[] latencies;

    /// <summary>Makes one.</summary>
    /// <param name="id">What names it.</param>
    /// <param name="players">Who is in it. One for a solo queue.</param>
    /// <param name="rating">How good the party is thought to be.</param>
    /// <param name="enqueued">When they joined, on the caller's clock.</param>
    /// <param name="tags">What they are — a role, a region, a game mode.</param>
    /// <param name="latencies">Their measured latency to each region, by region index.</param>
    public MatchTicket(
        string id,
        IReadOnlyList<PlayerId> players,
        Rating rating,
        float enqueued,
        GameplayTagSet? tags = null,
        IReadOnlyList<float>? latencies = null
    ) {
        ArgumentNullException.ThrowIfNull(players);

        Id = id ?? string.Empty;
        this.players = [.. players];
        Rating = rating;
        Enqueued = enqueued;
        Tags = tags ?? new GameplayTagSet();
        this.latencies = latencies is null ? [] : [.. latencies];
    }

    /// <summary>What names it.</summary>
    public string Id { get; }

    /// <summary>Who is in it.</summary>
    public ReadOnlySpan<PlayerId> Players => players;

    /// <summary>How many. A party is never split, so this is how many seats it takes.</summary>
    public int Size => players.Length;

    /// <summary>How good they are thought to be.</summary>
    public Rating Rating { get; }

    /// <summary>When they joined.</summary>
    public float Enqueued { get; }

    /// <summary>What they are.</summary>
    public GameplayTagSet Tags { get; }

    /// <summary>How long they have waited.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>The wait, in seconds.</returns>
    public float WaitedFor(float now) => MathF.Max(0f, now - Enqueued);

    /// <summary>Their latency to a region, or infinity when it has not been measured.</summary>
    /// <param name="region">Which region.</param>
    /// <returns>The latency, in milliseconds.</returns>
    public float LatencyTo(int region) =>
        (uint)region < (uint)latencies.Length ? latencies[region] : float.PositiveInfinity;
}

/// <summary>A filter over tickets: what a match function is allowed to see.</summary>
/// <remarks>
///     <b>Open Match's <em>pool</em>, and doc 28 says what it is made of:</b> <em>"a tag + range
///     query, the same requirement algebra as everything else"</em>. There is no filter language
///     here — <see cref="GameplayTagQuery" /> already does all-of, any-of and none-of, and inventing a
///     second one would give a game two vocabularies for the same question.
/// </remarks>
/// <param name="Tags">What a ticket must be, or null for anything.</param>
/// <param name="MinimumRating">The lowest conservative rating it will take.</param>
/// <param name="MaximumRating">The highest.</param>
/// <param name="Region">Which region's latency is checked, or −1 for none.</param>
/// <param name="MaximumLatency">The worst latency it will take, in milliseconds.</param>
public readonly record struct MatchPool(
    GameplayTagQuery? Tags = null,
    double MinimumRating = double.NegativeInfinity,
    double MaximumRating = double.PositiveInfinity,
    int Region = -1,
    float MaximumLatency = float.PositiveInfinity
) {
    /// <summary>The pool that takes anybody.</summary>
    /// <remarks>
    ///     ⚠ <b>Spelled out rather than <c>default</c>, because they are not the same thing and the
    ///     difference is a trap.</b> A positional record struct's parameter defaults belong to its
    ///     <em>constructor</em>; <c>default(MatchPool)</c> zeroes every field, which for this type
    ///     means a rating band of exactly zero and a maximum latency of nothing — a pool that admits
    ///     nobody. The first version of <see cref="Matchmaker" /> took <c>MatchPool pool = default</c>
    ///     and every queue built with it silently refused every ticket.
    /// </remarks>
    public static MatchPool Everybody { get; } =
        new(null, double.NegativeInfinity, double.PositiveInfinity, -1, float.PositiveInfinity);

    /// <summary>Whether a ticket belongs in it.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <returns>Whether it does.</returns>
    public bool Admits(MatchTicket ticket) {
        ArgumentNullException.ThrowIfNull(ticket);

        var rating = ticket.Rating.Conservative;

        return rating >= MinimumRating
            && rating <= MaximumRating
            && (Tags is null || Tags.Matches(ticket.Tags))
            && (Region < 0 || ticket.LatencyTo(Region) <= MaximumLatency);
    }
}

/// <summary>A match somebody thinks should happen.</summary>
/// <param name="Teams">The tickets on each side.</param>
/// <param name="Quality">How even it is, from zero to one.</param>
public readonly record struct MatchProposal(IReadOnlyList<IReadOnlyList<MatchTicket>> Teams, double Quality) {
    /// <summary>Every ticket in it.</summary>
    public IEnumerable<MatchTicket> Tickets => Teams.SelectMany(team => team);

    /// <summary>How many players it seats.</summary>
    public int Players => Teams.Sum(team => team.Sum(ticket => ticket.Size));

    /// <summary>The longest anybody in it has waited.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>The wait, in seconds.</returns>
    public float OldestWait(float now) {
        var oldest = 0f;

        foreach (var ticket in Tickets) {
            oldest = MathF.Max(oldest, ticket.WaitedFor(now));
        }

        return oldest;
    }
}

/// <summary>The game's own code: what makes a match out of a pool.</summary>
/// <remarks>
///     <b>Open Match's <em>match function</em>.</b> Doc 28 hands this to the game deliberately —
///     what a good match is differs per mode more than anything else in matchmaking, and a framework
///     that decided it would be a framework games fight.
/// </remarks>
public interface IMatchFunction {
    /// <summary>What it is called.</summary>
    string Name { get; }

    /// <summary>Proposes matches from what is waiting.</summary>
    /// <param name="pool">The tickets, oldest first.</param>
    /// <param name="now">The clock.</param>
    /// <returns>Whatever it thinks should happen. May overlap; the evaluator sorts that out.</returns>
    IReadOnlyList<MatchProposal> Propose(IReadOnlyList<MatchTicket> pool, float now);
}

/// <summary>What settles two proposals that want the same ticket.</summary>
public interface IMatchEvaluator {
    /// <summary>Picks a non-overlapping set.</summary>
    /// <param name="proposals">What was proposed.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The ones that will happen.</returns>
    IReadOnlyList<MatchProposal> Evaluate(IReadOnlyList<MatchProposal> proposals, float now);
}

/// <summary>Highest quality first, ties broken by whoever has waited longest.</summary>
/// <remarks>
///     ⚠ <b>The tie-break is not decoration.</b> Quality alone starves a ticket nothing pairs well
///     with: it is beaten by a better proposal every cycle and waits for ever. Doc 28 names oldest-first
///     as the default for that reason, and it is what bounds the queue time the tests measure.
/// </remarks>
public sealed class HighestQualityEvaluator : IMatchEvaluator {
    /// <inheritdoc />
    public IReadOnlyList<MatchProposal> Evaluate(IReadOnlyList<MatchProposal> proposals, float now) {
        ArgumentNullException.ThrowIfNull(proposals);

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<MatchProposal>();

        foreach (var proposal in proposals
                     .OrderByDescending(entry => entry.Quality)
                     .ThenByDescending(entry => entry.OldestWait(now))) {
            var clash = false;

            foreach (var ticket in proposal.Tickets) {
                if (taken.Contains(ticket.Id)) {
                    clash = true;

                    break;
                }
            }

            if (clash) {
                continue;
            }

            foreach (var ticket in proposal.Tickets) {
                taken.Add(ticket.Id);
            }

            accepted.Add(proposal);
        }

        return accepted;
    }
}

/// <summary>One queue: tickets go in, matches come out, and something else allocates the shard.</summary>
/// <remarks>
///     <para>
///         <b>Open Match's four concepts and none of its deployment.</b> Doc 28 is explicit that what
///         is worth taking is the separation of <em>filtering</em>, <em>proposing</em>,
///         <em>evaluating</em> and <em>allocating</em> — and that the Kubernetes-and-Go topology is
///         not, which is the same objection doc 27 ADR-019 already made about the substrate.
///     </para>
///     <para>
///         ⚠ <b>Allocating is not here.</b> The director's job is <c>IMapGrain.Place</c>, doc 27's
///         placement, and a second allocator is the thing this must not become: two of them disagree
///         about capacity and the disagreement is a shard nobody can join. <see cref="Matched" /> is
///         where a caller hands an accepted match to placement.
///     </para>
///     <para>
///         ⚠ <b>A widening band is what actually bounds a queue time</b>, and it is here rather than
///         in the match function because every mode needs it and none of them should have to write
///         it. A pool's rating range grows with the oldest wait in it; without that, the top and
///         bottom of a ladder never match anybody.
///     </para>
/// </remarks>
public sealed class Matchmaker {
    readonly List<MatchTicket> waiting = [];

    /// <summary>Makes a queue.</summary>
    /// <param name="function">What makes matches out of a pool.</param>
    /// <param name="pool">Which tickets it will consider, or null for anybody.</param>
    /// <param name="evaluator">What settles overlapping proposals, or the default.</param>
    /// <param name="wideningPerSecond">How much the rating band grows per second waited.</param>
    public Matchmaker(
        IMatchFunction function,
        MatchPool? pool = null,
        IMatchEvaluator? evaluator = null,
        double wideningPerSecond = 0d
    ) {
        ArgumentNullException.ThrowIfNull(function);

        Function = function;
        Pool = pool ?? MatchPool.Everybody;
        Evaluator = evaluator ?? new HighestQualityEvaluator();
        WideningPerSecond = Math.Max(0d, wideningPerSecond);
    }

    /// <summary>What makes matches.</summary>
    public IMatchFunction Function { get; }

    /// <summary>Which tickets it will consider.</summary>
    public MatchPool Pool { get; }

    /// <summary>What settles overlapping proposals.</summary>
    public IMatchEvaluator Evaluator { get; }

    /// <summary>How much the rating band grows per second waited.</summary>
    public double WideningPerSecond { get; }

    /// <summary>What is waiting, oldest first.</summary>
    public IReadOnlyList<MatchTicket> Waiting => waiting;

    /// <summary>How many parties are waiting.</summary>
    public int Count => waiting.Count;

    /// <summary>How many players are.</summary>
    public int Players => waiting.Sum(ticket => ticket.Size);

    /// <summary>Raised for every match that is made. Where a caller calls placement.</summary>
    public event Action<MatchProposal>? Matched;

    /// <summary>Puts a party in.</summary>
    /// <param name="ticket">The ticket.</param>
    /// <returns>Whether it went in — false when it is already waiting or the pool refuses it.</returns>
    public bool Enqueue(MatchTicket ticket) {
        ArgumentNullException.ThrowIfNull(ticket);

        if (!Pool.Admits(ticket) || waiting.Exists(entry => string.Equals(entry.Id, ticket.Id, StringComparison.Ordinal))) {
            return false;
        }

        waiting.Add(ticket);

        return true;
    }

    /// <summary>Takes a party out.</summary>
    /// <param name="id">Which ticket.</param>
    /// <returns>Whether it was in.</returns>
    public bool Cancel(string id) =>
        waiting.RemoveAll(ticket => string.Equals(ticket.Id, id, StringComparison.Ordinal)) > 0;

    /// <summary>Runs one cycle: snapshot, propose, evaluate, remove.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>What was matched.</returns>
    public IReadOnlyList<MatchProposal> Cycle(float now) {
        if (waiting.Count == 0) {
            return [];
        }

        // Oldest first, because that is the order a match function should see people in and the order
        // the default evaluator breaks its ties by. Sorting here means no function has to remember.
        var snapshot = waiting.OrderBy(ticket => ticket.Enqueued).ToArray();
        var proposals = Function.Propose(snapshot, now);

        if (proposals.Count == 0) {
            return [];
        }

        var accepted = Evaluator.Evaluate(proposals, now);

        foreach (var proposal in accepted) {
            foreach (var ticket in proposal.Tickets) {
                Cancel(ticket.Id);
            }

            Matched?.Invoke(proposal);
        }

        return accepted;
    }

    /// <summary>How wide a ticket's acceptable rating band has grown.</summary>
    /// <param name="ticket">Whose.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The half-width, in rating points.</returns>
    public double BandFor(MatchTicket ticket, float now) {
        ArgumentNullException.ThrowIfNull(ticket);

        return WideningPerSecond * ticket.WaitedFor(now);
    }

    /// <summary>Whether two tickets are close enough to be matched, given how long they have waited.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <param name="now">The clock.</param>
    /// <returns>Whether they are.</returns>
    /// <remarks>
    ///     ⚠ <b>The <em>wider</em> of the two bands, not the narrower.</b> Somebody who has waited ten
    ///     minutes should be matchable with a newcomer; requiring both to have waited would mean the
    ///     long-waiting player can only ever be paired with somebody who has waited just as long,
    ///     which is the starvation the widening exists to fix.
    /// </remarks>
    public bool Compatible(MatchTicket left, MatchTicket right, float now) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (WideningPerSecond <= 0d) {
            return true;
        }

        var band = Math.Max(BandFor(left, now), BandFor(right, now));

        return Math.Abs(left.Rating.Conservative - right.Rating.Conservative) <= band;
    }
}
