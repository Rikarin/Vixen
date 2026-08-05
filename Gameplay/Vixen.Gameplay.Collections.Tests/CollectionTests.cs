// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Collections.Tests;

/// <summary>
///     Seven collectibles and four achievements: one counted, one standing, one that cascades off the
///     first, and one whose criterion counts a verb the build does not have.
/// </summary>
public static class Content {
    public const string Gryphon = "collect/mount/gryphon";
    public const string Horse = "collect/mount/horse";
    public const string Cat = "collect/pet/cat";
    public const string Crown = "collect/look/crown";
    public const string Helm = "collect/look/helm";
    public const string Slayer = "collect/title/slayer";
    public const string Whistle = "collect/toy/whistle";

    public const string SlayUndead = "achieve/slayer";
    public const string Stabled = "achieve/stabled";
    public const string Decorated = "achieve/decorated";
    public const string Broken = "achieve/broken";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Event.Kill")
            .AddTag("Event.Craft")
            .AddTag("Kind.Undead")
            .AddTag("Kind.Beast")
            .Add(
                Gryphon,
                new CollectibleDefinition {
                    DisplayName = "A gryphon", Kind = CollectibleKind.Mount, Tag = "Collected.Mount.Gryphon"
                }
            )
            .Add(
                Horse,
                new CollectibleDefinition {
                    DisplayName = "A horse", Kind = CollectibleKind.Mount, Tag = "Collected.Mount.Horse"
                }
            )
            .Add(
                Cat,
                new CollectibleDefinition {
                    DisplayName = "A cat", Kind = CollectibleKind.Pet, Tag = "Collected.Pet.Cat"
                }
            )
            .Add(
                Crown,
                new CollectibleDefinition {
                    DisplayName = "A crown",
                    Kind = CollectibleKind.Appearance,
                    Slot = "Slot.Head",
                    Tag = "Collected.Look.Crown"
                }
            )
            .Add(
                Helm,
                new CollectibleDefinition {
                    DisplayName = "A helm",
                    Kind = CollectibleKind.Appearance,
                    Slot = "Slot.Head",
                    Tag = "Collected.Look.Helm"
                }
            )
            .Add(
                Slayer,
                new CollectibleDefinition {
                    DisplayName = "the Slayer", Kind = CollectibleKind.Title, Tag = "Collected.Title.Slayer"
                }
            )
            .Add(Whistle, new CollectibleDefinition { DisplayName = "A whistle", Kind = CollectibleKind.Toy })
            .Add(
                SlayUndead,
                new AchievementDefinition {
                    DisplayName = "Slayer",
                    Points = 20,
                    Tag = "Earned.Slayer",
                    Criteria = [
                        new() { Description = "Slay thirty undead", Verb = "Event.Kill", All = ["Kind.Undead"], Count = 30 }
                    ],
                    Unlocks = [Slayer]
                }
            )
            .Add(
                Stabled,
                new AchievementDefinition {
                    DisplayName = "Stabled",
                    Points = 10,
                    Tag = "Earned.Stabled",
                    // ⚠ "Own two mounts" is an ordinary Value requirement, because the record answers
                    // Collection.Mount. A tag test would only ever answer "has any".
                    Requires = [
                        new() {
                            Kind = RequirementKind.Value,
                            Subject = "Collection.Mount",
                            Comparison = RequirementComparison.AtLeast,
                            Value = 2f
                        }
                    ]
                }
            )
            .Add(
                Decorated,
                new AchievementDefinition {
                    DisplayName = "Decorated",
                    Points = 5,
                    // The cascade: earning Slayer unlocks the title, whose tag finishes this.
                    Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Collected.Title" }],
                    Unlocks = [Whistle]
                }
            )
            .Add(
                Broken,
                new AchievementDefinition {
                    DisplayName = "Broken",
                    Points = 100,
                    // ⚠ No verb at all. A *misspelt* verb would not be catchable — CollectTags hands
                    // it to the content build, which bakes it, so it resolves to a real range that
                    // nothing ever posts.
                    Criteria = [new() { Description = "Catch a fish", Count = 1 }]
                }
            )
            .Build();
}

public class CollectionTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly CollectionLibrary library;
    readonly CollectionRecord record;

    public CollectionTests() {
        library = CollectionLibrary.Compile(catalog);
        record = new(library);
    }

    Collectible Get(string address) => library.Find(DefId.From(address))!;

    Achievement Achievement(string address) => library.FindAchievement(DefId.From(address))!;

    GameplayTag Tag(string name) => catalog.Tags.Require(name);

    // ── Unlocking ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnlockingSomethingRecordsWhereItCameFrom() {
        Assert.True(record.Unlock(Get(Content.Gryphon), UnlockSource.Loot, DefId.From("boss/skarr")));

        var unlock = record.SourceOf(DefId.From(Content.Gryphon));

        Assert.NotNull(unlock);
        Assert.Equal(UnlockSource.Loot, unlock.Value.Source);
        Assert.Equal(DefId.From("boss/skarr"), unlock.Value.From);
        Assert.Equal(1, unlock.Value.Order);
    }

    [Fact]
    public void UnlockingSomethingTwiceIsNotNew() {
        Assert.True(record.Unlock(Get(Content.Cat)));
        Assert.False(record.Unlock(Get(Content.Cat)));
        Assert.Equal(1, record.Count);
    }

    [Fact]
    public void EverySortIsTheSameMechanismAndTheKindIsOnlyForCounting() {
        record.Unlock(Get(Content.Gryphon));
        record.Unlock(Get(Content.Horse));
        record.Unlock(Get(Content.Cat));

        Assert.Equal(3, record.Count);
        Assert.Equal(2, record.CountOf(CollectibleKind.Mount));
        Assert.Equal(1, record.CountOf(CollectibleKind.Pet));
        Assert.Equal(0, record.CountOf(CollectibleKind.Toy));
    }

    [Fact]
    public void UnlockingGrantsTheTag() {
        record.Unlock(Get(Content.Gryphon));

        Assert.True(record.Tags.Contains(Tag("Collected.Mount.Gryphon")));
    }

    [Fact]
    public void ThingsComeBackInTheOrderTheyWereGot() {
        record.Unlock(Get(Content.Cat));
        record.Unlock(Get(Content.Gryphon));
        record.Unlock(Get(Content.Horse));

        Assert.Equal(
            [DefId.From(Content.Cat), DefId.From(Content.Gryphon), DefId.From(Content.Horse)],
            record.Unlocks.Select(unlock => unlock.Collectible)
        );
    }

    // ── Counted achievements, off the bus ─────────────────────────────────────────────────────

    [Fact]
    public void ThirtyUndeadEarnsTheAchievementAndTheThirtiethIsWhatDoesIt() {
        var bus = new GameplayEventBus();
        var undead = new GameplayTagSet();
        var earned = 0;

        undead.Add(Tag("Kind.Undead"));
        record.Achieved += _ => earned++;
        record.Attach(bus);

        for (var kill = 0; kill < 29; kill++) {
            bus.Post(new(Tag("Event.Kill"), Tags: undead));
        }

        Assert.Equal(0, earned);
        Assert.Equal(29, record.ProgressOf(Achievement(Content.SlayUndead), 0));

        bus.Post(new(Tag("Event.Kill"), Tags: undead));

        // Two: Slayer, and Decorated through the cascade its unlocked title sets off.
        Assert.Equal(2, earned);
        Assert.True(record.IsEarned(Achievement(Content.SlayUndead)));
    }

    [Fact]
    public void AKillOfTheWrongSortDoesNotCount() {
        var bus = new GameplayEventBus();
        var beast = new GameplayTagSet();

        beast.Add(Tag("Kind.Beast"));
        record.Attach(bus);

        bus.Post(new(Tag("Event.Kill"), Tags: beast));
        bus.Post(new(Tag("Event.Craft"), Tags: beast));

        Assert.Equal(0, record.ProgressOf(Achievement(Content.SlayUndead), 0));
    }

    [Fact]
    public void ProgressIsCappedAtWhatTheCriterionAsksFor() {
        // An event worth a hundred must not bank progress the next tier would inherit.
        record.Observe(new(Tag("Event.Kill"), Amount: 100, Tags: Undead()));

        Assert.Equal(30, record.ProgressOf(Achievement(Content.SlayUndead), 0));
    }

    [Fact]
    public void AWatchIsDroppedTheMomentItsCriterionIsDone() {
        // ⚠ The cost of an achievement system falls as an account completes things, rather than
        // staying flat for ever. The accounts with the most live watches are the new ones.
        var before = record.Watching;

        Assert.True(before > 0);

        record.Observe(new(Tag("Event.Kill"), Amount: 30, Tags: Undead()));

        Assert.True(record.Watching < before, $"{record.Watching} watches left, was {before}");
    }

    [Fact]
    public void ACriterionWithNoVerbIsReportedAndNeverEarned() {
        // ⚠ The kernel's empty-range trap: an unknown prefix matches nothing, never everything. The
        // achievement stays unearned, which is right, and Compile says so, which is what matters.
        Assert.Contains(
            library.Problems,
            problem => problem.Contains("has no verb", StringComparison.Ordinal)
        );

        record.Refresh();

        Assert.False(record.IsEarned(Achievement(Content.Broken)));
    }

    [Fact]
    public void NotingACriterionByHandWorksWithoutABus() {
        Assert.False(record.Note(Achievement(Content.SlayUndead), 0, 29));
        Assert.True(record.Note(Achievement(Content.SlayUndead), 0));
        Assert.True(record.IsEarned(Achievement(Content.SlayUndead)));
    }

    // ── Standing achievements, and the values that make them expressible ──────────────────────

    [Fact]
    public void OwningTwoMountsEarnsTheStandingAchievement() {
        record.Unlock(Get(Content.Gryphon));

        Assert.False(record.IsEarned(Achievement(Content.Stabled)));

        record.Unlock(Get(Content.Horse));

        Assert.True(record.IsEarned(Achievement(Content.Stabled)));
        Assert.Equal(10, record.Points);
    }

    [Fact]
    public void ACollectionAnswersItsOwnCounts() {
        var context = (IRequirementContext)record;

        record.Unlock(Get(Content.Gryphon));
        record.Unlock(Get(Content.Cat));

        Assert.True(context.TryGetValue(AttributeId.From("Collection.Mount"), out var mounts));
        Assert.Equal(1f, mounts);

        Assert.True(context.TryGetValue(AttributeId.From(CollectionRecord.TotalValue), out var total));
        Assert.Equal(2f, total);

        Assert.False(context.TryGetValue(AttributeId.From("Level"), out _));
    }

    [Fact]
    public void AnAchievementIsNotEarnedUntilSomethingAsks() {
        // Nothing settles at construction: an achievement whose requirements happen to be met from
        // the start is earned by Refresh, so a caller decides when notifications fire.
        var fresh = new CollectionRecord(library);

        Assert.Equal(0, fresh.Earned);
    }

    // ── The cascade ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EarningOneAchievementCanEarnAnother() {
        // ⚠ Slayer unlocks the title; the title's tag finishes Decorated; Decorated unlocks the toy.
        // One kill sets off the whole chain, and it terminates because nothing is earned twice.
        var earned = new List<string>();

        record.Achieved += achievement => earned.Add(achievement.DisplayName);
        record.Observe(new(Tag("Event.Kill"), Amount: 30, Tags: Undead()));

        Assert.Equal(["Slayer", "Decorated"], earned);
        Assert.True(record.IsUnlocked(DefId.From(Content.Slayer)));
        Assert.True(record.IsUnlocked(DefId.From(Content.Whistle)));
        Assert.Equal(25, record.Points);
    }

    [Fact]
    public void AnAchievementIsNeverEarnedTwice() {
        var earned = 0;

        record.Achieved += _ => earned++;
        record.Note(Achievement(Content.SlayUndead), 0, 30);

        // Slayer and, through the cascade, Decorated.
        Assert.Equal(2, earned);

        record.Refresh();
        record.Refresh();

        Assert.Equal(2, earned);
        Assert.Equal(2, record.Earned);
    }

    [Fact]
    public void AnEarnedAchievementDoesNotUnEarnWhenWhatEarnedItGoesAway() {
        // ⚠ A refund, a sale or a patch must not take back something somebody already did.
        record.Unlock(Get(Content.Gryphon));
        record.Unlock(Get(Content.Horse));

        Assert.True(record.IsEarned(Achievement(Content.Stabled)));

        Assert.True(record.Revoke(Get(Content.Horse)));
        record.Refresh();

        Assert.True(record.IsEarned(Achievement(Content.Stabled)));
        Assert.Equal(1, record.CountOf(CollectibleKind.Mount));
    }

    // ── The wardrobe ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOverrideShowsTheAppearanceInsteadOfTheItem() {
        var wardrobe = new Wardrobe(record);
        var worn = DefId.From("items/rusty-helm");

        record.Unlock(Get(Content.Crown));

        Assert.True(wardrobe.Show(Get(Content.Crown)));
        Assert.Equal(DefId.From(Content.Crown), wardrobe.Resolve(Tag("Slot.Head"), worn));
    }

    [Fact]
    public void AnAppearanceThatIsNotUnlockedCannotBeWorn() {
        var wardrobe = new Wardrobe(record);

        Assert.False(wardrobe.Show(Get(Content.Crown)));
        Assert.Equal(0, wardrobe.Count);
    }

    [Fact]
    public void AnOverrideToSomethingRevokedFallsBackToTheRealItemRatherThanToNothing() {
        // ⚠ The rule the whole type exists for. An appearance can be taken back — a refund, a season
        // ending, a patch — and the character wearing it must not turn invisible. Resolve checks the
        // unlock every time precisely because there is no notification to miss.
        var wardrobe = new Wardrobe(record);
        var worn = DefId.From("items/rusty-helm");

        record.Unlock(Get(Content.Crown));
        wardrobe.Show(Get(Content.Crown));

        record.Revoke(Get(Content.Crown));

        Assert.Equal(worn, wardrobe.Resolve(Tag("Slot.Head"), worn));
        Assert.NotEqual(DefId.None, wardrobe.Resolve(Tag("Slot.Head"), worn));
    }

    [Fact]
    public void HidingASlotBeatsOverridingItAndGivesTheLookBackAfterwards() {
        // ⚠ "No helmet" and "a different helmet" are different wishes. A game that models hiding as
        // an override to nothing loses the chosen look the moment the box is ticked.
        var wardrobe = new Wardrobe(record);
        var worn = DefId.From("items/rusty-helm");

        record.Unlock(Get(Content.Crown));
        wardrobe.Show(Get(Content.Crown));

        Assert.True(wardrobe.Hide(Tag("Slot.Head")));
        Assert.Equal(DefId.None, wardrobe.Resolve(Tag("Slot.Head"), worn));

        Assert.True(wardrobe.Hide(Tag("Slot.Head"), false));
        Assert.Equal(DefId.From(Content.Crown), wardrobe.Resolve(Tag("Slot.Head"), worn));
    }

    [Fact]
    public void ASlotWithNothingSaidAboutItShowsWhatIsWorn() {
        var wardrobe = new Wardrobe(record);
        var worn = DefId.From("items/rusty-helm");

        Assert.Equal(worn, wardrobe.Resolve(Tag("Slot.Head"), worn));
    }

    [Fact]
    public void TheSecondOverrideOfASlotReplacesTheFirst() {
        var wardrobe = new Wardrobe(record);

        record.Unlock(Get(Content.Crown));
        record.Unlock(Get(Content.Helm));

        wardrobe.Show(Get(Content.Crown));
        wardrobe.Show(Get(Content.Helm));

        Assert.Equal(1, wardrobe.Count);
        Assert.Equal(DefId.From(Content.Helm), wardrobe.Resolve(Tag("Slot.Head"), DefId.None));
    }

    [Fact]
    public void OnlyATitleCanBeWornAsOneAndOnlyIfItIsUnlocked() {
        var wardrobe = new Wardrobe(record);

        Assert.False(wardrobe.Wear(Get(Content.Slayer)));

        record.Unlock(Get(Content.Slayer));

        Assert.True(wardrobe.Wear(Get(Content.Slayer)));
        Assert.Equal(DefId.From(Content.Slayer), wardrobe.Worn());

        record.Unlock(Get(Content.Cat));

        Assert.False(wardrobe.Wear(Get(Content.Cat)));
    }

    [Fact]
    public void ATitleThatIsTakenBackStopsShowing() {
        var wardrobe = new Wardrobe(record);

        record.Unlock(Get(Content.Slayer));
        wardrobe.Wear(Get(Content.Slayer));

        record.Revoke(Get(Content.Slayer));

        Assert.Equal(DefId.None, wardrobe.Worn());
    }

    [Fact]
    public void TwoCharactersShareACollectionAndNotAWardrobe() {
        // The split doc 28's paragraph does not make: unlocks are account-wide, presentation is not.
        var first = new Wardrobe(record);
        var second = new Wardrobe(record);

        record.Unlock(Get(Content.Crown));
        record.Unlock(Get(Content.Helm));

        first.Show(Get(Content.Crown));
        second.Show(Get(Content.Helm));

        Assert.Equal(DefId.From(Content.Crown), first.Resolve(Tag("Slot.Head"), DefId.None));
        Assert.Equal(DefId.From(Content.Helm), second.Resolve(Tag("Slot.Head"), DefId.None));
    }

    // ── Saving and loading ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASavedCollectionComesBackWithoutReDerivingAnything() {
        // ⚠ Not a replay. A patch that raised a threshold must not take back what somebody earned,
        // and one that lowered it must not hand out an achievement with no notification anybody saw.
        record.Observe(new(Tag("Event.Kill"), Amount: 30, Tags: Undead()));
        record.Note(Achievement(Content.SlayUndead), 0);

        var unlocks = record.Unlocks.ToArray();
        var earned = record.Achievements().Select(achievement => achievement.Id).ToArray();
        var counters = record.Counters().ToArray();

        var loaded = new CollectionRecord(library);

        loaded.Restore(unlocks, earned, counters);

        Assert.Equal(record.Count, loaded.Count);
        Assert.Equal(record.Earned, loaded.Earned);
        Assert.Equal(record.Points, loaded.Points);
        Assert.True(loaded.IsEarned(Achievement(Content.SlayUndead)));
        Assert.True(loaded.Tags.Contains(Tag("Earned.Slayer")));
    }

    [Fact]
    public void AHalfFinishedCriterionComesBackHalfFinished() {
        record.Observe(new(Tag("Event.Kill"), Amount: 12, Tags: Undead()));

        var counters = record.Counters().ToArray();
        var loaded = new CollectionRecord(library);

        loaded.Restore([], null, counters);

        Assert.Equal(12, loaded.ProgressOf(Achievement(Content.SlayUndead), 0));

        loaded.Observe(new(Tag("Event.Kill"), Amount: 18, Tags: Undead()));

        Assert.True(loaded.IsEarned(Achievement(Content.SlayUndead)));
    }

    [Fact]
    public void ARestoredAchievementIsNotWatchedForAgain() {
        var loaded = new CollectionRecord(library);
        var before = loaded.Watching;

        loaded.Restore([], [DefId.From(Content.SlayUndead)]);

        Assert.True(loaded.Watching < before);
        Assert.Equal(20, loaded.Points);
    }

    // ── Content problems ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAppearanceWithNoSlotIsReported() {
        var library = CollectionLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add("collect/look/nowhere", new CollectibleDefinition { Kind = CollectibleKind.Appearance })
                .Add(
                    "collect/pet/slotted",
                    new CollectibleDefinition { Kind = CollectibleKind.Pet, Slot = "Slot.Head" }
                )
                .Build()
        );

        Assert.Contains(library.Problems, problem => problem.Contains("no slot", StringComparison.Ordinal));
        Assert.Contains(library.Problems, problem => problem.Contains("Pet with a slot", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAchievementThatAsksForNothingIsReported() {
        var library = CollectionLibrary.Compile(
            new DefinitionCatalogBuilder().Add("achieve/free", new AchievementDefinition()).Build()
        );

        Assert.Contains(library.Problems, problem => problem.Contains("nothing at all", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAchievementThatRequiresItsOwnTagIsReported() {
        // It reads perfectly well in a spreadsheet and nothing at runtime would ever say so.
        var library = CollectionLibrary.Compile(
            new DefinitionCatalogBuilder()
                .AddTag("Earned.Ouroboros")
                .Add(
                    "achieve/ouroboros",
                    new AchievementDefinition {
                        Tag = "Earned.Ouroboros",
                        Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Earned.Ouroboros" }]
                    }
                )
                .Build()
        );

        Assert.Contains(
            library.Problems,
            problem => problem.Contains("its own precondition", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void AnAchievementThatUnlocksSomethingMissingIsReported() {
        var library = CollectionLibrary.Compile(
            new DefinitionCatalogBuilder()
                .AddTag("Event.Kill")
                .Add(
                    "achieve/phantom",
                    new AchievementDefinition {
                        Criteria = [new() { Verb = "Event.Kill" }], Unlocks = ["collect/ghost"]
                    }
                )
                .Build()
        );

        Assert.Contains(library.Problems, problem => problem.Contains("not in this build", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryPointInTheBuildIsWhatACompletionPercentageDividesBy() => Assert.Equal(135, library.Points);

    GameplayTagSet Undead() {
        var tags = new GameplayTagSet();

        tags.Add(Tag("Kind.Undead"));

        return tags;
    }
}
