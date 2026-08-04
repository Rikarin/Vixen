// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Xunit;

namespace Vixen.Ai.Tests;

public class AgentGovernorTests {
    /// <summary>
    ///     P0's second exit criterion: the schedule is a pure function of the tick and the index.
    ///     Asserted rather than assumed, because an amortised scheduler is the one part of this
    ///     subsystem that is time-dependent by construction, and a replay has to reproduce it.
    /// </summary>
    [Fact]
    public void TheScheduleIsAPureFunctionOfTickAndIndex() {
        var first = new RoundRobinGovernor { Budget = 37, MaximumInterval = 5 };
        var second = new RoundRobinGovernor { Budget = 37, MaximumInterval = 5 };

        // Walked in opposite directions, so that anything the governor remembered between calls —
        // an accumulated cursor, a queue, an arrival order — would show up as a disagreement.
        for (var tick = 0; tick < 500; tick++) {
            var forward = first.Plan(tick, 400);
            var backward = second.Plan(499 - tick, 400);

            Assert.Equal(forward, first.Plan(tick, 400));
            Assert.Equal(second.Plan(499 - tick, 400), backward);
        }

        for (long tick = 0; tick < 200; tick++) {
            Assert.Equal(first.Plan(tick, 400), second.Plan(tick, 400));
        }
    }

    [Fact]
    public void ANegativeTickStillNamesAWindowInsideThePopulation() {
        var governor = new RoundRobinGovernor { Budget = 8, MaximumInterval = 100 };

        for (long tick = -50; tick < 0; tick++) {
            var schedule = governor.Plan(tick, 37);

            Assert.InRange(schedule.Start, 0, 36);
            Assert.Equal(8, schedule.Count);
        }
    }

    /// <summary>Nobody starves: every agent is in the window at least once per stated interval.</summary>
    [Theory]
    [InlineData(1, 4, 1000)]
    [InlineData(64, 128, 512)]
    [InlineData(97, 1000, 8)]
    [InlineData(400, 37, 5)]
    public void NoAgentStarvesOverAThousandFrames(int population, int budget, int interval) {
        var governor = new RoundRobinGovernor { Budget = budget, MaximumInterval = interval };
        var lastSeen = new long[population];

        Array.Fill(lastSeen, -1);

        var worst = 0L;

        for (long tick = 0; tick < 1_000; tick++) {
            var schedule = governor.Plan(tick, population);

            for (var index = 0; index < population; index++) {
                if (!schedule.Includes(index)) {
                    continue;
                }

                if (lastSeen[index] >= 0) {
                    worst = Math.Max(worst, tick - lastSeen[index]);
                }

                lastSeen[index] = tick;
            }
        }

        Assert.All(lastSeen, seen => Assert.True(seen >= 1_000 - interval, $"an agent last ticked at {seen}."));
        Assert.True(worst <= interval, $"an agent waited {worst} ticks against a floor of {interval}.");
    }

    [Fact]
    public void TheWindowSpendsTheBudgetAndNoMoreWhenTheFloorFits() {
        var governor = new RoundRobinGovernor { Budget = 16, MaximumInterval = 64 };
        var schedule = governor.Plan(3, 500);

        Assert.Equal(16, schedule.Count);
        Assert.False(schedule.OverBudget);
        Assert.Equal(484, schedule.Skipped);
    }

    /// <summary>
    ///     ⚠ The floor outranks the budget, and says so. An agent that reacts eight seconds late is
    ///     not a saving.
    /// </summary>
    [Fact]
    public void TheFloorWinsOverTheBudgetAndTheReportSaysSo() {
        var governor = new RoundRobinGovernor { Budget = 10, MaximumInterval = 4 };
        var schedule = governor.Plan(0, 400);

        Assert.Equal(100, schedule.Count);
        Assert.True(schedule.OverBudget);
        Assert.Equal(4, schedule.Interval);
        Assert.Contains("over budget", schedule.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyPopulationSchedulesNothing() {
        var schedule = new RoundRobinGovernor().Plan(12, 0);

        Assert.Equal(0, schedule.Count);
        Assert.False(schedule.Includes(0));
        Assert.False(schedule.OverBudget);
    }

    [Fact]
    public void AnIndexOutsideThePopulationIsNeverIncluded() {
        var schedule = new RoundRobinGovernor { Budget = 100 }.Plan(0, 4);

        Assert.False(schedule.Includes(-1));
        Assert.False(schedule.Includes(4));
        Assert.True(schedule.Includes(0));
    }

    [Fact]
    public void TheUnboundedGovernorTicksEverybody() {
        var schedule = new UnboundedGovernor().Plan(99, 2_048);

        Assert.Equal(2_048, schedule.Count);
        Assert.Equal(1, schedule.Interval);
        Assert.False(schedule.OverBudget);
        Assert.All(Enumerable.Range(0, 2_048), index => Assert.True(schedule.Includes(index)));
    }

    [Fact]
    public void ABudgetOrIntervalBelowOneIsRefused() {
        var governor = new RoundRobinGovernor();

        Assert.Throws<ArgumentOutOfRangeException>(() => governor.Budget = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => governor.MaximumInterval = 0);
    }
}
