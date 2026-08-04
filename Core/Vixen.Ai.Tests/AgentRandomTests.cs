// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

public class AgentRandomTests {
    [Fact]
    public void TheSameAgentStreamAndSaltAlwaysGiveTheSameNumber() {
        var entity = new Entity(41, 2, 0);

        Assert.Equal(AgentRandom.Value(entity, 7, 3), AgentRandom.Value(entity, 7, 3));
        Assert.NotEqual(AgentRandom.Value(entity, 7, 3), AgentRandom.Value(entity, 7, 4));
        Assert.NotEqual(AgentRandom.Value(entity, 7, 3), AgentRandom.Value(entity, 8, 3));
    }

    /// <summary>
    ///     ⚠ Two uses of randomness on one agent must not agree, or every agent that takes the first
    ///     branch also ticks its service early — a correlation that looks like behaviour.
    /// </summary>
    [Fact]
    public void DifferentSaltsAreUncorrelatedAcrossAPopulation() {
        var agreements = 0;

        for (var id = 1; id <= 512; id++) {
            var entity = new Entity(id, 1, 0);
            var first = AgentRandom.Value(entity, 1, 0);
            var second = AgentRandom.Value(entity, 1, 1);

            if (Math.Abs(first - second) < 0.01f) {
                agreements++;
            }
        }

        Assert.True(agreements < 20, $"{agreements} of 512 agents drew the same number twice.");
    }

    [Fact]
    public void ValuesStayInsideTheUnitInterval() {
        for (var id = 1; id <= 2_048; id++) {
            var value = AgentRandom.Value(new(id, 1, 0), 12345, 6);

            Assert.InRange(value, 0f, 0.9999999f);
        }
    }

    [Fact]
    public void RangeAndIndexStayInsideTheirBounds() {
        for (var id = 1; id <= 512; id++) {
            var entity = new Entity(id, 1, 0);

            Assert.InRange(AgentRandom.Range(entity, 1, 0, -3f, 5f), -3f, 5f);
            Assert.InRange(AgentRandom.Index(entity, 1, 0, 4), 0, 3);
        }

        Assert.Equal(-1, AgentRandom.Index(new(1, 1, 0), 1, 0, 0));
    }

    [Fact]
    public void ConsecutiveEntitiesDoNotGetConsecutiveSeeds() {
        var seeds = Enumerable.Range(1, 64).Select(id => AgentRandom.SeedOf(new(id, 1, 0))).ToArray();

        Assert.Equal(64, seeds.Distinct().Count());

        // The whole reason SeedOf hashes: a wave of guards spawned together should look like a
        // crowd, not like a sequence.
        var runs = seeds.Zip(seeds.Skip(1)).Count(pair => pair.Second == pair.First + 1);

        Assert.Equal(0, runs);
    }

    [Fact]
    public void AContextDrawsFromItsAgentsOwnStream() {
        var entity = new Entity(9, 1, 0);
        var context = new AgentContext(
            new("random-test"),
            entity,
            new(BlackboardLayout.Empty),
            null,
            GameTime.Zero,
            AgentRandom.SeedOf(entity)
        );

        Assert.Equal(AgentRandom.Value(entity, AgentRandom.SeedOf(entity), 2), context.Random(2));
    }
}

public class AgentDebugRecorderTests {
    [Fact]
    public void NothingIsRecordedUntilItIsTurnedOn() {
        var recorder = new AgentDebugRecorder();

        recorder.Record(Record(1, 0));

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public void TheRingKeepsTheMostRecentAndDropsTheOldest() {
        var recorder = new AgentDebugRecorder { Capacity = 4, Enabled = true };

        for (var tick = 0; tick < 10; tick++) {
            recorder.Record(Record(1, tick));
        }

        var buffer = new AgentDebugRecord[8];
        var written = recorder.CopyTo(buffer);

        Assert.Equal(4, written);
        Assert.Equal([6L, 7L, 8L, 9L], buffer.Take(4).Select(record => record.Tick));
    }

    [Fact]
    public void TheLatestRecordForAnAgentIsFound() {
        var recorder = new AgentDebugRecorder { Enabled = true };

        recorder.Record(Record(1, 0));
        recorder.Record(Record(2, 1));
        recorder.Record(Record(1, 2));

        Assert.True(recorder.TryGetLatest(new(1, 1, 0), out var latest));
        Assert.Equal(2, latest.Tick);
        Assert.False(recorder.TryGetLatest(new(3, 1, 0), out _));
    }

    [Fact]
    public void ARecordSaysWhatHappenedAndWhy() {
        var record = new AgentDebugRecord(
            new(4, 1, 0),
            17,
            AiPlanner.Utility,
            Symbol.Intern("drink-coffee"),
            ActionStatus.Running,
            2,
            5,
            Symbol.Intern("boredom"),
            0.62f
        );

        var text = record.ToString();

        Assert.Contains("drink-coffee", text, StringComparison.Ordinal);
        Assert.Contains("boredom", text, StringComparison.Ordinal);
        Assert.Contains("0.62", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingAndResizingForgetWhatWasRecorded() {
        var recorder = new AgentDebugRecorder { Enabled = true };

        recorder.Record(Record(1, 0));
        recorder.Clear();

        Assert.Equal(0, recorder.Count);

        recorder.Record(Record(1, 1));
        recorder.Capacity = 8;

        Assert.Equal(0, recorder.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.Capacity = 0);
    }

    static AgentDebugRecord Record(int id, long tick) =>
        new(new(id, 1, 0), tick, AiPlanner.None, Symbol.Intern("act"), ActionStatus.Running, 0, 1, Symbol.None, 0f);
}
