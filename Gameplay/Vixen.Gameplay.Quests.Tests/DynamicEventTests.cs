// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Quests.Tests;

/// <summary>Dynamic events: scaling, contribution, and the chains that cycle.</summary>
public class DynamicEventTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly GameplayEventBus bus = new();
    readonly QuestLibrary library;

    public DynamicEventTests() => library = QuestLibrary.Compile(catalog);

    DynamicEventTemplate Defence => library.FindEvent(DefId.From(Content.CampDefence))!;

    void KillBandit(ulong who, int times = 1) {
        var bandit = new GameplayTagSet();
        bandit.Add(catalog.Tags.Resolve("Creature.Bandit"));

        for (var index = 0; index < times; index++) {
            bus.Post(new(Content.Verb(QuestVerbs.Kill), default, DefId.From(Content.Queensdale), 1, who, bandit));
        }
    }

    [Fact]
    public void ScalingIsMonotoneInParticipants() {
        // doc 28 § Testing names this one, and the reason it matters is that an event which got
        // *easier* when a tenth player arrived would be a mechanic for griefing it.
        var template = Defence;
        var previous = 0f;

        for (var participants = 0; participants <= 200; participants++) {
            var scale = template.Scale(participants);

            Assert.True(scale >= previous, $"{participants} scaled to {scale}, below {previous}.");
            Assert.InRange(scale, 1f, template.Definition.Scaling.Maximum);

            previous = scale;
        }
    }

    [Fact]
    public void ScalingIsFlatBelowTheBaselineAndCappedAbove() {
        var template = Defence;

        Assert.Equal(1f, template.Scale(0));
        Assert.Equal(1f, template.Scale(5));
        Assert.Equal(1.2f, template.Scale(6), 5);
        Assert.Equal(3f, template.Scale(1000));
    }

    [Fact]
    public void ContributionTiersAreSearchedRichestFirst() {
        // The test content authors them Bronze, Gold, Silver on purpose.
        var template = Defence;

        Assert.Null(template.TierFor(0));
        Assert.Equal("Bronze", template.TierFor(1)!.DisplayName);
        Assert.Equal("Silver", template.TierFor(20)!.DisplayName);
        Assert.Equal("Gold", template.TierFor(50)!.DisplayName);
        Assert.Equal("Gold", template.TierFor(5000)!.DisplayName);
        Assert.Equal(900, template.TierFor(50)!.Reward.Experience);
    }

    [Fact]
    public void ContributionIsRecordedFromWhoeverAdvancedAnObjective() {
        using var director = new DynamicEventDirector(library, bus);
        var instance = director.Begin(DefId.From(Content.CampDefence))!;

        KillBandit(7, 3);
        KillBandit(9);

        Assert.Equal(3, instance.ContributionOf(7));
        Assert.Equal(1, instance.ContributionOf(9));
        Assert.Equal(2, instance.Participants);
        Assert.Equal(4, instance.Objectives.ProgressOf(0));
    }

    [Fact]
    public void EverybodysKillsCountTowardsOneNumber() {
        using var director = new DynamicEventDirector(library, bus);
        var instance = director.Begin(DefId.From(Content.CampDefence))!;

        for (ulong who = 1; who <= 10; who++) {
            KillBandit(who);
        }

        Assert.Equal(10, instance.Objectives.ProgressOf(0));
        Assert.True(instance.Tick(0.1f));
        Assert.Equal(DynamicEventStatus.Succeeded, instance.Status);
    }

    [Fact]
    public void ContributionCreditsWorkNoObjectiveCounts() {
        using var director = new DynamicEventDirector(library, bus);
        var instance = director.Begin(DefId.From(Content.CampDefence))!;

        Assert.Equal(40, instance.Contribute(3, 40));
        Assert.Equal(40, instance.Contribute(3, -100));
        Assert.Equal("Silver", instance.TierOf(3)!.DisplayName);
    }

    [Fact]
    public void RescalingRaisesAndNeverLowers() {
        var objectives = Defence.Objectives;
        using var tracker = new ObjectiveTracker(bus, objectives);

        Assert.Equal(10, tracker.RequiredOf(0));
        Assert.Equal(1, tracker.Rescale(2f));
        Assert.Equal(20, tracker.RequiredOf(0));
        Assert.Equal(0, tracker.Rescale(1f));
        Assert.Equal(20, tracker.RequiredOf(0));
    }

    [Fact]
    public void AFailedEventStartsItsFailureBranch() {
        using var director = new DynamicEventDirector(library, bus);
        var steps = new List<EventChainStep>();

        director.Stepped += step => steps.Add(step);
        director.Begin(DefId.From(Content.CampDefence));
        director.Tick(61f);

        Assert.Single(steps);
        Assert.Equal(DynamicEventStatus.Failed, steps[0].Status);
        Assert.Equal(DefId.From(Content.CampRetake), steps[0].Started[0]);
        Assert.True(director.IsRunning(DefId.From(Content.CampRetake)));
        Assert.False(director.IsRunning(DefId.From(Content.CampDefence)));
    }

    [Fact]
    public void AChainMayCycleAndTheDirectorDoesNotMind() {
        using var director = new DynamicEventDirector(library, bus);

        director.Begin(DefId.From(Content.CampDefence));
        director.Tick(61f);

        // Retake succeeds, which starts the defence again — the loop doc 28 says makes a chain feel
        // alive, and the reason no acyclicity check could be right here.
        bus.Post(new(Content.Verb(QuestVerbs.Interact), DefId.From(Content.Lever), Instigator: 4));
        director.Tick(0.1f);

        Assert.True(director.IsRunning(DefId.From(Content.CampDefence)));
        Assert.False(director.IsRunning(DefId.From(Content.CampRetake)));
    }

    [Fact]
    public void AnEventAlreadyRunningIsNotStartedTwice() {
        using var director = new DynamicEventDirector(library, bus);

        Assert.NotNull(director.Begin(DefId.From(Content.CampDefence)));
        Assert.Null(director.Begin(DefId.From(Content.CampDefence)));
        Assert.Equal(1, director.Count);
    }

    [Fact]
    public void AnEventThisBuildDoesNotHaveDoesNotStart() {
        using var director = new DynamicEventDirector(library, bus);

        Assert.Null(director.Begin(DefId.From("events/nowhere")));
        Assert.Equal(0, director.Count);
    }

    [Fact]
    public void TheClockIsCheckedAfterTheObjectives() {
        // Finishing on the very tick the duration runs out succeeded: the work was done, and failing
        // it would read as the server cheating.
        using var director = new DynamicEventDirector(library, bus);
        var instance = director.Begin(DefId.From(Content.CampDefence))!;

        instance.Tick(59.9f);
        KillBandit(1, 10);

        Assert.True(instance.Tick(1f));
        Assert.Equal(DynamicEventStatus.Succeeded, instance.Status);
    }

    [Fact]
    public void AnEventEndedByHandResolvesItsChain() {
        using var director = new DynamicEventDirector(library, bus);

        director.Begin(DefId.From(Content.CampDefence));

        Assert.True(director.Finish(DefId.From(Content.CampDefence), DynamicEventStatus.Failed));
        Assert.True(director.IsRunning(DefId.From(Content.CampRetake)));
        Assert.False(director.Finish(DefId.From(Content.CampDefence), DynamicEventStatus.Failed));
    }

    [Fact]
    public void AnEventWithADurationAlwaysReachesATerminalState() {
        // doc 28 § Testing: "event chains reach a terminal state". Only true of an event that can end
        // on its own, which is what IsSelfTerminating says — so the property is asserted over exactly
        // those, and the library is what identifies them.
        var random = new GameplayRandom(0x5EEDul);

        foreach (var template in library.Events) {
            Assert.True(template.IsSelfTerminating);

            for (var run = 0; run < 50; run++) {
                using var instance = new DynamicEventInstance(template, bus);
                var steps = 0;

                while (!instance.IsTerminal && steps++ < 10_000) {
                    instance.Tick(random.NextFloat() * 5f);
                }

                Assert.True(instance.IsTerminal, $"'{template.Definition.Address}' never ended.");
                Assert.Equal(DynamicEventStatus.Failed, instance.Status);
            }
        }
    }

    [Fact]
    public void AnObjectiveTypeThisBuildDoesNotHaveIsAProblem() {
        var problems = QuestLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .AddTag(QuestVerbs.Kill)
                    .Add(
                        "quests/broken",
                        new QuestDefinition {
                            Stages = [new() { Objectives = [new() { Type = "Yodel", Count = 1 }] }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("Yodel", StringComparison.Ordinal));
    }

    [Fact]
    public void AVerbThatIsNotATagInThisBuildIsAProblem() {
        // Nothing declares the verbs here, so the shipped Kill type resolves to an empty range — the
        // silent-forever failure the report exists to make loud.
        var problems = QuestLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(
                        "quests/silent",
                        new QuestDefinition {
                            Stages = [new() { Objectives = [new() { Type = "Kill", Count = 1 }] }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains(QuestVerbs.Kill, StringComparison.Ordinal));
    }

    [Fact]
    public void AStageOfOnlyOptionalObjectivesIsAProblem() {
        var problems = QuestLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .AddTag(QuestVerbs.Kill)
                    .Add(
                        "quests/free",
                        new QuestDefinition {
                            Stages = [
                                new() { Objectives = [new() { Type = "Kill", Count = 1, Optional = true }] }
                            ]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("optional", StringComparison.Ordinal));
    }

    [Fact]
    public void AChainEdgeToAnEventThisBuildDoesNotHaveIsAProblem() {
        var problems = QuestLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .AddTag(QuestVerbs.Kill)
                    .Add(
                        "events/orphan",
                        new DynamicEventDefinition {
                            Duration = 10f,
                            Objectives = [new() { Type = "Kill", Count = 1 }],
                            OnSuccess = ["events/nowhere"]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("events/nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreachableFailureBranchIsAProblem() {
        var problems = QuestLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .AddTag(QuestVerbs.Kill)
                    .Add(
                        "events/eternal",
                        new DynamicEventDefinition {
                            Objectives = [new() { Type = "Kill", Count = 1 }],
                            OnFailure = ["events/eternal"]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("unreachable", StringComparison.Ordinal));
    }
}
