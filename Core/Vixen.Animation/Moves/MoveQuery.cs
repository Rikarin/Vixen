// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Moves;

/// <summary>What a body wants to be doing, as numbers a move can be measured against.</summary>
/// <remarks>
///     Filled by an <see cref="IGaitModel" /> from the intent, never by the selector. Which numbers
///     matter is a property of the body — a quadruped and a biped disagree about turning and about
///     nothing else here — and the selection pass is the same for both.
/// </remarks>
public readonly record struct MoveTargets {
    /// <summary>How fast, in metres a second.</summary>
    public float Speed { get; init; }

    /// <summary>How fast it wants to turn, in radians a second. Signed; positive is left.</summary>
    public float TurnRate { get; init; }

    /// <summary>How much a metre a second of speed error counts against a candidate.</summary>
    /// <remarks>
    ///     Weights rather than a fixed scale, because the two errors are in different units and the
    ///     trade between them is a project's judgement. The defaults are calibrated so that a metre a
    ///     second and about a third of a turn a second cost the same.
    /// </remarks>
    public float SpeedWeight { get; init; } = 1f;

    /// <summary>How much a radian a second of turn error counts.</summary>
    public float TurnWeight { get; init; } = 0.3f;

    /// <summary>Creates targets with the default weights.</summary>
    public MoveTargets() {
    }
}

/// <summary>The question the selector answers.</summary>
/// <remarks>
///     <para>
///         <b>A value, so building one costs nothing and comparing two is comparing a few words.</b>
///         The selection pass only has to run when the question changes, and that test is this type's
///         equality — which is why it is a record struct and why <see cref="Preferred" /> is a memory
///         rather than an array.
///     </para>
///     <para>
///         ⚠ <b><see cref="Required" /> is the only hard filter, and it should be used sparingly.</b>
///         Everything expressed as a requirement is a way for the query to return nothing; everything
///         expressed as a preference degrades instead. "The best walk, given that I am injured and on
///         ice" wants <c>role=loop</c> required and the rest preferred, so a set with no injured
///         ice-walk finds the injured walk, and one with neither finds the walk.
///     </para>
/// </remarks>
public readonly record struct MoveQuery {
    /// <summary>What a candidate must say to be considered at all.</summary>
    public FacetSet Required { get; init; } = FacetSet.Empty;

    /// <summary>What a candidate is rewarded for saying.</summary>
    /// <remarks>
    ///     ⚠ <b>An array, and it is compared by reference.</b> A memory would be tidier and costs a
    ///     span construction inside the scoring loop — five hundred of them per selection, which
    ///     measured. Reference equality is also the right test for "has the question changed": a
    ///     caller that rebuilds the same preferences into a new array every frame is asking a new
    ///     question as far as this is concerned, and the fix for that is to stop doing it.
    /// </remarks>
    public WeightedFacet[]? Preferred { get; init; }

    /// <summary>What the body wants to be doing.</summary>
    public MoveTargets Numeric { get; init; }

    /// <summary>What is playing, so the same move is not immediately chosen again.</summary>
    public MoveKey Previous { get; init; }

    /// <summary>How much choosing <see cref="Previous" /> again counts against it.</summary>
    /// <remarks>
    ///     ⚠ <b>Small, and not a prohibition.</b> A two-candidate set alternating visibly is the
    ///     problem this solves; a walk that refused to continue into itself would be worse than the
    ///     problem. Zero disables it, which is right for a query that is re-asked every frame while
    ///     nothing has changed.
    /// </remarks>
    public float RepeatPenalty { get; init; }

    /// <summary>Creates an empty query.</summary>
    public MoveQuery() {
    }
}

/// <summary>What the selector decided.</summary>
/// <param name="Index">Which entry, or −1 if the set had nothing at all.</param>
/// <param name="Score">What it scored. Higher is better; the scale is the scorer's own.</param>
/// <param name="PlaybackRate">
///     How fast to play it to hit the wanted speed, within what the move admits.
/// </param>
public readonly record struct MoveSelection(int Index, float Score, float PlaybackRate) {
    /// <summary>Nothing was chosen.</summary>
    public static MoveSelection None => new(-1, float.NegativeInfinity, 1f);

    /// <summary>Whether a move was chosen.</summary>
    public bool HasMove => Index >= 0;
}

/// <summary>One move, as the selection pass sees it: flat, contiguous, no pointer chasing.</summary>
/// <param name="Index">Its position in the set.</param>
/// <param name="Key">Its identity, for the tie-break.</param>
/// <param name="Traits">What it does.</param>
/// <param name="Facets">What it is for, packed and sorted.</param>
/// <param name="Entry">
///     The move itself, for a scorer that needs something the flat view does not carry. Reading it
///     costs the dereference the flat view exists to avoid, so a scorer on the hot path should not.
/// </param>
/// <remarks>
///     ⚠ <b>A ref struct, so it cannot be stored.</b> The facets are a window into the set's own
///     table and are valid for the call; a scorer that squirrelled one away would be holding a span
///     into an array it does not own. The compiler refusing is better than a comment asking.
/// </remarks>
public readonly ref struct MoveCandidate(
    int Index,
    MoveKey Key,
    MoveTraits Traits,
    ReadOnlySpan<ulong> Facets,
    MoveEntry Entry
) {
    /// <summary>Its position in the set.</summary>
    public int Index { get; } = Index;

    /// <summary>Its identity, for the tie-break.</summary>
    public MoveKey Key { get; } = Key;

    /// <summary>What it does.</summary>
    public MoveTraits Traits { get; } = Traits;

    /// <summary>What it is for, packed and sorted.</summary>
    public ReadOnlySpan<ulong> Facets { get; } = Facets;

    /// <summary>The move itself, for whatever the flat view does not carry.</summary>
    public MoveEntry Entry { get; } = Entry;

    /// <summary>Whether it says this.</summary>
    /// <param name="facet">The fact.</param>
    /// <returns>Whether it does.</returns>
    public bool Says(Facet facet) {
        var wanted = facet.Packed;

        foreach (var held in Facets) {
            if (held == wanted) {
                return true;
            }

            if (held > wanted) {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether it says everything a set does.</summary>
    /// <param name="required">The facts that have to be there.</param>
    /// <returns>Whether they all are.</returns>
    public bool SaysAll(FacetSet required) {
        ArgumentNullException.ThrowIfNull(required);

        if (required.Count == 0) {
            return true;
        }

        if (required.Count > Facets.Length) {
            return false;
        }

        var mine = 0;

        foreach (var want in required.Packed) {
            while (mine < Facets.Length && Facets[mine] < want) {
                mine++;
            }

            if (mine >= Facets.Length || Facets[mine] != want) {
                return false;
            }

            mine++;
        }

        return true;
    }
}

/// <summary>How well a move suits a query. The soft half of selection.</summary>
/// <remarks>
///     Separate from <see cref="IMoveSelector" /> because the two vary independently: a project may
///     want extra terms in the score — a cooldown, a preference the combat system supplies — without
///     touching how candidates are filtered or how ties break, and the reverse is just as true.
/// </remarks>
public interface IMoveScorer {
    /// <summary>Scores a candidate that has already passed the hard filter.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="query">What was asked for.</param>
    /// <returns>The score. Higher wins.</returns>
    float Score(in MoveCandidate candidate, in MoveQuery query);
}

/// <summary>The shipped scorer: matched preferences, numeric proximity, and a repeat penalty.</summary>
/// <remarks>
///     <para>
///         <b>Numeric error is a penalty rather than a filter, and that is a deliberate reading of
///         the design.</b> Dropping every candidate that cannot reach the wanted speed empties the
///         set when a character sprints faster than any authored clip, and an empty selection has no
///         good answer. Scoring the shortfall instead means the fastest move wins and plays at the
///         top of its rate range — the closest thing the set has, which is what the whole approach is
///         for.
///     </para>
///     <para>
///         <b>The reachable interval is what the error is measured to, not the authored speed.</b> A
///         walk that admits ±15 % and a run that admits none are being asked a different question,
///         and measuring both from where they happen to be authored would make the walk look worse
///         than it is at every speed it can actually cover.
///     </para>
/// </remarks>
public sealed class DefaultMoveScorer : IMoveScorer {
    /// <summary>The one every selector uses unless told otherwise.</summary>
    public static DefaultMoveScorer Shared { get; } = new();

    /// <inheritdoc />
    public float Score(in MoveCandidate candidate, in MoveQuery query) {
        var score = 0f;

        if (query.Preferred is { } preferred) {
            foreach (var wanted in preferred) {
                if (candidate.Says(wanted.Facet)) {
                    score += wanted.Weight;
                }
            }
        }

        var traits = candidate.Traits;
        var targets = query.Numeric;

        // Distance from the interval the move can actually be retimed into, which is zero inside it.
        var slowest = MathF.Min(traits.SlowestSpeed, traits.FastestSpeed);
        var fastest = MathF.Max(traits.SlowestSpeed, traits.FastestSpeed);
        var speedError = targets.Speed < slowest ? slowest - targets.Speed
            : targets.Speed > fastest ? targets.Speed - fastest
            : 0f;

        score -= speedError * targets.SpeedWeight;
        score -= MathF.Abs(targets.TurnRate - traits.TurnRate) * targets.TurnWeight;

        if (query.RepeatPenalty != 0f && candidate.Key == query.Previous) {
            score -= query.RepeatPenalty;
        }

        return score;
    }
}

/// <summary>Which move to play. The whole of selection, and replaceable.</summary>
/// <remarks>
///     The seam a project takes to replace the policy outright — a feature-vector nearest-neighbour
///     matcher, a learned policy, a table-driven chooser. All of them are "a thing that picks an
///     entry from a set given a question", which is this.
/// </remarks>
public interface IMoveSelector {
    /// <summary>Picks a move.</summary>
    /// <param name="moves">What there is to choose from.</param>
    /// <param name="query">What is wanted.</param>
    /// <param name="scorer">How to rank the candidates.</param>
    /// <returns>What was chosen, or <see cref="MoveSelection.None" />.</returns>
    MoveSelection Choose(MoveSet moves, in MoveQuery query, IMoveScorer scorer);
}

/// <summary>The shipped selector: filter, score, pick, retime.</summary>
/// <remarks>
///     <para>
///         <b>One pass over the candidates, no allocation, no hashing.</b> The filter is a merge over
///         two sorted facet runs with an early exit on the first miss, which most candidates take.
///     </para>
///     <para>
///         ⚠ <b>Ties break on <see cref="MoveKey" />, which is a hash of the name and therefore the
///         same everywhere.</b> Two machines running the same build must pick the same move from the
///         same inputs, and "whichever the loop reached first" is only stable if the set's order is —
///         which it is, but the key comparison says so explicitly rather than relying on it.
///     </para>
/// </remarks>
public sealed class QueryMoveSelector : IMoveSelector {
    /// <summary>The one an animator gets unless a project installs another.</summary>
    public static QueryMoveSelector Shared { get; } = new();

    /// <inheritdoc />
    public MoveSelection Choose(MoveSet moves, in MoveQuery query, IMoveScorer scorer) {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(scorer);

        var best = MoveSelection.None;
        var bestKey = default(MoveKey);

        for (var index = 0; index < moves.Count; index++) {
            // Filtered before the view is built, because most candidates do not survive this and a
            // view for one that does not is pure cost.
            if (!moves.Matches(index, query.Required)) {
                continue;
            }

            var candidate = moves.Candidate(index);
            var score = scorer.Score(candidate, query);

            if (best.HasMove) {
                if (score < best.Score) {
                    continue;
                }

                if (score == best.Score && candidate.Key.CompareTo(bestKey) >= 0) {
                    continue;
                }
            }

            best = new(index, score, candidate.Traits.RateFor(query.Numeric.Speed));
            bestKey = candidate.Key;
        }

        return best;
    }
}
