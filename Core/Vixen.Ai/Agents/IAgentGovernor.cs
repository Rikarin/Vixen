// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Ai;

/// <summary>Who gets to think this tick, and what that cost.</summary>
/// <param name="Tick">The tick this plan is for.</param>
/// <param name="Population">How many agents there are.</param>
/// <param name="Start">The first agent index in the window.</param>
/// <param name="Count">How many agents are in it.</param>
/// <param name="Budget">What the governor was allowed.</param>
/// <param name="Interval">
///     How many ticks it takes for the window to come back round to an agent — the floor an agent is
///     actually getting.
/// </param>
/// <remarks>
///     <para>
///         <b>A window rather than a list, because the list is derivable and the window is eight
///         bytes.</b> A plan that enumerated its agents would allocate once a frame per world, which
///         is the one thing this subsystem's exit criterion forbids.
///     </para>
///     <para>
///         ⚠ <b>The report is the deliverable, not a diagnostic.</b> A governor that quietly halved
///         everybody's reaction time is a frame budget met by an AI nobody agreed to.
///         <see cref="OverBudget" /> and <see cref="Interval" /> are what a project reads to find out
///         what the number it set actually bought, and <see cref="ToString" /> is written to be
///         pasted into a bug report.
///     </para>
/// </remarks>
public readonly record struct AgentSchedule(
    long Tick,
    int Population,
    int Start,
    int Count,
    int Budget,
    int Interval
) {
    /// <summary>Whether the plan spends more than it was allowed.</summary>
    /// <remarks>
    ///     True when the floor cost more than the budget — which is a governor refusing to starve an
    ///     agent rather than a governor failing. What it means is that the budget is too small for
    ///     the population, and it is said out loud rather than absorbed.
    /// </remarks>
    public bool OverBudget => Count > Budget;

    /// <summary>How many agents do not get a slot this tick.</summary>
    public int Skipped => Population - Count;

    /// <summary>Whether an agent is in this tick's window.</summary>
    /// <param name="index">Its schedule index.</param>
    /// <returns>Whether it thinks this tick.</returns>
    public bool Includes(int index) {
        if (Count <= 0 || Population <= 0 || (uint)index >= (uint)Population) {
            return false;
        }

        // The window wraps, so the test is on the distance from its start rather than on a pair of
        // bounds — two integer operations, and no special case for the tick where it straddles zero.
        var offset = index - Start;

        if (offset < 0) {
            offset += Population;
        }

        return offset < Count;
    }

    /// <inheritdoc />
    public override string ToString() {
        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"tick {Tick}: {Count} of {Population} agents against a budget of {Budget}, one turn every {Interval} ticks"
        );

        return OverBudget
            ? $"{head} — over budget by {Count - Budget}, because the floor is worth more than the budget."
            : $"{head}.";
    }
}

/// <summary>Decides which agents may think this tick.</summary>
/// <remarks>
///     <para>
///         Behaviour-tree steps are cheap and utility scoring is bounded, but neither is free, and a
///         thousand agents doing both every frame is a frame nobody gets back. The governor is the
///         one place that decides who is worth it, and it sits above all three planners because
///         doc 37 § D2 made the agent one shape.
///     </para>
///     <para>
///         ⚠ <b>Plan must be a pure function of its arguments.</b> An amortised scheduler is
///         time-dependent by construction, which is a real hole in determinism — so the hole is
///         bounded by making the schedule reproducible: given the same tick and the same population,
///         every machine picks the same agents. Not arrival order, not a queue, not a priority sort
///         on a float. docs/plan/37 § D18 states this rather than leaving it to be discovered as a
///         desync six months in, and <c>AgentGovernorTests</c> asserts it.
///     </para>
/// </remarks>
public interface IAgentGovernor {
    /// <summary>Works out this tick's window.</summary>
    /// <param name="tick">The tick number.</param>
    /// <param name="population">How many agents there are.</param>
    /// <returns>The plan.</returns>
    AgentSchedule Plan(long tick, int population);
}

/// <summary>
///     A window that walks the population, wide enough to keep everybody inside a stated interval.
/// </summary>
/// <remarks>
///     <para>
///         <b>Round-robin rather than most-important-first, and doc 34's governor is why.</b>
///         Spending a budget on the most important characters in order gave the first thirty-seven
///         everything and stranded the rest — a plan is not a queue. Here every agent is in the
///         window exactly once per pass, so an agent that misses its slot ticks later rather than
///         never.
///     </para>
///     <para>
///         ⚠ <b><see cref="MaximumInterval" /> is a floor, and it outranks the budget.</b> A
///         population that cannot fit inside the interval at the budgeted width gets a wider window
///         and an <see cref="AgentSchedule.OverBudget" /> plan, because an agent that reacts eight
///         seconds late is not a saving — it is a bug report about the AI being broken. The budget
///         bounds the ordinary case; the floor bounds the bad one.
///     </para>
///     <para>
///         Importance is deliberately absent. What matters is a game's to say, and the seam for it
///         is this interface — a distance-LOD governor that widens the window near the player is
///         doc 37's second shipped implementation and lands with perception, where there are
///         positions to read.
///     </para>
/// </remarks>
public sealed class RoundRobinGovernor : IAgentGovernor {
    int budget = 128;
    int maximumInterval = 8;

    /// <summary>How many agents may think in one tick, in the ordinary case.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to less than one.</exception>
    public int Budget {
        get => budget;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            budget = value;
        }
    }

    /// <summary>The most ticks an agent may wait for its turn.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to less than one.</exception>
    public int MaximumInterval {
        get => maximumInterval;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            maximumInterval = value;
        }
    }

    /// <inheritdoc />
    public AgentSchedule Plan(long tick, int population) {
        if (population <= 0) {
            return new(tick, 0, 0, 0, Budget, 1);
        }

        // What the floor costs: everybody, spread over the interval, rounded up so that the last
        // agent is inside it rather than one tick past it.
        var floor = (population + MaximumInterval - 1) / MaximumInterval;
        var width = Math.Min(population, Math.Max(Budget, floor));

        // Pure in `tick` and `population`, which is the whole contract. Multiplied rather than
        // accumulated: a running start would depend on how many ticks this governor object had seen,
        // and a replay that started later would schedule differently.
        var start = (int)(((tick % population) * width) % population);

        if (start < 0) {
            start += population;
        }

        return new(tick, population, start, width, Budget, (population + width - 1) / width);
    }
}

/// <summary>Everybody, every tick.</summary>
/// <remarks>
///     For tests, for tools, and for a game with a dozen agents where amortising is a complication
///     rather than a saving. It is also the control the budgeted governor is measured against: a
///     claim about what a budget saves means nothing without the unbudgeted number beside it.
/// </remarks>
public sealed class UnboundedGovernor : IAgentGovernor {
    /// <inheritdoc />
    public AgentSchedule Plan(long tick, int population) =>
        new(tick, Math.Max(population, 0), 0, Math.Max(population, 0), Math.Max(population, 1), 1);
}
