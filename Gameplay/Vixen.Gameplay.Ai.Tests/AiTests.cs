// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Ai.Tests;

public class LeashTests {
    static Leash Tethered(float patience = 5f) =>
        new(new() { Tether = 40f, Break = 60f, Patience = patience });

    [Fact]
    public void InsideTheTetherNothingHappens() {
        var leash = Tethered();
        var verdict = leash.Check(10f, 0f);

        Assert.Equal(LeashState.Held, verdict.State);
        Assert.False(verdict.Changed);
        Assert.False(verdict.ShouldReset);
    }

    [Fact]
    public void PastTheTetherItIsStretchedAndNotYetBroken() {
        var leash = Tethered();
        var verdict = leash.Check(50f, 0f);

        Assert.Equal(LeashState.Stretched, verdict.State);
        Assert.True(verdict.Changed);
        Assert.False(verdict.ShouldReset);
    }

    [Fact]
    public void PastTheBreakItGoesHome() {
        var leash = Tethered();
        var verdict = leash.Check(70f, 0f);

        Assert.Equal(LeashState.Broken, verdict.State);
        Assert.True(verdict.ShouldReset);
    }

    [Fact]
    public void ComingBackInsideTheTetherClearsItAndComingBackInsideTheBreakDoesNot() {
        // ⚠ The hysteresis. One radius makes a mob on the boundary flicker once a frame.
        var leash = Tethered();

        leash.Check(50f, 0f);

        Assert.Equal(LeashState.Stretched, leash.Check(55f, 1f).State);
        Assert.Equal(LeashState.Stretched, leash.Check(45f, 2f).State);
        Assert.Equal(LeashState.Held, leash.Check(39f, 3f).State);
    }

    [Fact]
    public void BeingStretchedForTooLongBreaksItAnyway() {
        // ⚠ What stops a mob being kited round a pillar for ever at exactly tether plus one.
        var leash = Tethered(patience: 5f);

        leash.Check(50f, 0f);

        Assert.Equal(LeashState.Stretched, leash.Check(50f, 4f).State);
        Assert.Equal(4f, leash.StretchedFor(4f));

        var verdict = leash.Check(50f, 5f);

        Assert.Equal(LeashState.Broken, verdict.State);
        Assert.True(verdict.ShouldReset);
    }

    [Fact]
    public void NoPatienceMeansItWaitsForEver() {
        var leash = Tethered(patience: 0f);

        leash.Check(50f, 0f);

        Assert.Equal(LeashState.Stretched, leash.Check(50f, 10_000f).State);
    }

    [Fact]
    public void ComingBackResetsTheClock() {
        var leash = Tethered(patience: 5f);

        leash.Check(50f, 0f);
        leash.Check(10f, 3f);

        Assert.Equal(0f, leash.StretchedFor(3f));

        leash.Check(50f, 4f);

        Assert.Equal(LeashState.Stretched, leash.Check(50f, 8f).State);
    }

    [Fact]
    public void ReleasingPutsItBack() {
        var leash = Tethered();

        leash.Check(70f, 0f);

        Assert.Equal(LeashState.Broken, leash.State);

        leash.Release();

        Assert.Equal(LeashState.Held, leash.State);
    }

    [Fact]
    public void ABreakInsideTheTetherIsClampedRatherThanInverted() {
        var leash = new Leash(new() { Tether = 60f, Break = 10f });

        Assert.Equal(60f, leash.Break);
        Assert.Equal(LeashState.Held, leash.Check(50f, 0f).State);
    }

    [Fact]
    public void ItHealsOnResetUnlessSomebodyTurnedThatOff() {
        // ⚠ A mob that keeps its damage across a reset is whittled down over a dozen pulls by one
        // player who never has to win a fight.
        Assert.True(new LeashDefinition().HealsOnReset);
    }
}

/// <summary>A camp of bandits and a lone champion.</summary>
public static class Content {
    public const string Camp = "spawns/camp";
    public const string Champion = "spawns/champion";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add(
                Camp,
                new SpawnTableDefinition {
                    DisplayName = "Bandit camp",
                    Cap = 3,
                    RespawnSeconds = 30f,
                    RespawnJitter = 5f,
                    Entries = [
                        new() { Creature = "creatures/thug", Weight = 3f, Minimum = 1, Maximum = 2 },
                        new() { Creature = "creatures/archer", Weight = 1f }
                    ]
                }
            )
            .Add(
                Champion,
                new SpawnTableDefinition {
                    DisplayName = "Champion",
                    Cap = 1,
                    RespawnSeconds = 600f,
                    RespawnJitter = 0f,
                    Entries = [new() { Creature = "creatures/champion" }]
                }
            )
            .Build();
}

public class SpawnTests {
    readonly SpawnLibrary library = SpawnLibrary.Compile(Content.Catalog());

    Spawner Spawner(string address, ulong seed = 1ul) => new(library.Find(DefId.From(address))!, seed);

    static List<SpawnOrder> Orders(Spawner spawner, float now) {
        var orders = new List<SpawnOrder>();

        spawner.Tick(now, orders);

        return orders;
    }

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void AFreshSpawnerFillsToItsCapAtOnce() {
        var camp = Spawner(Content.Camp);
        var orders = Orders(camp, 0f);

        Assert.Equal(3, orders.Count);
        Assert.Equal(3, camp.Alive);
        Assert.True(camp.IsFull);
        Assert.Empty(Orders(camp, 1f));
    }

    [Fact]
    public void TheCapCountsWhatIsAliveRatherThanWhatHasBeenSpawned() {
        // ⚠ Counting spawns makes a camp that has been cleared twice permanently empty.
        var camp = Spawner(Content.Camp);

        Orders(camp, 0f);

        for (var slot = 0; slot < 3; slot++) {
            camp.Died(slot, 0f);
        }

        Assert.Equal(0, camp.Alive);
        Assert.Equal(3, Orders(camp, 100f).Count);
        Assert.Equal(3, camp.Alive);
    }

    [Fact]
    public void ARespawnTimerStartsAtTheDeathRatherThanAtTheTickThatNoticed() {
        // ⚠ A server that fell behind would otherwise repopulate faster than one that did not.
        var camp = Spawner(Content.Champion);

        Orders(camp, 0f);
        camp.Died(0, 10f);

        Assert.Equal(610f, camp.DueAt(0), 3);
        Assert.Empty(Orders(camp, 600f));
        Assert.Single(Orders(camp, 610f));
    }

    [Fact]
    public void JitterMovesTheTimerAndIsDeterministic() {
        var first = Spawner(Content.Camp, seed: 42ul);
        var second = Spawner(Content.Camp, seed: 42ul);

        Orders(first, 0f);
        Orders(second, 0f);
        first.Died(0, 0f);
        second.Died(0, 0f);

        Assert.Equal(first.DueAt(0), second.DueAt(0));
        Assert.InRange(first.DueAt(0), 25f, 35f);
    }

    [Fact]
    public void TwoSeedsGiveTwoCampsThatDoNotMarchInStep() {
        var first = Spawner(Content.Camp, seed: 1ul);
        var second = Spawner(Content.Camp, seed: 2ul);

        Orders(first, 0f);
        Orders(second, 0f);
        first.Died(0, 0f);
        second.Died(0, 0f);

        Assert.NotEqual(first.DueAt(0), second.DueAt(0));
    }

    [Fact]
    public void ADeadSlotCannotDieTwice() {
        var camp = Spawner(Content.Camp);

        Orders(camp, 0f);

        Assert.True(camp.Died(0, 0f));
        Assert.False(camp.Died(0, 0f));
        Assert.False(camp.Died(9, 0f));
    }

    [Fact]
    public void AResetEmptiesItAndMakesEverythingDueAtOnce() {
        var camp = Spawner(Content.Camp);

        Orders(camp, 0f);
        camp.Reset(50f);

        Assert.Equal(0, camp.Alive);
        Assert.Equal(3, Orders(camp, 50f).Count);
    }

    [Fact]
    public void CountsComeFromTheRangeAndTheWeightsAreRespected() {
        var thugs = 0;
        var archers = 0;
        var counts = new HashSet<int>();

        for (var seed = 0ul; seed < 300ul; seed++) {
            var camp = Spawner(Content.Camp, seed);

            foreach (var order in Orders(camp, 0f)) {
                counts.Add(order.Count);

                if (order.Creature == DefId.From("creatures/thug")) {
                    thugs++;
                } else {
                    archers++;
                }
            }
        }

        // Three to one, so thugs dominate but archers are not absent.
        Assert.True(thugs > archers * 2, $"{thugs} thugs to {archers} archers");
        Assert.True(archers > 100, $"only {archers} archers in 900 spawns");
        Assert.Equal([1, 2], counts.Order());
    }

    [Fact]
    public void ATableWithNoEntriesIsAProblemAndSpawnsNothing() {
        var bare = SpawnLibrary.Compile(
            new DefinitionCatalogBuilder().Add("spawns/bare", new SpawnTableDefinition()).Build()
        );

        Assert.Contains(bare.Problems, problem => problem.Contains("nothing ever spawns", StringComparison.Ordinal));
        Assert.Empty(Orders(new(bare.Find(DefId.From("spawns/bare"))!, 1ul), 0f));
    }

    [Fact]
    public void AWeightlessRowIsAProblemAndIsDropped() {
        var odd = SpawnLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add(
                    "spawns/odd",
                    new SpawnTableDefinition {
                        Entries = [new() { Creature = "creatures/a", Weight = 0f }, new() { Creature = "creatures/b" }]
                    }
                )
                .Build()
        );

        Assert.Contains(odd.Problems, problem => problem.Contains("never be picked", StringComparison.Ordinal));
        Assert.Single(odd.Find(DefId.From("spawns/odd"))!.Entries.ToArray());
    }

    [Fact]
    public void JitterLargerThanTheRespawnIsAProblem() {
        var problems = SpawnLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "spawns/odd",
                        new SpawnTableDefinition {
                            RespawnSeconds = 5f,
                            RespawnJitter = 30f,
                            Entries = [new() { Creature = "creatures/a" }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("before it died", StringComparison.Ordinal));
    }

    [Fact]
    public void ARangeThatIsNoRangeIsAProblem() {
        var problems = SpawnLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "spawns/odd",
                        new SpawnTableDefinition {
                            Entries = [new() { Creature = "creatures/a", Minimum = 5, Maximum = 2 }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("no number at all", StringComparison.Ordinal));
    }
}
