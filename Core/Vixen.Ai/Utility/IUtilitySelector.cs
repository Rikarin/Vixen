// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai;

/// <summary>Which of the scored actions wins.</summary>
/// <remarks>
///     <para>
///         doc 37 § D9's seam. Highest-scoring is one rule and it is not always the right one — but
///         all four of the shipped rules are about the <i>scores</i>, and none of them knows that
///         there is an action already running. ⚠ <b>Inertia is deliberately not here</b>; it is on the
///         set, because it is about the running action.
///     </para>
///     <para>
///         A selector must be a pure function of the scores and the agent's own random stream, so that
///         doc 37 § D18's determinism holds: a replay of the same tick makes the same choice.
///     </para>
/// </remarks>
public interface IUtilitySelector {
    /// <summary>Picks one.</summary>
    /// <param name="context">The agent, for its random stream.</param>
    /// <param name="options">The set being chosen from, for weights and buckets.</param>
    /// <param name="scores">Each action's score, in the set's own order.</param>
    /// <returns>The index of the winner, or <c>-1</c> when nothing scored above zero.</returns>
    int Pick(in AgentContext context, UtilitySet options, ReadOnlySpan<float> scores);
}

/// <summary>The selectors that ship.</summary>
public static class UtilitySelectors {
    /// <summary>The best one. Deterministic, and correct for anything a designer must be able to predict.</summary>
    public static IUtilitySelector Highest { get; } = new HighestSelector();

    /// <summary>Score as weight. Natural-looking and occasionally stupid.</summary>
    public static IUtilitySelector WeightedRandom { get; } = new WeightedRandomSelector();

    /// <summary>Weighted random among the best few, which is the one most games actually want.</summary>
    /// <param name="count">How many of the best to consider.</param>
    /// <returns>The selector.</returns>
    public static IUtilitySelector TopWeightedRandom(int count) => new TopWeightedRandomSelector { Count = count };

    /// <summary>Weighted random among the best fraction.</summary>
    /// <param name="fraction">What proportion of the set to consider, in <c>(0,1]</c>.</param>
    /// <returns>The selector.</returns>
    public static IUtilitySelector TopWeightedRandomFraction(float fraction) =>
        new TopWeightedRandomSelector { Fraction = fraction };

    /// <summary>Dual utility: the best group wins, and only its members are considered.</summary>
    public static IUtilitySelector Bucketed { get; } = new BucketedSelector();

    /// <summary>The best index, ignoring anything at or below zero.</summary>
    /// <remarks>
    ///     <paramref name="set" /> and <paramref name="bucket" /> restrict it to one group. Ties go to
    ///     the earlier action, so a set's own order is the tie-break and two agents scoring identically
    ///     do the same thing.
    /// </remarks>
    internal static int Best(ReadOnlySpan<float> scores, UtilitySet? set = null, int bucket = 0) {
        var best = -1;
        var value = 0f;

        for (var index = 0; index < scores.Length; index++) {
            if (scores[index] <= value || (set is not null && set[index].Bucket != bucket)) {
                continue;
            }

            best = index;
            value = scores[index];
        }

        return best;
    }
}

/// <summary>The best one.</summary>
sealed class HighestSelector : IUtilitySelector {
    public int Pick(in AgentContext context, UtilitySet options, ReadOnlySpan<float> scores) =>
        UtilitySelectors.Best(scores);
}

/// <summary>Score as weight.</summary>
sealed class WeightedRandomSelector : IUtilitySelector {
    public int Pick(in AgentContext context, UtilitySet options, ReadOnlySpan<float> scores) =>
        Roll(in context, scores, scores.Length, 0x0D1CE);

    /// <summary>Rolls over the <paramref name="count" /> best scores.</summary>
    /// <remarks>
    ///     ⚠ The roll is from the agent's own stream and salted per use, so two agents scoring
    ///     identically do not agree and one agent's action choice does not correlate with anything
    ///     else it draws — which is what doc 37 § D18 means by determinism being a property of the
    ///     decision.
    /// </remarks>
    internal static int Roll(in AgentContext context, ReadOnlySpan<float> scores, int count, uint salt) {
        Span<int> best = stackalloc int[Math.Min(count, scores.Length)];
        var found = Rank(scores, best);

        if (found == 0) {
            return -1;
        }

        var total = 0f;

        for (var index = 0; index < found; index++) {
            total += scores[best[index]];
        }

        if (total <= 0f) {
            return best[0];
        }

        var roll = context.Random(salt) * total;

        for (var index = 0; index < found; index++) {
            roll -= scores[best[index]];

            if (roll <= 0f) {
                return best[index];
            }
        }

        return best[found - 1];
    }

    /// <summary>Fills a span with the best indices, best first. Returns how many scored above zero.</summary>
    /// <remarks>
    ///     A selection sort, because the span is at most a handful long and a sort that allocated
    ///     would allocate once per agent per decision.
    /// </remarks>
    static int Rank(ReadOnlySpan<float> scores, Span<int> best) {
        var found = 0;

        for (var slot = 0; slot < best.Length; slot++) {
            var pick = -1;
            var value = 0f;

            for (var index = 0; index < scores.Length; index++) {
                if (scores[index] <= value || Taken(best[..found], index)) {
                    continue;
                }

                pick = index;
                value = scores[index];
            }

            if (pick < 0) {
                break;
            }

            best[found++] = pick;
        }

        return found;
    }

    static bool Taken(ReadOnlySpan<int> chosen, int index) {
        foreach (var taken in chosen) {
            if (taken == index) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Weighted random among the best few.</summary>
sealed class TopWeightedRandomSelector : IUtilitySelector {
    /// <summary>How many of the best to consider, or zero to use <see cref="Fraction" />.</summary>
    public int Count { get; init; }

    /// <summary>What proportion of the set to consider.</summary>
    public float Fraction { get; init; } = 0.25f;

    public int Pick(in AgentContext context, UtilitySet options, ReadOnlySpan<float> scores) {
        ArgumentNullException.ThrowIfNull(options);

        var count = Count > 0
            ? Count
            : Math.Max(1, (int)MathF.Ceiling(scores.Length * Math.Clamp(Fraction, 0f, 1f)));

        return WeightedRandomSelector.Roll(in context, scores, count, 0x70D1CE);
    }
}

/// <summary>Dual utility: the best group wins, and only its members are considered.</summary>
/// <remarks>
///     ⚠ <b>This is what stops a guard being shot at from scoring "drink coffee".</b> With one flat
///     list, a very good ambient action beats a merely adequate emergency one — and the weights that
///     would prevent that have to be large enough that the emergency bucket outranks *any* combination
///     below it, which is a hard ordering by the back door. Choosing the group first makes the
///     comparison local: the best combat action against the other combat actions.
/// </remarks>
sealed class BucketedSelector : IUtilitySelector {
    public int Pick(in AgentContext context, UtilitySet options, ReadOnlySpan<float> scores) {
        ArgumentNullException.ThrowIfNull(options);

        // ⚠ The highest *bucket* with anything in it at all, and only then the best inside it. Taking
        // the best-scoring action's bucket instead would be Highest wearing a hat: the group would be
        // chosen by the same comparison the group exists to avoid making.
        var group = int.MinValue;

        for (var index = 0; index < scores.Length; index++) {
            if (scores[index] > 0f && options[index].Bucket > group) {
                group = options[index].Bucket;
            }
        }

        return group == int.MinValue ? -1 : UtilitySelectors.Best(scores, options, group);
    }
}
