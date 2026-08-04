// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Pvp.Tests;

/// <summary>A three-point battleground and a best-of-three arena.</summary>
public static class Content {
    public const string Basin = "pvp/basin";
    public const string Ring = "pvp/ring";
    public const string Photo = "pvp/photo";

    public static PlayerId Player(ulong who) => new(who);

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag(PvpModule.Flagged)
            .Add(
                Basin,
                new PvpMapDefinition {
                    DisplayName = "The Basin",
                    Kind = MatchKind.Battleground,
                    Scene = "maps/basin",
                    Teams = 2,
                    TeamSize = 10,
                    ScoreToWin = 100,
                    TimeLimit = 600f,
                    Tag = "Pvp.InMatch",
                    Objectives = [
                        new() { Id = "mine", Kind = PvpObjectiveKind.ResourceControl, CaptureSeconds = 4f, PointsPerTick = 1, TickSeconds = 1f },
                        new() { Id = "farm", Kind = PvpObjectiveKind.ResourceControl, CaptureSeconds = 4f, PointsPerTick = 1, TickSeconds = 1f, StartingOwner = 0 },
                        new() { Id = "flag", Kind = PvpObjectiveKind.FlagReturn, CaptureSeconds = 2f, PointsPerTick = 0, PointsOnCapture = 25 }
                    ]
                }
            )
            .Add(
                Ring,
                new PvpMapDefinition {
                    DisplayName = "The Ring",
                    Kind = MatchKind.Arena,
                    Teams = 2,
                    TeamSize = 3,
                    ScoreToWin = 10,
                    TimeLimit = 60f,
                    Rounds = 3,
                    Objectives = [
                        new() { Id = "centre", Kind = PvpObjectiveKind.CapturePoint, CaptureSeconds = 1f, PointsPerTick = 5, TickSeconds = 1f }
                    ]
                }
            )
            .Add(
                Photo,
                // Ten points at one a second with a ten-second clock, so the winning score lands on
                // exactly the tick the clock runs out — which is the only way to test their order.
                new PvpMapDefinition {
                    DisplayName = "Photo Finish",
                    Kind = MatchKind.Battleground,
                    Teams = 2,
                    ScoreToWin = 10,
                    TimeLimit = 10f,
                    Objectives = [
                        new() { Id = "point", Kind = PvpObjectiveKind.CapturePoint, CaptureSeconds = 0.1f, PointsPerTick = 1, TickSeconds = 1f }
                    ]
                }
            )
            .Build();
}

public class PvpMatchTests {
    readonly PvpLibrary library = PvpLibrary.Compile(Content.Catalog());

    PvpMatch Basin() {
        var match = new PvpMatch(library.Find(DefId.From(Content.Basin))!);

        match.Join(Content.Player(1), 0);
        match.Join(Content.Player(2), 1);

        return match;
    }

    static void Stand(PvpMatch match, int objective, int red, int blue) =>
        match.Occupy(objective, [red, blue]);

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void AnObjectiveStartsOwnedByWhoeverTheContentSays() {
        var match = Basin();

        Assert.Equal(-1, match.Objectives[0].Owner);
        Assert.Equal(0, match.Objectives[1].Owner);
        Assert.Equal(1f, match.Objectives[1].Progress);
    }

    [Fact]
    public void StandingOnANeutralPointTakesItAfterItsCaptureTime() {
        var match = Basin();

        Stand(match, 0, 1, 0);
        match.Tick(3f);

        Assert.Equal(-1, match.Objectives[0].Owner);

        match.Tick(1.1f);

        Assert.Equal(0, match.Objectives[0].Owner);
    }

    [Fact]
    public void AContestedObjectiveDoesNotMoveInEitherDirection() {
        // ⚠ Frozen, not "the bigger group wins slowly". The alternative makes numbers the whole game
        // and makes standing on a point you already hold worth doing.
        var match = Basin();

        Stand(match, 0, 5, 1);
        match.Tick(30f);

        Assert.Equal(-1, match.Objectives[0].Owner);
        Assert.Equal(0f, match.Objectives[0].Progress);
        Assert.True(match.Objectives[0].IsContested);
    }

    [Fact]
    public void TakingAPointBackHasToPassThroughNeutral() {
        // ⚠ Two per-team meters would flip it the instant the last defender dies.
        var match = Basin();

        Stand(match, 1, 0, 1);
        match.Tick(2f);

        // Halfway through pulling the owner's meter down: still theirs, and nobody else's yet.
        Assert.Equal(0, match.Objectives[1].Owner);

        match.Tick(2.1f);

        Assert.Equal(-1, match.Objectives[1].Owner);
        Assert.Equal(0f, match.Objectives[1].Progress);

        match.Tick(4.1f);

        Assert.Equal(1, match.Objectives[1].Owner);
    }

    [Fact]
    public void HoldingAPointScoresPerTick() {
        var match = Basin();

        Stand(match, 1, 1, 0);
        match.Tick(5f);

        Assert.Equal(5, match.ScoreOf(0));
        Assert.Equal(0, match.ScoreOf(1));
    }

    [Fact]
    public void ANeutralPointScoresForNobody() {
        var match = Basin();

        match.Tick(20f);

        Assert.Equal(20, match.ScoreOf(0));
        Assert.Equal(0, match.ScoreOf(1));
    }

    [Fact]
    public void ACaptureCanPayOnceInsteadOfTicking() {
        var match = Basin();

        Stand(match, 2, 0, 1);
        match.Tick(2.1f);

        Assert.Equal(1, match.Objectives[2].Owner);
        Assert.Equal(25, match.ScoreOf(1));

        match.Tick(10f);

        // It ticks nothing, so holding it adds nothing more.
        Assert.Equal(25, match.ScoreOf(1));
    }

    [Fact]
    public void ReachingTheScoreWinsIt() {
        var match = Basin();

        Stand(match, 1, 1, 0);
        match.Tick(99f);

        Assert.False(match.IsOver);

        Assert.True(match.Tick(2f));
        Assert.Equal(MatchOutcome.Score, match.Outcome);
        Assert.Equal(0, match.Winner);
    }

    [Fact]
    public void TheClockIsCheckedAfterTheScore() {
        // ⚠ On this map the winning score lands on exactly the tick the clock expires. The work was
        // done, so it is a win on score rather than a draw.
        var match = new PvpMatch(library.Find(DefId.From(Content.Photo))!);

        match.Join(Content.Player(1), 0);
        match.Join(Content.Player(2), 1);
        match.Occupy(0, [1, 0]);

        Assert.True(match.Tick(10f));
        Assert.Equal(10f, match.Elapsed);
        Assert.Equal(MatchOutcome.Score, match.Outcome);
        Assert.Equal(0, match.Winner);
    }

    [Fact]
    public void RunningOutOfTimeLevelIsADraw() {
        var match = new PvpMatch(library.Find(DefId.From(Content.Photo))!);

        match.Join(Content.Player(1), 0);
        match.Join(Content.Player(2), 1);

        Assert.True(match.Tick(10.1f));
        Assert.Equal(MatchOutcome.Draw, match.Outcome);
        Assert.Equal(-1, match.Winner);
    }

    [Fact]
    public void AContestedPointGoesOnScoringForWhoeverStillHoldsIt() {
        // Contesting freezes the *capture*, not the scoring. You keep the points until the flag
        // actually flips, which is what makes defending worth anything.
        var match = Basin();

        Stand(match, 1, 1, 1);
        match.Tick(5f);

        Assert.Equal(0, match.Objectives[1].Owner);
        Assert.Equal(5, match.ScoreOf(0));
    }

    [Fact]
    public void ATeamEmptyingForfeitsTheMatch() {
        var match = Basin();

        Assert.Equal(PvpRefusal.None, match.Leave(Content.Player(2)));
        Assert.Equal(MatchOutcome.Forfeit, match.Outcome);
        Assert.Equal(0, match.Winner);
    }

    [Fact]
    public void AMatchNobodyHasJoinedDoesNotForfeitItself() {
        // ⚠ A match that forfeited on an empty side would end the moment it was created.
        var match = new PvpMatch(library.Find(DefId.From(Content.Basin))!);

        Assert.False(match.IsOver);
        Assert.Equal(PvpRefusal.NotAPlayer, match.Leave(Content.Player(1)));
        Assert.False(match.IsOver);
    }

    [Fact]
    public void SomebodyLeavingATeamThatStillHasPeopleChangesNothing() {
        var match = Basin();

        match.Join(Content.Player(3), 1);

        Assert.Equal(PvpRefusal.None, match.Leave(Content.Player(2)));
        Assert.False(match.IsOver);
    }

    [Fact]
    public void AnArenaIsBestOfItsRounds() {
        var match = new PvpMatch(library.Find(DefId.From(Content.Ring))!);

        match.Join(Content.Player(1), 0);
        match.Join(Content.Player(2), 1);
        match.Occupy(0, [1, 0]);

        // Ten points at five a second: two seconds a round, plus the second to capture.
        Assert.False(match.Tick(4f));
        Assert.Equal(1, match.RoundsWonBy(0));
        Assert.Equal(2, match.Round);
        Assert.Equal(0, match.ScoreOf(0));

        match.Occupy(0, [1, 0]);

        Assert.True(match.Tick(4f));
        Assert.Equal(2, match.RoundsWonBy(0));
        Assert.Equal(MatchOutcome.Score, match.Outcome);
        Assert.Equal(0, match.Winner);
    }

    [Fact]
    public void ARoundResetsTheObjectivesAndTheClock() {
        var match = new PvpMatch(library.Find(DefId.From(Content.Ring))!);

        match.Join(Content.Player(1), 0);
        match.Join(Content.Player(2), 1);
        match.Occupy(0, [1, 0]);
        match.Tick(4f);

        Assert.Equal(-1, match.Objectives[0].Owner);
        Assert.Equal(0f, match.Elapsed);
        Assert.Equal(0, match.ScoreOf(0));
    }

    [Fact]
    public void AFinishedMatchStopsTicking() {
        var match = Basin();

        match.Leave(Content.Player(2));

        Assert.True(match.IsOver);
        Assert.False(match.Tick(1000f));
    }

    [Fact]
    public void AnUnknownTeamOrObjectiveIsRefused() {
        var match = Basin();

        Assert.Equal(PvpRefusal.Unknown, match.Join(Content.Player(3), 9));
        Assert.Equal(PvpRefusal.Unknown, match.Occupy(9, [1, 0]));
        Assert.Equal(-1, match.TeamOf(Content.Player(9)));
    }

    [Fact]
    public void ScoreOnlyEverGoesUpAndAPointIsOwnedByAtMostOneTeam() {
        // The property that matters for a scoring system: however the objectives are fought over,
        // nobody's score falls and no objective is ever held by two teams at once.
        var random = new GameplayRandom(0xB4551ul);
        var captures = 0;

        for (var run = 0; run < 60; run++) {
            var match = Basin();
            var previous = new[] { 0, 0 };

            match.Captured += (_, _) => captures++;

            for (var step = 0; step < 80 && !match.IsOver; step++) {
                for (var objective = 0; objective < match.Objectives.Length; objective++) {
                    match.Occupy(objective, [random.NextInt(3), random.NextInt(3)]);
                }

                match.Tick(random.NextFloat() * 3f);

                for (var team = 0; team < 2; team++) {
                    Assert.True(match.ScoreOf(team) >= previous[team], "a score went down");
                    previous[team] = match.ScoreOf(team);
                }

                foreach (var state in match.Objectives) {
                    Assert.InRange(state.Owner, -1, 1);
                    Assert.InRange(state.Progress, 0f, 1f);
                }
            }
        }

        Assert.True(captures > 100, $"only {captures} captures happened");
    }
}

public class PvpLibraryTests {
    static IReadOnlyList<string> Problems(PvpMapDefinition definition) =>
        PvpLibrary.Compile(new DefinitionCatalogBuilder().Add("pvp/odd", definition).Build()).Problems;

    [Fact]
    public void AMapNothingCanEndIsAProblem() =>
        Assert.Contains(
            Problems(new() { ScoreToWin = 0, TimeLimit = 0f }),
            problem => problem.Contains("nothing can end it", StringComparison.Ordinal)
        );

    [Fact]
    public void AMapWonOnScoreWithNoObjectivesIsAProblem() =>
        Assert.Contains(
            Problems(new() { ScoreToWin = 100 }),
            problem => problem.Contains("nobody can score", StringComparison.Ordinal)
        );

    [Fact]
    public void AnObjectiveThatScoresNothingIsAProblem() =>
        Assert.Contains(
            Problems(new() { Objectives = [new() { Id = "a", PointsPerTick = 0, PointsOnCapture = 0 }] }),
            problem => problem.Contains("holding it does nothing", StringComparison.Ordinal)
        );

    [Fact]
    public void AnObjectiveOwnedByATeamThatDoesNotExistIsAProblem() =>
        Assert.Contains(
            Problems(new() { Teams = 2, Objectives = [new() { Id = "a", StartingOwner = 5 }] }),
            problem => problem.Contains("only 2", StringComparison.Ordinal)
        );

    [Fact]
    public void TwoObjectivesWithOneIdIsAProblem() =>
        Assert.Contains(
            Problems(new() { Objectives = [new() { Id = "a" }, new() { Id = "a" }] }),
            problem => problem.Contains("two objectives called 'a'", StringComparison.Ordinal)
        );
}
