// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Quests.Tests;

/// <summary>The quest state machine, and doc 28's property that no objective completes twice.</summary>
public class QuestJournalTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly GameplayEventBus bus = new();
    readonly QuestLibrary library;
    readonly GameplaySubject subject = new(AttributeLayout.Empty);

    public QuestJournalTests() => library = QuestLibrary.Compile(catalog);

    QuestJournal Journal(ulong owner = 1) => new(library, bus, owner, subject, subject.Tags);

    void Kill(int times, ulong who = 1, string scene = Content.Queensdale) {
        var undead = Content.Undead(catalog.Tags);

        for (var index = 0; index < times; index++) {
            bus.Post(Content.Kill(undead, who, scene));
        }
    }

    void Collect(int amount, ulong who = 1) =>
        bus.Post(new(Content.Verb(QuestVerbs.Collect), DefId.From(Content.Ore), default, amount, who));

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void AcceptingGrantsTheQuestsActiveTags() {
        var journal = Journal();

        Assert.Equal(QuestRefusal.None, journal.Accept(DefId.From(Content.Prologue)));
        Assert.True(subject.Tags.Contains(catalog.Tags.Resolve(Content.OnPrologue)));
        Assert.Equal(QuestStatus.Active, journal.StatusOf(DefId.From(Content.Prologue)));
    }

    [Fact]
    public void AQuestAlreadyOnTheJournalIsRefused() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));

        Assert.Equal(QuestRefusal.AlreadyActive, journal.Accept(DefId.From(Content.Prologue)));
    }

    [Fact]
    public void AQuestChainIsARequirementAndNothingElse() {
        var journal = Journal();

        Assert.Equal(QuestRefusal.Requirements, journal.Accept(DefId.From(Content.Chain)));

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);
        Collect(2);
        Assert.Equal(QuestRefusal.None, journal.TurnIn(DefId.From(Content.Prologue), 0, out _));

        Assert.Equal(QuestRefusal.None, journal.Accept(DefId.From(Content.Chain)));
    }

    [Fact]
    public void KillsInTheWrongSceneDoNotCount() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(5, scene: Content.Elsewhere);

        Assert.Equal(0, journal.Find(DefId.From(Content.Prologue))!.Tracker!.ProgressOf(0));
    }

    [Fact]
    public void SomebodyElsesKillsDoNotCount() {
        var journal = Journal(owner: 7);

        journal.Accept(DefId.From(Content.Prologue));
        Kill(5, who: 8);

        Assert.Equal(0, journal.Find(DefId.From(Content.Prologue))!.Tracker!.ProgressOf(0));

        Kill(3, who: 7);

        Assert.Equal(1, journal.Find(DefId.From(Content.Prologue))!.Stage);
    }

    [Fact]
    public void FinishingAStageStartsTheNext() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);

        var quest = journal.Find(DefId.From(Content.Prologue))!;

        Assert.Equal(1, quest.Stage);
        Assert.Equal("gather", quest.CurrentStage!.Id);
    }

    [Fact]
    public void AStageCompletesWithoutItsOptionalObjectives() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);
        Collect(2);

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.StatusOf(DefId.From(Content.Prologue)));
    }

    [Fact]
    public void NoObjectiveCompletesTwiceHoweverManyEventsArrive() {
        // ⚠ doc 28 § Testing names this one. Three kills finish the objective; ninety-seven more must
        // do nothing at all, and the completion must be reported exactly once.
        var journal = Journal();
        var completions = 0;

        journal.Advanced += (_, advance) => {
            if (advance.Completed) {
                completions++;
            }
        };

        journal.Accept(DefId.From(Content.Prologue));
        Kill(100);

        Assert.Equal(1, completions);
        Assert.Equal(1, journal.Find(DefId.From(Content.Prologue))!.Stage);
    }

    [Fact]
    public void TheNextStageDoesNotCountTheEventThatEndedTheLast() {
        // Both stages of this quest count kills, which is the only shape that can catch it: the bus
        // holds a subscription made mid-dispatch until the dispatch ends, so the kill that finished
        // stage one is not also the kill that starts stage two.
        var journal = Journal();

        journal.Accept(DefId.From(Content.Culling));
        Kill(1);

        var quest = journal.Find(DefId.From(Content.Culling))!;

        Assert.Equal(1, quest.Stage);
        Assert.Equal(0, quest.Tracker!.ProgressOf(0));

        Kill(1);

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.StatusOf(DefId.From(Content.Culling)));
    }

    [Fact]
    public void ACollectObjectiveGoesBackDownWhenTheItemsAreSold() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);
        Collect(1);

        var tracker = journal.Find(DefId.From(Content.Prologue))!.Tracker!;

        Assert.Equal(1, tracker.ProgressOf(0));

        Collect(-1);

        Assert.Equal(0, tracker.ProgressOf(0));
    }

    [Fact]
    public void ATallyNeverGoesBackDown() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(2);

        var tracker = journal.Find(DefId.From(Content.Prologue))!.Tracker!;

        Assert.Equal(2, tracker.ProgressOf(0));

        bus.Post(
            new(Content.Verb(QuestVerbs.Kill), DefId.From(Content.Skeleton), DefId.From(Content.Queensdale), -2, 1, Content.Undead(catalog.Tags))
        );

        Assert.Equal(2, tracker.ProgressOf(0));
    }

    [Fact]
    public void ALatchedObjectiveStaysCompleteWhenTheLevelFalls() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);
        Collect(2);

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.StatusOf(DefId.From(Content.Prologue)));

        Collect(-2);

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.StatusOf(DefId.From(Content.Prologue)));
    }

    [Fact]
    public void TurningInReportsTheRewardAndGrantsTheCompletionTag() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);
        Collect(2);

        Assert.Equal(QuestRefusal.None, journal.TurnIn(DefId.From(Content.Prologue), 1, out var reward));
        Assert.NotNull(reward);
        Assert.Equal(500, reward.Experience);
        Assert.Equal(Content.Sword, reward.Items[0].Address);
        Assert.Equal(Content.Wand, reward.Choices[1].Address);
        Assert.True(subject.Tags.Contains(catalog.Tags.Resolve(Content.Completed)));
        Assert.False(subject.Tags.Contains(catalog.Tags.Resolve(Content.OnPrologue)));
    }

    [Fact]
    public void ARewardWithChoicesRefusesATurnInThatMakesNone() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);
        Collect(2);

        Assert.Equal(QuestRefusal.BadChoice, journal.TurnIn(DefId.From(Content.Prologue), -1, out _));
        Assert.Equal(QuestRefusal.BadChoice, journal.TurnIn(DefId.From(Content.Prologue), 9, out _));
    }

    [Fact]
    public void AQuestWithNoChoicesRefusesOne() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Vigil));
        journal.Tick(6f);

        Assert.Equal(QuestRefusal.BadChoice, journal.TurnIn(DefId.From(Content.Vigil), 0, out _));
        Assert.Equal(QuestRefusal.None, journal.TurnIn(DefId.From(Content.Vigil), -1, out _));
    }

    [Fact]
    public void AQuestThatDoesNotRepeatCannotBeTakenTwice() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));
        Kill(3);
        Collect(2);
        journal.TurnIn(DefId.From(Content.Prologue), 0, out _);

        Assert.Equal(QuestRefusal.AlreadyDone, journal.Accept(DefId.From(Content.Prologue)));
    }

    [Fact]
    public void AbandoningDropsTheActiveTagsAndStopsCounting() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Prologue));

        var before = bus.Count;

        Assert.True(journal.Abandon(DefId.From(Content.Prologue)));
        Assert.False(subject.Tags.Contains(catalog.Tags.Resolve(Content.OnPrologue)));
        Assert.Equal(QuestStatus.Abandoned, journal.StatusOf(DefId.From(Content.Prologue)));
        Assert.True(bus.Count < before);
    }

    [Fact]
    public void ASurviveObjectiveCompletesOnTheClockAlone() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Vigil));
        journal.Tick(2f);

        Assert.Equal(QuestStatus.Active, journal.StatusOf(DefId.From(Content.Vigil)));

        journal.Tick(4f);

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.StatusOf(DefId.From(Content.Vigil)));
    }

    [Fact]
    public void AnEscorteeDyingFailsTheQuest() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Escort));
        bus.Post(new(Content.Verb(QuestVerbs.EscortFailed), DefId.From(Content.Villager), Instigator: 1));

        Assert.Equal(QuestStatus.Failed, journal.StatusOf(DefId.From(Content.Escort)));
    }

    [Fact]
    public void AStageClockRunningOutFailsTheQuest() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Escort));
        journal.Tick(29f);

        Assert.Equal(QuestStatus.Active, journal.StatusOf(DefId.From(Content.Escort)));

        journal.Tick(2f);

        Assert.Equal(QuestStatus.Failed, journal.StatusOf(DefId.From(Content.Escort)));
    }

    [Fact]
    public void AnEscortThatArrivesInTimeIsNotFailedByTheClockAfterwards() {
        var journal = Journal();

        journal.Accept(DefId.From(Content.Escort));
        bus.Post(new(Content.Verb(QuestVerbs.Escort), DefId.From(Content.Villager), Instigator: 1));

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.StatusOf(DefId.From(Content.Escort)));

        journal.Tick(100f);

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.StatusOf(DefId.From(Content.Escort)));
    }

    [Fact]
    public void EveryQuestEndsInATerminalStateUnderAnyOrderOfEvents() {
        // doc 28 § Testing: "event chains reach a terminal state". For a quest the property is that no
        // sequence of events leaves one running for ever — every path ends turned in, failed or
        // abandoned, and none of them ends somewhere that is not one of those.
        var random = new GameplayRandom(0xC0FFEEul);
        var undead = Content.Undead(catalog.Tags);
        var addresses = new[] { Content.Prologue, Content.Escort, Content.Vigil };

        for (var run = 0; run < 200; run++) {
            using var journal = Journal();

            foreach (var address in addresses) {
                journal.Accept(DefId.From(address));
            }

            for (var step = 0; step < 40; step++) {
                switch (random.NextInt(6)) {
                    case 0:
                        bus.Post(Content.Kill(undead));

                        break;

                    case 1:
                        Collect(random.NextInt(-2, 3));

                        break;

                    case 2:
                        bus.Post(new(Content.Verb(QuestVerbs.Escort), DefId.From(Content.Villager), Instigator: 1));

                        break;

                    case 3:
                        bus.Post(new(Content.Verb(QuestVerbs.EscortFailed), DefId.From(Content.Villager), Instigator: 1));

                        break;

                    default:
                        journal.Tick(random.NextFloat() * 20f);

                        break;
                }
            }

            // Whatever happened, drive them home: everything still running is finished by hand, and
            // every one of them must accept being finished.
            journal.Tick(1000f);

            foreach (var address in addresses) {
                var id = DefId.From(address);

                if (journal.StatusOf(id) == QuestStatus.ReadyToTurnIn) {
                    var choice = journal.Find(id)!.Template.Reward.NeedsChoice ? 0 : -1;

                    Assert.Equal(QuestRefusal.None, journal.TurnIn(id, choice, out _));
                } else if (journal.Find(id) is not null) {
                    Assert.True(journal.Abandon(id));
                }

                Assert.Contains(
                    journal.StatusOf(id),
                    new[] { QuestStatus.TurnedIn, QuestStatus.Failed, QuestStatus.Abandoned }
                );
            }

            Assert.Equal(0, journal.Count);
        }
    }
}
