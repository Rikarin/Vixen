// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using Vixen.Gameplay.Collections;
using Vixen.Gameplay.Exploration;
using Vixen.Gameplay.Progression;
using Vixen.Gameplay.Quests;
using Xunit;

namespace Vixen.Live.Gameplay.Tests;

/// <summary>The smallest content each of the four codecs needs, and one build that has less of it.</summary>
public static class SectionContent {
    public const string Curve = "progression/curve";
    public const string Tree = "talents/fire";
    public const string Smithing = "professions/smithing";
    public const string Ebonhawke = "factions/ebonhawke";
    public const string Prologue = "quests/prologue";
    public const string Errand = "quests/errand";
    public const string Queensdale = "maps/queensdale";
    public const string Crown = "collect/look/crown";

    public static DefinitionCatalog Progression() =>
        new DefinitionCatalogBuilder()
            .Add(Curve, new ExperienceCurveDefinition { MaximumLevel = 20, Base = 100f, Growth = 1f })
            .Add(
                Tree,
                new TalentTreeDefinition {
                    DisplayName = "Fire",
                    Nodes = [new() { Id = "kindle", DisplayName = "Kindle", MaximumRanks = 3 }]
                }
            )
            .Add(Smithing, new ProfessionDefinition { DisplayName = "Smithing", MaximumSkill = 500 })
            .Add(Ebonhawke, new ReputationDefinition { DisplayName = "Ebonhawke", Minimum = -6000, Maximum = 42000 })
            .Build();

    public static DefinitionCatalog Quests() {
        var builder = new DefinitionCatalogBuilder();

        foreach (var verb in QuestVerbs.All) {
            builder.AddTag(verb);
        }

        return builder
            .AddTag("Creature.Undead")
            .Add(
                Prologue,
                new QuestDefinition {
                    DisplayName = "A Prologue",
                    Stages = [
                        new() {
                            Id = "hunt",
                            Objectives = [
                                new() { Type = "Kill", Count = 3, TargetTags = ["Creature.Undead"] },
                                new() { Type = "Survive", Count = 30 }
                            ]
                        },
                        new() { Id = "return", Objectives = [new() { Type = "Interact", Count = 1 }] }
                    ]
                }
            )
            .Add(Errand, new QuestDefinition { DisplayName = "An Errand", Stages = [new() { Id = "only" }] })
            .Build();
    }

    /// <summary>The same build with the errand taken out, which is what a rollback looks like.</summary>
    public static DefinitionCatalog QuestsWithoutTheErrand() {
        var builder = new DefinitionCatalogBuilder();

        foreach (var verb in QuestVerbs.All) {
            builder.AddTag(verb);
        }

        return builder
            .AddTag("Creature.Undead")
            .Add(Prologue, new QuestDefinition { DisplayName = "A Prologue", Stages = [new() { Id = "hunt" }] })
            .Build();
    }

    public static DefinitionCatalog Exploration(int columns = 16, int rows = 8) =>
        new DefinitionCatalogBuilder()
            .Add(
                Queensdale,
                new MapDefinition {
                    DisplayName = "Queensdale",
                    Columns = columns,
                    Rows = rows,
                    Tag = "Completion.Queensdale",
                    Points = [
                        new() { Id = "ascalon", Kind = PointKind.Landmark, Tag = "Discovered.Queensdale.Ascalon" },
                        new() { Id = "falls", Kind = PointKind.Vista, Tag = "Discovered.Queensdale.Falls" }
                    ]
                }
            )
            .Build();

    public static DefinitionCatalog Collections() =>
        new DefinitionCatalogBuilder()
            .Add(
                Crown,
                new CollectibleDefinition {
                    DisplayName = "A crown",
                    Kind = CollectibleKind.Appearance,
                    Slot = "Slot.Head",
                    Tag = "Collected.Look.Crown"
                }
            )
            .Build();
}

public class ProgressionSectionTests {
    readonly ProgressionLibrary library = ProgressionLibrary.Compile(SectionContent.Progression());
    readonly ProgressionState state;
    readonly ProgressionSection section;

    public ProgressionSectionTests() {
        state = new(library);
        section = new(state);
    }

    ProgressionState Reload(ReadOnlyMemory<byte> bytes) {
        var fresh = new ProgressionState(library);

        new ProgressionSection(fresh).Load(bytes);

        return fresh;
    }

    [Fact]
    public void ALevelComesBackWithTheExperienceTowardsTheNextOne() {
        // ⚠ SetLevel zeroes the experience, which is right for a boost and wrong for a load — doing
        // it that way throws away everything earned towards the next level, every single login.
        state.Award(250);

        var back = Reload(section.Save());

        Assert.Equal(state.Level, back.Level);
        Assert.Equal(state.Experience, back.Experience);
        Assert.Equal(250L, back.TotalExperience);
        Assert.NotEqual(0, back.Experience);
    }

    [Fact]
    public void ProfessionsReputationsAndTalentsAllComeBack() {
        state.Train(DefId.From(SectionContent.Smithing), 120);
        state.Earn(DefId.From(SectionContent.Ebonhawke), 4200);
        state.TalentPoints = 5;
        state.Allocate(DefId.From(SectionContent.Tree), new TalentAllocation().Set("kindle", 2));

        var back = Reload(section.Save());

        Assert.Equal(120, back.SkillIn(DefId.From(SectionContent.Smithing)));
        Assert.Equal(4200, back.StandingWith(DefId.From(SectionContent.Ebonhawke)));
        Assert.Equal(5, back.TalentPoints);
        Assert.Equal(2, back.AllocationIn(DefId.From(SectionContent.Tree)).RanksOf("kindle"));
    }

    [Fact]
    public void ABuildAPatchMadeIllegalIsNotWipedOnLogin() {
        // ⚠ The one where re-validating hurts most. Allocate() would reject this against a tree that
        // no longer allows it, and the character would find their talents gone with no refund and no
        // message — a game that wants them respecced does it as a migration that gives the points back.
        state.TalentPoints = 5;
        state.Allocate(DefId.From(SectionContent.Tree), new TalentAllocation().Set("kindle", 3));

        var bytes = section.Save();

        state.TalentPoints = 0;

        var back = Reload(bytes);

        Assert.Equal(3, back.AllocationIn(DefId.From(SectionContent.Tree)).RanksOf("kindle"));
    }

    [Fact]
    public void ASkillIsNotDroppedForBeingInAProfessionThisBuildLostAndIsNotClamped() {
        // ⚠ Two rules in one. Clamping on load makes a patch that lowers a cap destroy the difference
        // for everybody on their next login, and reverting the patch does not bring it back — the
        // next Train() clamps, which is late enough to be reversible.
        state.SeatSkill(DefId.From("professions/gone"), 900);
        state.SeatSkill(DefId.From(SectionContent.Smithing), 900);

        var back = Reload(section.Save());

        Assert.Equal(900, back.SkillIn(DefId.From("professions/gone")));
        Assert.Equal(900, back.SkillIn(DefId.From(SectionContent.Smithing)));
        Assert.Equal(500, back.Train(DefId.From(SectionContent.Smithing), 0));
    }

    [Fact]
    public void TwoStatesHoldingTheSameCharacterWriteTheSameBytes() {
        var other = new ProgressionState(library);

        state.Train(DefId.From(SectionContent.Smithing), 10);
        state.Earn(DefId.From(SectionContent.Ebonhawke), 20);
        state.TalentPoints = 3;
        state.Allocate(DefId.From(SectionContent.Tree), new TalentAllocation().Set("kindle", 1));

        other.Earn(DefId.From(SectionContent.Ebonhawke), 20);
        other.TalentPoints = 3;
        other.Allocate(DefId.From(SectionContent.Tree), new TalentAllocation().Set("kindle", 1));
        other.Train(DefId.From(SectionContent.Smithing), 10);

        Assert.Equal(section.Save().ToArray(), new ProgressionSection(other).Save().ToArray());
    }

    [Fact]
    public void AVersionThisBuildDoesNotReadChangesNothing() {
        // ⚠ Left alone rather than guessed at, and the bytes stay in the profile because the
        // container holds them. A character who zones back finds them intact.
        var bytes = section.Save().ToArray();

        bytes[0] = 99;

        var back = new ProgressionState(library);

        back.Seat(7, 40, 500);
        new ProgressionSection(back).Load(bytes);

        Assert.Equal(7, back.Level);
    }

    [Fact]
    public void TruncatedBytesLoadWhatTheyHaveRatherThanThrowing() {
        state.Train(DefId.From(SectionContent.Smithing), 120);

        var bytes = section.Save().ToArray();
        var back = Reload(bytes.AsMemory(0, 20));

        Assert.Equal(1, back.Level);
        Assert.Equal(0, back.SkillIn(DefId.From(SectionContent.Smithing)));
    }
}

public class QuestSectionTests : IDisposable {
    readonly DefinitionCatalog catalog = SectionContent.Quests();
    readonly QuestLibrary library;
    readonly GameplayEventBus bus = new();
    readonly QuestJournal journal;
    readonly QuestSection section;

    public QuestSectionTests() {
        library = QuestLibrary.Compile(catalog);
        journal = new(library, bus, new(1));
        section = new(journal);
    }

    QuestJournal Reload(ReadOnlyMemory<byte> bytes, QuestLibrary? into = null) {
        var fresh = new QuestJournal(into ?? library, new GameplayEventBus(), new(1));

        new QuestSection(fresh).Load(bytes);

        return fresh;
    }

    [Fact]
    public void AQuestComesBackOnItsStageWithItsCountersAndItsTags() {
        journal.Accept(DefId.From(SectionContent.Prologue));

        var entry = journal.Find(DefId.From(SectionContent.Prologue))!;

        entry.Tracker!.Seat(0, 2f, false);
        entry.Tracker.Seat(1, 12.5f, false);

        var back = Reload(section.Save());
        var restored = back.Find(DefId.From(SectionContent.Prologue))!;

        Assert.Equal(QuestStatus.Active, restored.Status);
        Assert.Equal(2, restored.Tracker!.ProgressOf(0));
        Assert.Equal(12.5f, restored.Tracker.Exact(1));
    }

    [Fact]
    public void ATimedObjectiveKeepsItsFraction() {
        // ⚠ ProgressOf truncates. Saving that instead of Exact loses the fraction on every write, so
        // a player who logs in and out often enough never finishes a survival objective.
        journal.Accept(DefId.From(SectionContent.Prologue));
        journal.Find(DefId.From(SectionContent.Prologue))!.Tracker!.Seat(1, 29.75f, false);

        var back = Reload(section.Save());

        Assert.Equal(29.75f, back.Find(DefId.From(SectionContent.Prologue))!.Tracker!.Exact(1));
    }

    [Fact]
    public void RestoringRaisesNoAdvances() {
        // ⚠ Replaying the advances that made this progress would announce every objective again,
        // settle every stage again and fire a reward chain a second time.
        journal.Accept(DefId.From(SectionContent.Prologue));
        journal.Find(DefId.From(SectionContent.Prologue))!.Tracker!.Seat(0, 3f, true);

        var bytes = section.Save();
        var fresh = new QuestJournal(library, new GameplayEventBus(), new(1));
        var advances = 0;

        fresh.Advanced += (_, _) => advances++;
        new QuestSection(fresh).Load(bytes);

        Assert.Equal(0, advances);
    }

    [Fact]
    public void AQuestAlreadyFinishedComesBackReadyRatherThanActive() {
        journal.Accept(DefId.From(SectionContent.Errand));

        Assert.Equal(QuestStatus.ReadyToTurnIn, journal.Find(DefId.From(SectionContent.Errand))!.Status);

        var back = Reload(section.Save());

        Assert.Equal(QuestStatus.ReadyToTurnIn, back.Find(DefId.From(SectionContent.Errand))!.Status);
    }

    [Fact]
    public void HistorySurvivesTheQuestItselfBeingGone() {
        // ⚠ The asymmetry, and it is deliberate: history is what QuestRepeat.Once reads, so losing
        // an id lets somebody take a one-off quest again — and an id is all history needs.
        journal.SeatHistory(DefId.From(SectionContent.Errand), QuestStatus.TurnedIn);

        var back = Reload(section.Save(), QuestLibrary.Compile(SectionContent.QuestsWithoutTheErrand()));

        Assert.Equal(QuestStatus.TurnedIn, back.StatusOf(DefId.From(SectionContent.Errand)));
    }

    [Fact]
    public void AnActiveQuestThisBuildLostIsSkippedAndTheOnesBehindItAreNot() {
        // The other half of the asymmetry: a quest with no template has no stages, no objectives and
        // no tags, so there is nothing to hold — but the reader still has to walk past it.
        journal.Accept(DefId.From(SectionContent.Errand));
        journal.Accept(DefId.From(SectionContent.Prologue));
        journal.SeatHistory(DefId.From("quests/older"), QuestStatus.TurnedIn);

        var back = Reload(section.Save(), QuestLibrary.Compile(SectionContent.QuestsWithoutTheErrand()));

        Assert.Null(back.Find(DefId.From(SectionContent.Errand)));
        Assert.NotNull(back.Find(DefId.From(SectionContent.Prologue)));
        Assert.Equal(QuestStatus.TurnedIn, back.StatusOf(DefId.From("quests/older")));
    }

    [Fact]
    public void AQuestIsNotAskedItsRequirementsAgainOnLoad() {
        // ⚠ A character who took a quest at level ten is not asked at level nine whether they may
        // still have it. Seat is what makes that true; Accept would refuse.
        journal.Accept(DefId.From(SectionContent.Prologue));

        var bytes = section.Save();
        var fresh = new QuestJournal(library, new GameplayEventBus(), new(1), new Nothing());

        new QuestSection(fresh).Load(bytes);

        Assert.NotNull(fresh.Find(DefId.From(SectionContent.Prologue)));
    }

    /// <inheritdoc />
    public void Dispose() {
        journal.Dispose();
        GC.SuppressFinalize(this);
    }

    sealed class Nothing : IRequirementContext {
        GameplayTagSet? IRequirementContext.Tags => null;

        public bool TryGetValue(AttributeId subject, out float value) {
            value = 0f;

            return false;
        }
    }
}

public class ExplorationSectionTests {
    readonly ExplorationLibrary library = ExplorationLibrary.Compile(SectionContent.Exploration());
    readonly ExplorationRecord record;
    readonly ExplorationSection section;

    public ExplorationSectionTests() {
        record = new(library);
        section = new(record);
    }

    MapChart Map => library.Find(DefId.From(SectionContent.Queensdale))!;

    [Fact]
    public void DiscoveriesAndFogBothComeBack() {
        record.Discover(Map, Map.Points[0]);
        record.Reveal(Map, 4, 4, 2);

        var back = new ExplorationRecord(library);

        new ExplorationSection(back).Load(section.Save());

        Assert.True(back.HasFound(Map, Map.Points[0]));
        Assert.True(back.IsRevealed(Map, 4, 4));
        Assert.Equal(record.RevealedOn(Map), back.RevealedOn(Map));
    }

    [Fact]
    public void RestoringAnnouncesNothing() {
        // ⚠ Discover with a null context skips the requirements and still raises Found and Completed,
        // so a login would toast every landmark the character has ever visited.
        record.Discover(Map, Map.Points[0]);
        record.Discover(Map, Map.Points[1]);

        var bytes = section.Save();
        var back = new ExplorationRecord(library);
        var announced = 0;
        var completed = 0;

        back.Found += (_, _) => announced++;
        back.Completed += _ => completed++;
        new ExplorationSection(back).Load(bytes);

        Assert.Equal(0, announced);
        Assert.Equal(0, completed);
        Assert.True(back.IsComplete(Map));
    }

    [Fact]
    public void ARestoredRecordStillCarriesItsTags() {
        // A restored record whose tags are missing is a character every tag query answers wrong about.
        record.Discover(Map, Map.Points[0]);

        var back = new ExplorationRecord(library);

        new ExplorationSection(back).Load(section.Save());

        Assert.Equal(record.Tags.Count, back.Tags.Count);
    }

    [Fact]
    public void AResizedMapLosesItsFogAndSaysSo() {
        // ⚠ A bitmap read into a grid of a different width is a character whose explored map has
        // quietly become diagonal stripes. Losing one map's fog on the patch that resized it is the
        // honest outcome.
        record.Reveal(Map, 4, 4, 2);

        var bytes = section.Save();
        var wider = ExplorationLibrary.Compile(SectionContent.Exploration(columns: 32));
        var back = new ExplorationRecord(wider);
        var into = new ExplorationSection(back);

        into.Load(bytes);

        Assert.Equal(1, into.Resized);
        Assert.Equal(0f, back.RevealedOn(wider.Find(DefId.From(SectionContent.Queensdale))!));
    }

    [Fact]
    public void AMapNobodyHasBeenToIsNotAResize() {
        var back = new ExplorationRecord(library);
        var into = new ExplorationSection(back);

        into.Load(section.Save());

        Assert.Equal(0, into.Resized);
    }
}

public class WardrobeSectionTests {
    readonly DefinitionCatalog catalog = SectionContent.Collections();
    readonly CollectionLibrary library;
    readonly CollectionRecord record;
    readonly Wardrobe wardrobe;
    readonly WardrobeSection section;

    public WardrobeSectionTests() {
        library = CollectionLibrary.Compile(catalog);
        record = new(library);
        wardrobe = new(record);
        section = new(wardrobe, catalog.Tags);
    }

    GameplayTag Head => catalog.Tags.Resolve("Slot.Head");

    Collectible Crown => library.Find(DefId.From(SectionContent.Crown))!;

    [Fact]
    public void AnOverrideAHiddenSlotAndATitleAllComeBack() {
        record.Unlock(Crown);
        wardrobe.Show(Crown);
        wardrobe.Hide(Head, false);
        wardrobe.SeatTitle(DefId.From("collect/title/slayer"));

        var back = new Wardrobe(record);

        new WardrobeSection(back, catalog.Tags).Load(section.Save());

        Assert.Equal(Crown.Id, back.OverrideOf(Head));
        Assert.Equal(DefId.From("collect/title/slayer"), back.Title);
    }

    [Fact]
    public void ASlotIsWrittenByNameSoRenumberingTheTagTreeCannotMoveIt() {
        // ⚠ A GameplayTag is an index into a pre-order walk of the build's tag tree, so adding one
        // tag renumbers every tag after it. A wardrobe stored by index and read back on the next
        // patch is a character whose helm override has silently become their boots.
        record.Unlock(Crown);
        wardrobe.Show(Crown);

        var bytes = section.Save();
        var shifted = new DefinitionCatalogBuilder()
            .AddTag("Aaa.Inserted.Before.Everything")
            .Add(
                SectionContent.Crown,
                new CollectibleDefinition {
                    DisplayName = "A crown",
                    Kind = CollectibleKind.Appearance,
                    Slot = "Slot.Head",
                    Tag = "Collected.Look.Crown"
                }
            )
            .Build();

        Assert.NotEqual(Head, shifted.Tags.Resolve("Slot.Head"));

        var into = CollectionLibrary.Compile(shifted);
        var back = new Wardrobe(new CollectionRecord(into));

        new WardrobeSection(back, shifted.Tags).Load(bytes);

        Assert.Equal(Crown.Id, back.OverrideOf(shifted.Tags.Resolve("Slot.Head")));
    }

    [Fact]
    public void AnAppearanceTheyNoLongerHaveIsKeptRatherThanForgotten() {
        // ⚠ Resolve re-checks the unlock every time it draws, so checking again at load would throw
        // the player's choice away for good where leaving it stored lets a re-grant bring it back.
        record.Unlock(Crown);
        wardrobe.Show(Crown);

        var bytes = section.Save();
        var stripped = new CollectionRecord(library);
        var back = new Wardrobe(stripped);

        new WardrobeSection(back, catalog.Tags).Load(bytes);

        Assert.Equal(DefId.None, back.Resolve(Head, DefId.None));
        Assert.Equal(Crown.Id, back.OverrideOf(Head));

        stripped.Unlock(Crown);

        Assert.Equal(Crown.Id, back.Resolve(Head, DefId.None));
    }

    [Fact]
    public void ASlotThisBuildHasNoTagForIsCountedRatherThanGuessedAt() {
        wardrobe.Seat(Head, DefId.From("collect/look/helm"));

        var bytes = section.Save();
        var bare = new DefinitionCatalogBuilder().AddTag("Slot.Feet").Build();
        var into = new WardrobeSection(new Wardrobe(record), bare.Tags);

        into.Load(bytes);

        Assert.Equal(1, into.UnknownSlots);
    }
}
