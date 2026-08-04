// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Tests;

public class GameplayRandomTests {
    [Fact]
    public void ADropIsReproducibleFromItsEventId() {
        var first = GameplayRandom.For(0x5eed_1234_abcd_0001ul);
        var second = GameplayRandom.For(0x5eed_1234_abcd_0001ul);

        for (var draw = 0; draw < 1000; draw++) {
            Assert.Equal(first.NextUInt(), second.NextUInt());
        }
    }

    [Fact]
    public void TwoRollsWithinOneEventAreDifferentStreams() {
        var item = GameplayRandom.For(7, 0);
        var quality = GameplayRandom.For(7, 1);

        var same = 0;

        for (var draw = 0; draw < 100; draw++) {
            if (item.NextUInt() == quality.NextUInt()) {
                same++;
            }
        }

        Assert.Equal(0, same);
    }

    [Fact]
    public void SeedsThatWouldCancelUnderAnXorDoNot() {
        // Vixen.Ai's AgentRandom shipped with `hash ^ seed`, which for a caller that seeded from the
        // same hash was zero for every agent in the world. The mixer has no such pair, so a thousand
        // (id, salt) combinations where the two are equal must still be a thousand streams.
        var seen = new HashSet<uint>();

        for (var value = 0ul; value < 1000; value++) {
            var random = GameplayRandom.For(value, value);
            seen.Add(random.NextUInt());
        }

        Assert.Equal(1000, seen.Count);
    }

    [Fact]
    public void AStreamResumesExactlyWhereItsStateSaysItIs() {
        var random = new GameplayRandom(42);

        for (var draw = 0; draw < 17; draw++) {
            random.NextUInt();
        }

        var resumed = GameplayRandom.Resume(random.State);

        for (var draw = 0; draw < 50; draw++) {
            Assert.Equal(random.NextUInt(), resumed.NextUInt());
        }
    }

    [Fact]
    public void FloatsAreInsideTheHalfOpenUnitInterval() {
        var random = new GameplayRandom(1);
        var highest = 0f;

        for (var draw = 0; draw < 200000; draw++) {
            var value = random.NextFloat();

            Assert.True(value is >= 0f and < 1f, $"{value} is outside [0, 1)");
            highest = MathF.Max(highest, value);
        }

        // Close enough to one that the interval is genuinely being covered, and never equal to it.
        Assert.True(highest > 0.999f);
    }

    [Fact]
    public void AGuaranteedChanceAlwaysHappensAndAnImpossibleOneNever() {
        var random = new GameplayRandom(3);

        for (var draw = 0; draw < 10000; draw++) {
            Assert.True(random.Chance(1f));
            Assert.False(random.Chance(0f));
        }
    }

    [Fact]
    public void BoundedIntegersStayInRangeAndCoverIt() {
        var random = new GameplayRandom(5);
        var seen = new bool[7];

        for (var draw = 0; draw < 10000; draw++) {
            var value = random.NextInt(7);

            Assert.InRange(value, 0, 6);
            seen[value] = true;
        }

        Assert.All(seen, Assert.True);
        Assert.Equal(0, random.NextInt(0));
        Assert.InRange(random.NextInt(10, 20), 10, 19);
    }

    [Fact]
    public void ADrawIsUniformEnoughToBalanceALootTableAgainst() {
        var random = new GameplayRandom(11);
        var buckets = new int[16];
        const int Draws = 320000;

        for (var draw = 0; draw < Draws; draw++) {
            buckets[random.NextInt(buckets.Length)]++;
        }

        var expected = Draws / buckets.Length;

        foreach (var count in buckets) {
            // Two per cent, which is about eleven standard deviations at this sample size — loose
            // enough never to flake and tight enough that a modulo bias of one part in a hundred
            // would fail it.
            Assert.InRange(count, expected * 0.98, expected * 1.02);
        }
    }

    [Fact]
    public void PickHonoursTheWeights() {
        var random = new GameplayRandom(13);
        float[] weights = [1f, 3f, 0f, 6f];
        var picks = new int[4];

        for (var draw = 0; draw < 200000; draw++) {
            picks[random.Pick(weights)]++;
        }

        Assert.Equal(0, picks[2]);
        Assert.InRange(picks[0] / 200000f, 0.09, 0.11);
        Assert.InRange(picks[1] / 200000f, 0.29, 0.31);
        Assert.InRange(picks[3] / 200000f, 0.59, 0.61);
    }

    [Fact]
    public void PickWithNothingToPickSaysSo() {
        var random = new GameplayRandom(17);

        Assert.Equal(-1, random.Pick([]));
        Assert.Equal(-1, random.Pick([0f, 0f]));
        Assert.Equal(-1, random.Pick([-1f, -2f]));
        Assert.Equal(1, random.Pick([0f, 5f, 0f]));
    }

    [Fact]
    public void TheStreamIsTheSameOnEveryRunOfEveryBuild() {
        // Pinned. The whole value of a reproducible roll is that it is reproducible next year, so a
        // change to the generator has to fail here and be a decision rather than a surprise.
        var random = GameplayRandom.For(1, 0);

        Assert.Equal(350055199u, random.NextUInt());
        Assert.Equal(3718350913u, random.NextUInt());
        Assert.Equal(3895503312u, random.NextUInt());
    }
}
