// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Quests;
using Xunit;

namespace Vixen.Editor.Gameplay.Quests.Tests;

/// <summary>A camp that falls, is retaken and falls again — the cyclic chain, and a quest to edit.</summary>
static class Content {
    public const string Defence = "events/camp-defence";
    public const string Retake = "events/camp-retake";
    public const string Aftermath = "events/aftermath";

    public static QuestLibrary Library() =>
        QuestLibrary.Compile(
            new DefinitionCatalogBuilder()
                .AddTag(QuestVerbs.Kill)
                .AddTag(QuestVerbs.Interact)
                .Add(
                    Defence,
                    new DynamicEventDefinition {
                        DisplayName = "Defend the camp",
                        Duration = 60f,
                        Objectives = [new() { Type = "Kill", DisplayName = "Bandits", Count = 10 }],
                        OnSuccess = [Aftermath],
                        OnFailure = [Retake]
                    }
                )
                .Add(
                    Retake,
                    new DynamicEventDefinition {
                        DisplayName = "Retake the camp",
                        Duration = 90f,
                        Objectives = [new() { Type = "Interact", Count = 1 }],

                        // The edge that closes the loop, and the one no acyclic model can hold.
                        OnSuccess = [Defence],
                        OnFailure = ["events/nowhere"]
                    }
                )
                .Add(
                    Aftermath,
                    new DynamicEventDefinition {
                        DisplayName = "Bury the dead",
                        Duration = 30f,
                        Objectives = [new() { Type = "Interact", Count = 3 }]
                    }
                )
                .Build()
        );

    public static QuestDefinition Quest() =>
        new() {
            DisplayName = "A Prologue",
            Stages = [
                new() {
                    Id = "hunt",
                    Objectives = [new() { Type = "Kill", DisplayName = "Skeletons", Count = 3 }]
                }
            ]
        };
}

public class QuestModelTests {
    [Fact]
    public void EveryGestureIsOneOperationAndOneChange() {
        var model = new QuestModel(Content.Quest());
        var changes = 0;

        model.Changed += _ => changes++;

        Assert.Equal(1, model.AddStage());
        Assert.Equal(0, model.AddObjective(1, new() { Type = "Collect", Count = 4 }));
        Assert.True(model.Edit(1, 0, objective => objective.Count = 9));
        Assert.True(model.MoveStage(1, 0));
        Assert.True(model.RemoveObjective(0, 0));
        Assert.True(model.RemoveStage(0));

        Assert.Equal(6, changes);
        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void AnOperationOnSomethingThatIsNotThereChangesNothing() {
        var model = new QuestModel(Content.Quest());
        var changes = 0;

        model.Changed += _ => changes++;

        Assert.False(model.RemoveStage(9));
        Assert.Equal(-1, model.AddObjective(9));
        Assert.False(model.RemoveObjective(0, 9));
        Assert.False(model.Edit(9, 0, _ => { }));
        Assert.False(model.MoveStage(0, 0));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ASnapshotIsDeepEnoughToUndoAnObjectiveEdit() {
        var quest = Content.Quest();
        var model = new QuestModel(quest);
        var snapshot = model.Snapshot();

        model.Edit(0, 0, objective => objective.Count = 99);
        model.AddStage();

        Assert.Equal(99, model.Quest.Stages[0].Objectives[0].Count);

        model.Restore(snapshot);

        Assert.Equal(3, model.Quest.Stages[0].Objectives[0].Count);
        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void AQuestWithNoStagesIsAProblem() {
        var model = new QuestModel(new QuestDefinition());

        Assert.Contains(model.Validate(), problem => problem.Stage == -1 && problem.Message.Contains("no stages", StringComparison.Ordinal));
    }

    [Fact]
    public void AnObjectiveTypeTheBuildDoesNotHaveIsAProblemAgainstItsOwnRow() {
        var quest = Content.Quest();

        quest.Stages[0].Objectives[0].Type = "Yodel";

        var problems = new QuestModel(quest).Validate();

        Assert.Contains(problems, problem => problem is { Stage: 0, Objective: 0 } && problem.Message.Contains("Yodel", StringComparison.Ordinal));
    }

    [Fact]
    public void AStageOfOnlyOptionalObjectivesIsAProblem() {
        var quest = Content.Quest();

        quest.Stages[0].Objectives[0].Optional = true;

        Assert.Contains(new QuestModel(quest).Validate(), problem => problem.Message.Contains("optional", StringComparison.Ordinal));
    }

    [Fact]
    public void ARewardChoiceOfOneIsAProblem() {
        var quest = Content.Quest();

        quest.Rewards.Choices = [new() { Def = "items/sword" }];

        Assert.Contains(new QuestModel(quest).Validate(), problem => problem.Message.Contains("not a choice", StringComparison.Ordinal));
    }

    [Fact]
    public void AGamesOwnObjectiveTypeValidatesOnceItIsRegistered() {
        var quest = Content.Quest();

        quest.Stages[0].Objectives[0].Type = "Yodel";

        var registry = new QuestObjectiveRegistry().AddShipped().Add(new YodelObjective());

        Assert.Empty(new QuestModel(quest).Validate(registry));
    }

    sealed class YodelObjective : IQuestObjective {
        public string Type => "Yodel";

        public string Verb => "Event.Yodel";
    }
}

public class EventChainTests {
    [Fact]
    public void ALoopingChainHasNoRootAndTheHubIsItsEntry() {
        // ⚠ The thing writing the tests found: in a chain that loops, *every* event has something
        // pointing at it, so there is no root — and a walk that started only from roots would draw an
        // empty canvas for perfectly good content. The event leading to the most places is the entry.
        var chain = EventChain.Build(Content.Library());

        Assert.Empty(chain.Roots);
        Assert.Single(chain.Entries);
        Assert.Equal(DefId.From(Content.Defence), chain.Entries[0]);
        Assert.Equal(3, chain.Order.Count);
    }

    [Fact]
    public void AChainThatDoesNotLoopBackDoesHaveARoot() {
        var library = QuestLibrary.Compile(
            new DefinitionCatalogBuilder()
                .AddTag(QuestVerbs.Interact)
                .Add(
                    "events/first",
                    new DynamicEventDefinition {
                        Duration = 10f,
                        Objectives = [new() { Type = "Interact", Count = 1 }],
                        OnSuccess = ["events/second"]
                    }
                )
                .Add(
                    "events/second",
                    new DynamicEventDefinition {
                        Duration = 10f,
                        Objectives = [new() { Type = "Interact", Count = 1 }]
                    }
                )
                .Build()
        );

        var chain = EventChain.Build(library);

        Assert.Single(chain.Roots);
        Assert.Equal(DefId.From("events/first"), chain.Roots[0]);
        Assert.Equal(chain.Roots, chain.Entries);
        Assert.False(chain.IsCyclic);
    }

    [Fact]
    public void TheEdgeThatClosesTheLoopIsNamedRatherThanDropped() {
        // ⚠ The finding that decided this library: retake → defence is a cycle, and both graph models
        // in the engine refuse a cycle as the edge is made. So it is walked out of the spanning tree
        // and reported, not lost.
        var chain = EventChain.Build(Content.Library());

        Assert.True(chain.IsCyclic);
        Assert.Contains(
            chain.BackEdges,
            edge => edge.From == DefId.From(Content.Retake) && edge.To == DefId.From(Content.Defence)
        );
        Assert.DoesNotContain(chain.TreeEdges, edge => edge.To == DefId.From(Content.Defence));
    }

    [Fact]
    public void EveryReachedEventHasExactlyOneIncomingTreeEdgeOrIsAnEntry() {
        // The rule that makes the picture drawable at all: the canvas gives an input one wire.
        var chain = EventChain.Build(Content.Library());

        foreach (var id in chain.Order) {
            var arriving = chain.TreeEdges.Count(edge => edge.To == id);

            Assert.True(arriving <= 1, $"{id} has {arriving} incoming tree edges.");
            Assert.True(arriving == 1 || chain.Entries.Contains(id));
        }
    }

    [Fact]
    public void AnEdgeToAnEventThisBuildDoesNotHaveIsDangling() {
        var chain = EventChain.Build(Content.Library());

        Assert.Single(chain.Dangling);
        Assert.Equal("events/nowhere", chain.Dangling[0].Address);
    }

    [Fact]
    public void AChainWithNoRootStillHasAnEntry() {
        // Two events that only lead to each other. Walking from roots alone would draw nothing.
        var library = QuestLibrary.Compile(
            new DefinitionCatalogBuilder()
                .AddTag(QuestVerbs.Interact)
                .Add(
                    "events/tick",
                    new DynamicEventDefinition {
                        Duration = 10f,
                        Objectives = [new() { Type = "Interact", Count = 1 }],
                        OnSuccess = ["events/tock"]
                    }
                )
                .Add(
                    "events/tock",
                    new DynamicEventDefinition {
                        Duration = 10f,
                        Objectives = [new() { Type = "Interact", Count = 1 }],
                        OnSuccess = ["events/tick"]
                    }
                )
                .Build()
        );

        var chain = EventChain.Build(library);

        Assert.Empty(chain.Roots);
        Assert.Single(chain.Entries);
        Assert.Equal(DefId.From("events/tick"), chain.Entries[0]);
        Assert.Equal(2, chain.Order.Count);
    }

    [Fact]
    public void TheWalkIsStableAcrossBuilds() {
        var first = EventChain.Build(Content.Library());
        var second = EventChain.Build(Content.Library());

        Assert.Equal(first.Order, second.Order);
        Assert.Equal(first.TreeEdges, second.TreeEdges);
        Assert.Equal(first.BackEdges, second.BackEdges);
    }
}

public class EventChainProjectionTests {
    [Fact]
    public void EveryEventGetsABoxWithBothBranchPorts() {
        var projection = new EventChainProjection();
        var graph = projection.Project(EventChain.Build(Content.Library()));

        Assert.Equal(3, graph.Nodes.Count);

        foreach (var box in graph.Nodes) {
            Assert.Single(box.Inputs);
            Assert.Equal(2, box.Outputs.Count);
            Assert.Equal("success", box.Outputs[0].Name);
            Assert.Equal("failure", box.Outputs[1].Name);
            Assert.NotNull(EventChainProjection.EventOf(box));
        }
    }

    [Fact]
    public void OnlyTheTreeEdgesAreWiredAndTheRestAreBadges() {
        var projection = new EventChainProjection();
        var chain = EventChain.Build(Content.Library());
        var graph = projection.Project(chain);

        Assert.Equal(chain.TreeEdges.Count, graph.Wires.Count);
        Assert.Equal(chain.BackEdges.Count + chain.Dangling.Count, projection.Deferred);

        var retake = projection.BoxOf(DefId.From(Content.Retake))!;

        Assert.Contains(retake.Attachments, attachment => attachment.Kind == "loop");
        Assert.Contains(retake.Attachments, attachment => attachment.Kind == "missing");
    }

    [Fact]
    public void AFailureWireLeavesTheFailurePort() {
        var projection = new EventChainProjection();

        projection.Project(EventChain.Build(Content.Library()));

        var defence = projection.BoxOf(DefId.From(Content.Defence))!;
        var retake = projection.BoxOf(DefId.From(Content.Retake))!;
        var wire = projection.Graph.Wires.Single(entry => entry.To.Node == retake);

        Assert.Equal(defence.Outputs[1], wire.From);
    }

    [Fact]
    public void TheEntrySitsInTheFirstColumnAndItsSuccessorsAfterIt() {
        var projection = new EventChainProjection();

        projection.Project(EventChain.Build(Content.Library()));

        Assert.Equal(0f, projection.BoxOf(DefId.From(Content.Defence))!.Position.X);
        Assert.Equal(projection.ColumnWidth, projection.BoxOf(DefId.From(Content.Retake))!.Position.X);
        Assert.Equal(projection.ColumnWidth, projection.BoxOf(DefId.From(Content.Aftermath))!.Position.X);
    }

    [Fact]
    public void TheEntryIsAccentedAndTheOthersAreNot() {
        var projection = new EventChainProjection();

        projection.Project(EventChain.Build(Content.Library()));

        Assert.Equal("root", projection.BoxOf(DefId.From(Content.Defence))!.Accent);
        Assert.Equal(string.Empty, projection.BoxOf(DefId.From(Content.Retake))!.Accent);
    }

    [Fact]
    public void ObjectivesAndTheClockAreDrawnOnTheBox() {
        var projection = new EventChainProjection();

        projection.Project(EventChain.Build(Content.Library()));

        var defence = projection.BoxOf(DefId.From(Content.Defence))!;

        Assert.Contains(defence.Attachments, attachment => attachment is { Kind: "objective", Text: "Bandits", Detail: "×10" });
        Assert.Contains(defence.Attachments, attachment => attachment is { Kind: "timer", Detail: "60 s" });
    }

    [Fact]
    public void TheLiveOverlayTintsWhatIsRunningAndBadgesItsCrowd() {
        var library = Content.Library();
        var bus = new GameplayEventBus();
        var projection = new EventChainProjection();

        projection.Project(EventChain.Build(library));

        using var director = new DynamicEventDirector(library, bus);
        var instance = director.Begin(DefId.From(Content.Defence))!;

        instance.Contribute(1, 5);
        instance.Contribute(2, 5);

        Assert.Equal(1, projection.Live(director));

        var defence = projection.BoxOf(DefId.From(Content.Defence))!;

        Assert.Equal("active", defence.Accent);
        Assert.Equal("2", defence.Badge);

        Assert.Equal(0, projection.Live(null));
        Assert.Equal(string.Empty, defence.Accent);
    }
}
