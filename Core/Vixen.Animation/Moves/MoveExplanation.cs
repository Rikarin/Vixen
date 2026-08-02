// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Moves;

/// <summary>One term of a move's score, with its sign.</summary>
/// <param name="Reason">What it is — a matched preference, a speed error, the repeat penalty.</param>
/// <param name="Amount">What it added. Negative for a penalty.</param>
public readonly record struct ScoreTerm(string Reason, float Amount) {
    /// <inheritdoc />
    public override string ToString() => $"{(Amount >= 0f ? "+" : "")}{Amount:0.###}  {Reason}";
}

/// <summary>Why one move scored what it did, and whether it was eligible at all.</summary>
/// <param name="Key">Which move.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Eligible">Whether it passed the required facets.</param>
/// <param name="Missing">The first required facet it does not say, when it did not.</param>
/// <param name="Score">What it scored.</param>
/// <param name="Terms">Every term that made up that score.</param>
/// <param name="PlaybackRate">What it would have been retimed to.</param>
/// <remarks>
///     ⚠ <b>"Why did it pick that clip" is otherwise unanswerable, and that is the single most
///     valuable thing in the move set editor.</b> A scored query is a good design and an opaque one:
///     an author looking at the wrong animation has no way back from the result to the reason without
///     this, and the alternative they reach for is a pairwise table they can read.
/// </remarks>
public readonly record struct MoveExplanation(
    MoveKey Key,
    string Name,
    bool Eligible,
    Facet Missing,
    float Score,
    IReadOnlyList<ScoreTerm> Terms,
    float PlaybackRate
) {
    /// <inheritdoc />
    public override string ToString() =>
        Eligible
            ? $"{Name}: {Score:0.###} at {PlaybackRate:0.##}×"
            : $"{Name}: not eligible, does not say {Missing}";
}

/// <summary>Explains a query against a set, and says where the set has nothing to offer.</summary>
/// <remarks>
///     The editor's half of <see cref="QueryMoveSelector" />. It does not re-implement the scoring —
///     it asks <see cref="DefaultMoveScorer" /> for the total and separately reports the terms, so a
///     breakdown that disagreed with the answer is a test failure rather than a thing an author has to
///     notice.
/// </remarks>
public static class MoveExplanations {
    /// <summary>Scores every move in a set against a query, best first.</summary>
    /// <param name="moves">The set.</param>
    /// <param name="query">The question.</param>
    /// <returns>Every move, eligible or not, in the order the selector would rank them.</returns>
    public static IReadOnlyList<MoveExplanation> Explain(MoveSet moves, in MoveQuery query) {
        ArgumentNullException.ThrowIfNull(moves);

        var scorer = DefaultMoveScorer.Shared;

        List<MoveExplanation> found = [];

        for (var index = 0; index < moves.Count; index++) {
            var entry = moves[index];
            var eligible = moves.Matches(index, query.Required);
            var candidate = moves.Candidate(index);

            found.Add(
                new(
                    entry.Key,
                    entry.Name,
                    eligible,
                    eligible ? default : FirstMissing(entry, query.Required),
                    eligible ? scorer.Score(candidate, query) : float.NegativeInfinity,
                    Terms(entry, query),
                    entry.Traits.RateFor(query.Numeric.Speed)
                )
            );
        }

        // The selector's own order: score descending, ties on the key so two machines agree.
        found.Sort(
            static (left, right) => left.Score.Equals(right.Score)
                ? left.Key.CompareTo(right.Key)
                : right.Score.CompareTo(left.Score)
        );

        return found;
    }

    /// <summary>Every term of one move's score, in the order the scorer applies them.</summary>
    static List<ScoreTerm> Terms(MoveEntry entry, in MoveQuery query) {
        List<ScoreTerm> terms = [];

        if (query.Preferred is { } preferred) {
            foreach (var wanted in preferred) {
                if (entry.Facets.Contains(wanted.Facet)) {
                    terms.Add(new($"says {wanted.Facet}", wanted.Weight));
                }
            }
        }

        var traits = entry.Traits;
        var targets = query.Numeric;
        var slowest = MathF.Min(traits.SlowestSpeed, traits.FastestSpeed);
        var fastest = MathF.Max(traits.SlowestSpeed, traits.FastestSpeed);

        var speedError = targets.Speed < slowest ? slowest - targets.Speed
            : targets.Speed > fastest ? targets.Speed - fastest
            : 0f;

        if (speedError > 0f) {
            terms.Add(
                new(
                    $"wants {targets.Speed:0.##} m/s and retimes to {slowest:0.##}–{fastest:0.##}",
                    -speedError * targets.SpeedWeight
                )
            );
        }

        var turnError = MathF.Abs(targets.TurnRate - traits.TurnRate);

        if (turnError > 0f) {
            terms.Add(
                new($"wants {targets.TurnRate:0.##} rad/s and turns at {traits.TurnRate:0.##}", -turnError * targets.TurnWeight)
            );
        }

        if (query.RepeatPenalty != 0f && entry.Key == query.Previous) {
            terms.Add(new("just played", -query.RepeatPenalty));
        }

        return terms;
    }

    static Facet FirstMissing(MoveEntry entry, FacetSet required) {
        foreach (var facet in required.Facets) {
            if (!entry.Facets.Contains(facet)) {
                return facet;
            }
        }

        return default;
    }
}

/// <summary>One cell of a coverage sweep: a question, and what the set answers with.</summary>
/// <param name="Speed">The speed asked for.</param>
/// <param name="Required">What was required of the move.</param>
/// <param name="Chosen">What would be picked, or empty when nothing is eligible.</param>
/// <param name="Error">How far the chosen move's rate range is from the speed asked for, in m/s.</param>
/// <param name="Stretch">How far outside its declared rate range it would have to be played.</param>
public readonly record struct CoverageCell(
    float Speed,
    FacetSet Required,
    string Chosen,
    float Error,
    float Stretch
) {
    /// <summary>Whether anything at all answers.</summary>
    public bool Answered => Chosen.Length > 0;

    /// <summary>Whether the answer is a fallback rather than a fit.</summary>
    public bool FallsBack => !Answered || Error > 0f;
}

/// <summary>Where a set has nothing to offer, swept across the questions a game will ask.</summary>
/// <param name="Cells">Every question and its answer.</param>
/// <remarks>
///     ⚠ <b>Not an error, and the thing an author needs to see before shipping.</b> A set with no
///     injured stop and nothing above four metres a second works — it falls back — and the failure is
///     that nobody knew. A playtest finds this on the one combination somebody happened to try.
/// </remarks>
public sealed record MoveCoverage(IReadOnlyList<CoverageCell> Cells) {
    /// <summary>How many questions had no eligible answer at all.</summary>
    public int Unanswered => Count(static cell => !cell.Answered);

    /// <summary>How many got an answer that does not fit.</summary>
    public int FallsBack => Count(static cell => cell.FallsBack);

    /// <summary>The worst cell, for an editor that wants to select it.</summary>
    public CoverageCell Worst {
        get {
            var worst = default(CoverageCell);
            var found = false;

            foreach (var cell in Cells) {
                if (!found || Rank(cell) > Rank(worst)) {
                    worst = cell;
                    found = true;
                }
            }

            return worst;
        }
    }

    static float Rank(in CoverageCell cell) => cell.Answered ? cell.Error : float.MaxValue;

    int Count(Func<CoverageCell, bool> predicate) {
        var found = 0;

        foreach (var cell in Cells) {
            if (predicate(cell)) {
                found++;
            }
        }

        return found;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Cells.Count} questions: {Unanswered} with no answer, {FallsBack} falling back.";

    /// <summary>Sweeps a set across a speed range and a set of required-facet combinations.</summary>
    /// <param name="moves">The set.</param>
    /// <param name="required">
    ///     Each combination of facets a game might require — an empty set, <c>injured</c>,
    ///     <c>injured</c> + <c>snow</c>, and so on.
    /// </param>
    /// <param name="fastest">The fastest speed to ask about, in metres a second.</param>
    /// <param name="steps">How many speeds to try between zero and that.</param>
    /// <returns>The sweep.</returns>
    public static MoveCoverage Sweep(MoveSet moves, IReadOnlyList<FacetSet> required, float fastest = 8f, int steps = 17) {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(required);

        List<CoverageCell> cells = [];
        var count = Math.Max(steps, 2);

        foreach (var wanted in required) {
            for (var index = 0; index < count; index++) {
                var speed = fastest * index / (count - 1);
                var query = new MoveQuery { Required = wanted, Numeric = new() { Speed = speed } };
                var chosen = QueryMoveSelector.Shared.Choose(moves, query, DefaultMoveScorer.Shared);

                if (!chosen.HasMove) {
                    cells.Add(new(speed, wanted, string.Empty, float.MaxValue, 0f));
                    continue;
                }

                var traits = moves[chosen.Index].Traits;
                var slowest = MathF.Min(traits.SlowestSpeed, traits.FastestSpeed);
                var top = MathF.Max(traits.SlowestSpeed, traits.FastestSpeed);

                var error = speed < slowest ? slowest - speed
                    : speed > top ? speed - top
                    : 0f;

                cells.Add(
                    new(
                        speed,
                        wanted,
                        moves[chosen.Index].Name,
                        error,
                        error <= 0f ? 0f : error / MathF.Max(top, 1e-3f)
                    )
                );
            }
        }

        return new(cells);
    }
}
