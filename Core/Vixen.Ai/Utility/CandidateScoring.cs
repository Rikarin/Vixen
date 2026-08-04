// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>How many factors a candidate has, and what each of them reads.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A struct with a generic constraint rather than an interface reference, so the loop
///         devirtualises and nothing is allocated to score a candidate.</b> A utility agent scores a
///         handful of actions five times a second; an environment query scores every factor of every
///         generated point, of which there may be hundreds. One boxed reader per point per query
///         would be an allocation in the middle of the frame the whole design exists to avoid.
///     </para>
///     <para>
///         A factor is already normalised and already through its curve: it is in <c>[0,1]</c>, and
///         <b>zero is a veto</b>.
///     </para>
/// </remarks>
public interface IFactorSource {
    /// <summary>How many factors there are.</summary>
    int Count { get; }

    /// <summary>Reads one, in <c>[0,1]</c>.</summary>
    /// <param name="index">Which factor.</param>
    /// <returns>Its score.</returns>
    float Factor(int index);
}

/// <summary>Factors that have already been read, so the shared scorer can combine them.</summary>
/// <param name="factors">The readings, in <c>[0,1]</c>.</param>
/// <remarks>
///     ⚠ <b>The half of the pipeline that cannot stream, and it is not a shortcoming.</b> A utility
///     action reads its considerations one at a time and stops at the first zero, because a veto makes
///     the rest irrelevant. An environment query cannot: a test may <i>filter</i> the point, and the
///     filtering and the scoring are interleaved down one list — so a run collects what survived and
///     hands it here, and the mean and the veto are still the one implementation.
/// </remarks>
public readonly ref struct FactorSpan(ReadOnlySpan<float> factors) : IFactorSource {
    readonly ReadOnlySpan<float> factors = factors;

    /// <inheritdoc />
    public int Count => factors.Length;

    /// <inheritdoc />
    public float Factor(int index) => factors[index];
}

/// <summary>
///     A list of candidates scored the same way, whatever a candidate happens to be.
/// </summary>
/// <typeparam name="T">What is being chosen between — an action, a point.</typeparam>
/// <remarks>
///     <para>
///         <b>Doc 37 § D14, as a type.</b> Unreal's EQS answers "where should I stand" by generating
///         candidate points, running scored tests over them and taking the best; utility scoring
///         answers "what should I do" by generating candidate actions, running scored considerations
///         over them and taking the best. <i>Those are the same machine</i>, and this interface is
///         where that stops being a remark and becomes something the compiler checks:
///         <see cref="UtilitySet" /> implements it and so does <see cref="EnvironmentQuery" />.
///     </para>
///     <para>
///         ⚠ <b>Factor <i>counts</i> are per candidate, not per set</b>, which is the one place the
///         two hosts genuinely differ. Every point in a query runs the same test list; every action in
///         a utility set has its own considerations. A shared abstraction that assumed the query's
///         shape would have made the utility set implement it by lying.
///     </para>
/// </remarks>
public interface IScoredCandidateSet<out T> {
    /// <summary>How many candidates there are.</summary>
    int CandidateCount { get; }

    /// <summary>One of them.</summary>
    /// <param name="index">Which.</param>
    /// <returns>It.</returns>
    T CandidateAt(int index);

    /// <summary>What one candidate is called, for a table, an overlay and a diagnostic.</summary>
    /// <param name="index">Which candidate.</param>
    /// <returns>Its name.</returns>
    Symbol CandidateName(int index);

    /// <summary>How many factors score one candidate.</summary>
    /// <param name="index">Which candidate.</param>
    /// <returns>The count.</returns>
    int FactorsOf(int index);

    /// <summary>What one of a candidate's factors is called.</summary>
    /// <param name="index">Which candidate.</param>
    /// <param name="factor">Which factor.</param>
    /// <returns>Its name.</returns>
    Symbol FactorName(int index, int factor);

    /// <summary>Scores one candidate, and optionally reports every factor it read.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="index">Which candidate.</param>
    /// <param name="detail">Where to put each factor's own score, or empty for none.</param>
    /// <returns>The candidate's score.</returns>
    float ScoreOf(in AgentContext context, int index, Span<float> detail = default);
}

/// <summary>
///     The one place a set of factors becomes one number, and the one place a winner is picked.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 37 § D14's shared scorer.</b> A utility action's considerations and an environment
///         query test's readings arrive here by different routes and are combined by the same code:
///         the weighted geometric mean, with the zero rule intact.
///         <see cref="UtilityScoring.Combine" /> is the same arithmetic given a span that is already
///         full; this is the streaming form, which is what lets a veto stop the reads.
///     </para>
///     <para>
///         ⚠ <b>It stops at the first zero unless somebody asked for the detail.</b> A veto makes the
///         rest of the list irrelevant, and the rest of the list is where the reads of the world are —
///         so a query point that fails its cheapest test does not pay for a trace. The editor and the
///         debug overlay pass a span and get every number, because "why is this scoring zero" is the
///         question they exist to answer.
///     </para>
/// </remarks>
public static class CandidateScoring {
    /// <summary>Combines factors that have already been read.</summary>
    /// <param name="factors">Each factor, in <c>[0,1]</c>.</param>
    /// <param name="weight">The candidate's multiplier.</param>
    /// <returns>The score.</returns>
    /// <remarks>
    ///     ⚠ <b>The one implementation of the mean, and everything else in the engine forwards to
    ///     it.</b> <see cref="UtilityScoring.Combine" /> is this under the name a utility set knows it
    ///     by, and <see cref="EnvironmentQuery" /> calls it directly — which is what makes doc 37
    ///     § D14's "the same scorer serves both" a fact about the call graph rather than a comment.
    /// </remarks>
    public static float Combine(ReadOnlySpan<float> factors, float weight = 1f) {
        if (factors.Length == 0) {
            return MathF.Max(0f, weight);
        }

        var product = 1f;

        foreach (var factor in factors) {
            if (factor <= 0f) {
                return 0f;
            }

            product *= Math.Clamp(factor, 0f, 1f);
        }

        return MathF.Max(0f, weight) * MathF.Pow(product, 1f / factors.Length);
    }

    /// <summary>Combines one candidate's factors, reading them one at a time.</summary>
    /// <typeparam name="TSource">Where the factors come from.</typeparam>
    /// <param name="source">The factors.</param>
    /// <param name="weight">The candidate's multiplier.</param>
    /// <param name="detail">Where to put each factor's own score, or empty for none.</param>
    /// <returns>The score.</returns>
    /// <remarks>
    ///     An empty factor list scores <paramref name="weight" />: a candidate with nothing scoring it
    ///     is one that is always as good as its weight says, which is what a fallback wants to be.
    /// </remarks>
    public static float Score<TSource>(scoped ref readonly TSource source, float weight, Span<float> detail = default)
        where TSource : struct, IFactorSource, allows ref struct {
        var count = source.Count;

        if (count == 0) {
            return MathF.Max(0f, weight);
        }

        var wanted = detail.Length >= count;
        var product = 1f;

        for (var index = 0; index < count; index++) {
            var score = source.Factor(index);

            if (wanted) {
                detail[index] = score;
            }

            if (score <= 0f) {
                if (!wanted) {
                    return 0f;
                }

                product = 0f;
            }

            if (product > 0f) {
                product *= Math.Clamp(score, 0f, 1f);
            }
        }

        // ⚠ The nth root, and it is what stops a candidate being demoted for being tuned. With every
        // term in [0,1] a plain product makes six factors structurally worse than three, invisibly,
        // because every individual number still looks right.
        return product <= 0f ? 0f : MathF.Max(0f, weight) * MathF.Pow(product, 1f / count);
    }

    /// <summary>Scores every candidate of a set.</summary>
    /// <typeparam name="T">What a candidate is.</typeparam>
    /// <param name="set">The set.</param>
    /// <param name="context">The agent.</param>
    /// <param name="scores">Where the scores go. Must be at least as long as the set.</param>
    /// <returns>How many were scored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="scores" /> is too short.</exception>
    public static int ScoreAll<T>(IScoredCandidateSet<T> set, in AgentContext context, Span<float> scores) {
        ArgumentNullException.ThrowIfNull(set);

        if (scores.Length < set.CandidateCount) {
            throw new ArgumentException(
                $"A set of {set.CandidateCount} needs somewhere to put {set.CandidateCount} scores.",
                nameof(scores)
            );
        }

        for (var index = 0; index < set.CandidateCount; index++) {
            scores[index] = set.ScoreOf(in context, index);
        }

        return set.CandidateCount;
    }

    /// <summary>The best-scoring candidate.</summary>
    /// <param name="scores">What each scored.</param>
    /// <returns>Its index, or <c>-1</c> when nothing scored above zero.</returns>
    /// <remarks>
    ///     ⚠ <b>A tie breaks on the lower index, always</b> — doc 37 § D18. Never on a float
    ///     comparison and never on enumeration order, or a replay of the same tick makes a different
    ///     choice and the desync is six months away.
    /// </remarks>
    public static int Best(ReadOnlySpan<float> scores) {
        var best = -1;
        var top = 0f;

        for (var index = 0; index < scores.Length; index++) {
            if (scores[index] > top) {
                top = scores[index];
                best = index;
            }
        }

        return best;
    }
}
