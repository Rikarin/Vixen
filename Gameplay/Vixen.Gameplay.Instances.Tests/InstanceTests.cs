// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Instances.Tests;

/// <summary>One dungeon on two difficulties, with a gate in the middle.</summary>
public static class Content {
    public const string Crypt = "instances/crypt";
    public const double Day = 86400d;
    public const double Week = Day * 7d;

    public static PlayerId Player(ulong who) => new(who);

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Instance.Heroic")
            .Add(
                Crypt,
                new InstanceDefinition {
                    DisplayName = "The Crypt",
                    Scene = "maps/crypt",
                    MinimumPlayers = 2,
                    MaximumPlayers = 5,
                    Difficulties = [
                        new() { Id = "normal", DisplayName = "Normal", Lockout = new() { Reset = LockoutReset.None } },
                        new() {
                            Id = "heroic",
                            DisplayName = "Heroic",
                            Tag = "Instance.Heroic",
                            HealthScale = 2f,
                            DamageScale = 1.5f,
                            Lockout = new() { Scope = LockoutScope.Character, Reset = LockoutReset.Weekly },
                            Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Instance.Heroic" }]
                        }
                    ],
                    Encounters = [
                        new() { Id = "gatekeeper", DisplayName = "The Gatekeeper", IsGate = true },
                        new() { Id = "warden", DisplayName = "The Warden", IsCheckpoint = false },
                        new() { Id = "lich", DisplayName = "The Lich King" }
                    ]
                }
            )
            .Build();
}

sealed class Attuned : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        value = 0f;

        return false;
    }
}

public class LockoutTests {
    [Fact]
    public void AWeeklyResetIsTheSameInstantForEverybody() {
        // ⚠ A duration from when somebody entered gives every player their own schedule, and a guild
        // cannot plan a raid night around a boundary that differs per member.
        var policy = new LockoutPolicy(new() { Reset = LockoutReset.Weekly });

        Assert.Equal(Content.Week, policy.NextResetAfter(0d));
        Assert.Equal(Content.Week, policy.NextResetAfter(Content.Day));
        Assert.Equal(Content.Week, policy.NextResetAfter(Content.Week - 1d));
        Assert.Equal(Content.Week * 2d, policy.NextResetAfter(Content.Week));
    }

    [Fact]
    public void ADailyResetIsTheDayBoundary() {
        var policy = new LockoutPolicy(new() { Reset = LockoutReset.Daily });

        Assert.Equal(Content.Day, policy.NextResetAfter(1d));
        Assert.Equal(Content.Day * 3d, policy.NextResetAfter(Content.Day * 2.5d));
    }

    [Fact]
    public void APolicyThatNeverResetsNeverLocksAnybodyOut() {
        var policy = new LockoutPolicy(new() { Reset = LockoutReset.None });

        Assert.False(policy.IsSome);
        Assert.Equal(double.PositiveInfinity, policy.NextResetAfter(0d));
    }

    [Fact]
    public void AStoreForgetsWhatHasLifted() {
        var store = new MemoryLockoutStore();
        var id = DefId.From(Content.Crypt);

        store.Save(new(Content.Player(1), id, "heroic", Content.Week, 1));

        Assert.NotNull(store.Find(Content.Player(1), id, "heroic"));
        Assert.Equal(0, store.Purge(Content.Week - 1d));
        Assert.Equal(1, store.Purge(Content.Week));
        Assert.Null(store.Find(Content.Player(1), id, "heroic"));
    }
}

public class InstanceRunTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly InstanceLibrary library;
    readonly MemoryLockoutStore lockouts = new();
    readonly Attuned attuned = new();

    public InstanceRunTests() {
        library = InstanceLibrary.Compile(catalog);
        attuned.Tags.Add(catalog.Tags.Resolve("Instance.Heroic"));
    }

    Instance Crypt => library.Find(DefId.From(Content.Crypt))!;

    static IReadOnlyList<PlayerId> Party(int size = 3) =>
        [.. Enumerable.Range(1, size).Select(who => Content.Player((ulong)who))];

    InstanceRun Enter(string difficulty = "normal", int size = 3, double now = 0d) {
        var refusal = InstanceRun.Enter(Crypt, difficulty, Party(size), lockouts, now, out var run, attuned);

        Assert.Equal(InstanceRefusal.None, refusal);

        return run!;
    }

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void AGroupOfTheWrongSizeIsRefused() {
        Assert.Equal(
            InstanceRefusal.BadSize,
            InstanceRun.Enter(Crypt, "normal", Party(1), lockouts, 0d, out _, attuned)
        );

        Assert.Equal(
            InstanceRefusal.BadSize,
            InstanceRun.Enter(Crypt, "normal", Party(9), lockouts, 0d, out _, attuned)
        );
    }

    [Fact]
    public void ADifficultysRequirementIsChecked() {
        Assert.Equal(
            InstanceRefusal.Requirements,
            InstanceRun.Enter(Crypt, "heroic", Party(), lockouts, 0d, out _, new Attuned())
        );

        Assert.Equal(InstanceRefusal.None, InstanceRun.Enter(Crypt, "heroic", Party(), lockouts, 0d, out _, attuned));
    }

    [Fact]
    public void AnUnknownDifficultyIsRefused() =>
        Assert.Equal(
            InstanceRefusal.Unknown,
            InstanceRun.Enter(Crypt, "mythic", Party(), lockouts, 0d, out _, attuned)
        );

    [Fact]
    public void ALockoutIsIssuedOnTheFirstDefeatRatherThanOnEntry() {
        // ⚠ Somebody who walked in and left has not used their week.
        var run = Enter("heroic");

        Assert.False(run.IsLocked);
        Assert.Null(lockouts.Find(Content.Player(1), Crypt.Id, "heroic"));

        run.Engage(0);
        run.Defeat(0, lockouts, 100d);

        Assert.True(run.IsLocked);
        Assert.Equal(Content.Week, lockouts.Find(Content.Player(1), Crypt.Id, "heroic")!.Value.Expires);
    }

    [Fact]
    public void ASecondKillOnTheSameRunDoesNotPushTheResetOut() {
        var run = Enter("heroic");

        run.Engage(0);
        run.Defeat(0, lockouts, 100d);

        var first = lockouts.Find(Content.Player(1), Crypt.Id, "heroic")!.Value;

        run.Engage(1);
        run.Defeat(1, lockouts, Content.Week - 1d);

        var after = lockouts.Find(Content.Player(1), Crypt.Id, "heroic")!.Value;

        Assert.Equal(first.Expires, after.Expires);
        Assert.Equal(1, after.Completions);
    }

    [Fact]
    public void EverybodyInThePartyIsLockedNotJustTheOneWhoSwung() {
        var run = Enter("heroic");

        run.Engage(0);
        run.Defeat(0, lockouts, 0d);

        foreach (var player in run.Participants) {
            Assert.NotNull(lockouts.Find(player, Crypt.Id, "heroic"));
        }
    }

    [Fact]
    public void OneLockedMemberRefusesTheWholeGroup() {
        // ⚠ Letting the rest in leaves somebody at the door while their party clears without them.
        lockouts.Save(new(Content.Player(2), Crypt.Id, "heroic", Content.Week, 1));

        Assert.Equal(
            InstanceRefusal.LockedOut,
            InstanceRun.Enter(Crypt, "heroic", Party(), lockouts, 0d, out _, attuned)
        );

        // And not on the difficulty they are not locked to.
        Assert.Equal(InstanceRefusal.None, InstanceRun.Enter(Crypt, "normal", Party(), lockouts, 0d, out _, attuned));
    }

    [Fact]
    public void ALockoutStopsBitingOnceItHasLifted() {
        lockouts.Save(new(Content.Player(2), Crypt.Id, "heroic", Content.Week, 1));

        Assert.Equal(
            InstanceRefusal.None,
            InstanceRun.Enter(Crypt, "heroic", Party(), lockouts, Content.Week, out _, attuned)
        );
    }

    [Fact]
    public void ADifficultyThatNeverResetsNeverLocksAnybodyIn() {
        var run = Enter();

        run.Engage(0);
        run.Defeat(0, lockouts, 0d);

        Assert.False(run.IsLocked);
        Assert.Equal(0, lockouts.Count);
    }

    [Fact]
    public void AGateMustFallBeforeAnythingBehindIt() {
        var run = Enter();

        Assert.Equal(InstanceRefusal.OutOfOrder, run.Engage(1));
        Assert.Equal(InstanceRefusal.OutOfOrder, run.Engage(2));
        Assert.Equal(InstanceRefusal.None, run.Engage(0));

        run.Defeat(0, lockouts, 0d);

        Assert.Equal(InstanceRefusal.None, run.Engage(2));
    }

    [Fact]
    public void AWipeResetsWhatWasBeingFoughtAndNothingElse() {
        // ⚠ A boss that is dead stays dead — that is what makes a raid night's progress progress.
        var run = Enter();

        run.Engage(0);
        run.Defeat(0, lockouts, 0d);
        run.Engage(1);

        Assert.Equal(1, run.Wipe());
        Assert.Equal(EncounterStatus.Defeated, run.StatusOf(0));
        Assert.Equal(EncounterStatus.Wiped, run.StatusOf(1));
        Assert.Equal(EncounterStatus.Waiting, run.StatusOf(2));
        Assert.Equal(1, run.Defeated);
    }

    [Fact]
    public void TheCheckpointIsTheFurthestOneBeatenAndSkipsFightsThatAreNotOne() {
        var run = Enter();

        Assert.Equal(-1, run.Checkpoint);

        run.Engage(0);
        run.Defeat(0, lockouts, 0d);

        Assert.Equal(0, run.Checkpoint);

        // The warden is authored IsCheckpoint: false, so beating it does not move the mark.
        run.Engage(1);
        run.Defeat(1, lockouts, 0d);

        Assert.Equal(0, run.Checkpoint);
    }

    [Fact]
    public void EveryAttemptIsCounted() {
        var run = Enter();

        run.Engage(0);
        run.Wipe();
        run.Engage(0);
        run.Wipe();
        run.Engage(0);
        run.Defeat(0, lockouts, 0d);

        Assert.Equal(3, run.AttemptsOn(0));
    }

    [Fact]
    public void ClearingEverythingIsClearingEverything() {
        var run = Enter();

        Assert.False(run.IsCleared);

        for (var encounter = 0; encounter < 3; encounter++) {
            run.Engage(encounter);
            run.Defeat(encounter, lockouts, 0d);
        }

        Assert.True(run.IsCleared);
    }

    [Fact]
    public void AClosedRunDoesNothingMore() {
        var run = Enter();

        run.Close();

        Assert.Equal(InstanceRefusal.Closed, run.Engage(0));
        Assert.Equal(InstanceRefusal.Closed, run.Defeat(0, lockouts, 0d));
        Assert.Equal(0, run.Wipe());
    }

    [Fact]
    public void ADefeatedFightIsNotFoughtAgain() {
        var run = Enter();

        run.Engage(0);
        run.Defeat(0, lockouts, 0d);

        Assert.Equal(InstanceRefusal.OutOfOrder, run.Engage(0));
        Assert.Equal(InstanceRefusal.Unknown, run.Defeat(0, lockouts, 0d));
    }

    [Fact]
    public void TheDifficultyIsFixedForTheLifeOfTheRun() {
        // ⚠ There is deliberately no way to change it: a lockout is per (instance, difficulty), so a
        // group that could switch halfway would have one lockout covering two.
        var run = Enter("heroic");

        Assert.Equal("heroic", run.Difficulty.Id);
        Assert.Equal(2f, run.Difficulty.HealthScale);
        Assert.DoesNotContain(
            typeof(InstanceRun).GetMethods(),
            method => method.Name.Contains("Difficulty", StringComparison.Ordinal) && method.Name.StartsWith("set_", StringComparison.Ordinal)
        );
    }
}

public class InstanceLibraryTests {
    static IReadOnlyList<string> Problems(InstanceDefinition definition) =>
        InstanceLibrary.Compile(new DefinitionCatalogBuilder().Add("instances/odd", definition).Build()).Problems;

    [Fact]
    public void AnInstanceWithNoDifficultiesIsAProblem() =>
        Assert.Contains(Problems(new()), problem => problem.Contains("no difficulties", StringComparison.Ordinal));

    [Fact]
    public void ASizeNoGroupSatisfiesIsAProblem() =>
        Assert.Contains(
            Problems(new() { MinimumPlayers = 10, MaximumPlayers = 5, Difficulties = [new() { Id = "n" }] }),
            problem => problem.Contains("no group satisfies", StringComparison.Ordinal)
        );

    [Fact]
    public void TwoDifficultiesWithOneIdIsAProblem() =>
        Assert.Contains(
            Problems(new() { Difficulties = [new() { Id = "n" }, new() { Id = "n" }] }),
            problem => problem.Contains("two difficulties called 'n'", StringComparison.Ordinal)
        );

    [Fact]
    public void AnInstanceWithNoCheckpointIsAProblem() =>
        Assert.Contains(
            Problems(
                new() {
                    Difficulties = [new() { Id = "n" }],
                    Encounters = [new() { Id = "a", IsCheckpoint = false }]
                }
            ),
            problem => problem.Contains("no checkpoint", StringComparison.Ordinal)
        );

    [Fact]
    public void TwoEncountersWithOneIdIsAProblem() =>
        Assert.Contains(
            Problems(
                new() {
                    Difficulties = [new() { Id = "n" }],
                    Encounters = [new() { Id = "a" }, new() { Id = "a" }]
                }
            ),
            problem => problem.Contains("two encounters called 'a'", StringComparison.Ordinal)
        );
}
