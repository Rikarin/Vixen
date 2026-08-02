// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Constraints;

/// <summary>One rung of the ladder a governor walks down.</summary>
/// <param name="SolveEvery">Solve every <i>n</i>-th frame.</param>
/// <param name="Lod">Which detail level, which is also which chains are dropped.</param>
public readonly record struct ConstraintTier(int SolveEvery, byte Lod) {
    /// <inheritdoc />
    public override string ToString() =>
        SolveEvery == 1 && Lod == 0 ? "full" : $"1-in-{SolveEvery} at detail {Lod}";
}

/// <summary>What a governor decided, and what it could not fit.</summary>
/// <param name="Characters">How many were considered.</param>
/// <param name="Full">How many got a full solve every frame at full detail.</param>
/// <param name="Reduced">How many were put on a lower rung.</param>
/// <param name="AtFloor">How many were on the last rung and still did not fit.</param>
/// <param name="Estimated">What the plan costs, in goal-solves per frame.</param>
/// <param name="Budget">What it was allowed.</param>
/// <remarks>
///     ⚠ <b>The report is the deliverable, not a diagnostic bolted on.</b> D22's rate knob degrades a
///     character quietly — a held correction looks fine for a few frames and wrong after that — so a
///     governor that ran out of ladder and said nothing would be a frame budget met by a picture
///     nobody agreed to. <see cref="AtFloor" /> is the number that has to be zero, and
///     <see cref="ToString" /> is written to be pasted into a bug report.
/// </remarks>
public readonly record struct GovernorReport(
    int Characters,
    int Full,
    int Reduced,
    int AtFloor,
    float Estimated,
    float Budget
) {
    /// <summary>Whether the plan fits.</summary>
    public bool WithinBudget => Estimated <= Budget;

    /// <summary>How much over it is, in goal-solves per frame. Zero when it fits.</summary>
    public float Shortfall => MathF.Max(Estimated - Budget, 0f);

    /// <inheritdoc />
    public override string ToString() {
        var head = FormattableString.Invariant(
            $"{Characters} constrained characters, {Estimated:0.#} goal-solves a frame against a budget of {Budget:0.#}: {Full} at full rate, {Reduced} reduced"
        );

        return AtFloor == 0
            ? $"{head}, none at the floor."
            : FormattableString.Invariant(
                $"{head}, {AtFloor} at the floor and still over by {Shortfall:0.#}. The rate ladder is bounded; there is nothing left to give without dropping goals outright."
            );
    }
}

/// <summary>Decides what a frame can afford to solve, and says what it gave up.</summary>
/// <remarks>
///     <para>
///         <b>Three knobs, walked in one order.</b> The most important characters keep a full solve
///         every frame at full detail; the next band drops to every other frame and the coarse shape
///         set; the last drops further still. Which characters matter is
///         <see cref="ConstraintStack.Importance" />, which a game writes from distance — the governor
///         has no opinion about what matters and could not have one.
///     </para>
///     <para>
///         ⚠ <b>Goals, not microseconds.</b> The only honest per-goal time is one measured on the
///         machine the game is running on, and a budget in microseconds would be a number that means
///         something different on every device. What actually scales is the goal count, so that is
///         what is counted — and a project that has measured its own per-goal cost converts once,
///         at the point it sets <see cref="Budget" />.
///     </para>
///     <para>
///         ⚠ <b>The ladder is bounded and the bound is the point.</b> Holding a correction is right for
///         a few frames and wrong for many, so there is no rung below <see cref="Tiers" />'s last —
///         and when even that does not fit, the report says so instead of the governor inventing one.
///     </para>
/// </remarks>
public sealed class ConstraintGovernor {
    ConstraintStack[] ordered = [];
    float[] keys = [];

    /// <summary>How many goal-solves a frame may spend, across every character.</summary>
    public float Budget { get; set; } = 200f;

    /// <summary>The rungs, best first.</summary>
    /// <remarks>
    ///     Settable, because how far a project is willing to degrade is a project's decision — but the
    ///     list is finite by construction, which is the part that is not.
    /// </remarks>
    public ConstraintTier[] Tiers { get; set; } = [new(1, 0), new(2, 1), new(4, 2)];

    /// <summary>Assigns every character a rung, and reports what that cost.</summary>
    /// <param name="stacks">Every constrained character in the world.</param>
    /// <returns>What it decided.</returns>
    public GovernorReport Plan(ReadOnlySpan<ConstraintStack> stacks) {
        if (stacks.IsEmpty) {
            return new(0, 0, 0, 0, 0f, Budget);
        }

        if (ordered.Length < stacks.Length) {
            ordered = new ConstraintStack[Math.Max(16, stacks.Length)];
            keys = new float[ordered.Length];
        }

        stacks.CopyTo(ordered);

        var count = stacks.Length;

        // ⚠ Sorted on a parallel array of keys rather than with an IComparer, and that is not a
        // stylistic preference: `Array.Sort(T[], int, int, IComparer<T>)` allocates 64 bytes on every
        // call to wrap the comparer, which on a per-frame governor is six kilobytes a second. The key
        // is negated so that ascending order is most-important-first.
        for (var index = 0; index < count; index++) {
            keys[index] = -ordered[index].Importance;
        }

        Array.Sort(keys, ordered, 0, count);

        var tiers = Tiers.Length > 0 ? Tiers : [new ConstraintTier(1, 0)];
        var floor = Math.Max(tiers[^1].SolveEvery, 1);
        var owed = 0f;

        // ⚠ What everybody would cost on the last rung, worked out first. A governor that simply spent
        // the budget on the most important characters in order gives the first few everything and
        // strands the rest at the floor — a hundred characters that would all have fitted at
        // one-in-four reported as over budget, because thirty-seven of them took it all. Reserving the
        // floor for everyone still waiting is the difference between a plan and a queue.
        foreach (var stack in ordered.AsSpan(0, count)) {
            owed += MathF.Max(stack.EstimatedCost, 0) / floor;
        }

        var fits = owed <= Budget;
        var spent = 0f;
        var full = 0;
        var reduced = 0;
        var floored = 0;

        foreach (var stack in ordered.AsSpan(0, count)) {
            var cost = MathF.Max(stack.EstimatedCost, 0);
            var tier = tiers.Length - 1;

            owed -= cost / floor;

            // The best rung this character can have while everybody after it still gets the floor.
            for (var rung = 0; rung < tiers.Length; rung++) {
                if (spent + (cost / Math.Max(tiers[rung].SolveEvery, 1)) + owed <= Budget) {
                    tier = rung;
                    break;
                }
            }

            var chosen = tiers[tier];

            stack.SolveEvery = Math.Max(chosen.SolveEvery, 1);
            stack.Lod = chosen.Lod;
            spent += cost / stack.SolveEvery;

            if (tier == 0) {
                full++;
            } else {
                reduced++;
            }

            if (!fits && tier == tiers.Length - 1) {
                floored++;
            }
        }

        return new(count, full, reduced, floored, spent, Budget);
    }
}
