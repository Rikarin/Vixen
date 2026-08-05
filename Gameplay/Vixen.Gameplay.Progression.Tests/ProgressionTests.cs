// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Progression.Tests;

/// <summary>A curve, a three-row talent tree, a profession, a faction and two specialisations.</summary>
public static class Content {
    public const string Curve = "progression/curve";
    public const string Tree = "talents/fire";
    public const string Smithing = "professions/smithing";
    public const string Ebonhawke = "factions/ebonhawke";
    public const string Pyromancer = "specialisations/pyromancer";
    public const string Frostbinder = "specialisations/frostbinder";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .Add(
                Curve,
                new ExperienceCurveDefinition {
                    MaximumLevel = 10,
                    Thresholds = [100, 200, 300],
                    Base = 1000f,
                    Growth = 2f
                }
            )
            .Add(
                Tree,
                new TalentTreeDefinition {
                    DisplayName = "Fire",
                    Nodes = [
                        new() {
                            Id = "kindle",
                            DisplayName = "Kindle",
                            MaximumRanks = 3,
                            Modifiers = [new() { Attribute = "Power", Op = ModifierOp.AddPercent, Value = 0.02f }],
                            GrantsTags = ["Talent.Fire.Kindle"]
                        },
                        new() {
                            Id = "blaze",
                            DisplayName = "Blaze",
                            RequiredPoints = 3,
                            Requires = [new() { Node = "kindle", Ranks = 2 }],
                            Modifiers = [new() { Attribute = "CritChance", Op = ModifierOp.Add, Value = 0.05f }]
                        },
                        new() {
                            Id = "inferno",
                            DisplayName = "Inferno",
                            CostPerRank = 2,
                            RequiredPoints = 3,
                            Requires = [new() { Node = "blaze", Ranks = 1 }],
                            GrantsTags = ["Talent.Fire.Inferno"]
                        }
                    ]
                }
            )
            .Add(
                Smithing,
                new ProfessionDefinition {
                    DisplayName = "Smithing",
                    Tag = "Profession.Smithing",
                    MaximumSkill = 500,
                    Tiers = [
                        new() { DisplayName = "Apprentice", Skill = 1, Tag = "Profession.Smithing.Apprentice" },
                        new() { DisplayName = "Master", Skill = 300, Tag = "Profession.Smithing.Master" },
                        new() { DisplayName = "Journeyman", Skill = 100, Tag = "Profession.Smithing.Journeyman" }
                    ]
                }
            )
            .Add(
                Ebonhawke,
                new ReputationDefinition {
                    DisplayName = "Ebonhawke",
                    Tag = "Faction.Ebonhawke",
                    Minimum = -6000,
                    Maximum = 42000,
                    Ranks = [
                        new() { DisplayName = "Hated", Threshold = -6000, Tag = "Faction.Ebonhawke.Hated" },
                        new() { DisplayName = "Neutral", Threshold = 0, Tag = "Faction.Ebonhawke.Neutral" },
                        new() { DisplayName = "Honoured", Threshold = 21000, Tag = "Faction.Ebonhawke.Honoured" }
                    ]
                }
            )
            .Add(
                Pyromancer,
                new SpecialisationDefinition {
                    DisplayName = "Pyromancer",
                    Tag = "Specialisation.Pyromancer",
                    TalentTree = Tree,
                    Requirements = [
                        new() {
                            Kind = RequirementKind.Value,
                            Subject = "Level",
                            Comparison = RequirementComparison.AtLeast,
                            Value = 5f
                        }
                    ],
                    Modifiers = [new() { Attribute = "Power", Op = ModifierOp.Add, Value = 50f }]
                }
            )
            .Add(
                Frostbinder,
                new SpecialisationDefinition {
                    DisplayName = "Frostbinder",
                    Tag = "Specialisation.Frostbinder",
                    Requirements = [
                        new() {
                            Kind = RequirementKind.Value,
                            Subject = "Faction.Ebonhawke",
                            Comparison = RequirementComparison.AtLeast,
                            Value = 21000f
                        }
                    ]
                }
            )
            .Build();

    public static ProgressionLibrary Library() => ProgressionLibrary.Compile(Catalog());

    public static ProgressionState State() => new(Library());
}

public class ExperienceCurveTests {
    [Fact]
    public void TheTableIsUsedWhereItExistsAndTheFormulaWhereItDoesNot() {
        var curve = Content.Library().Curve;

        Assert.Equal(10, curve.MaximumLevel);
        Assert.Equal(100, curve.CostOf(1));
        Assert.Equal(200, curve.CostOf(2));
        Assert.Equal(300, curve.CostOf(3));

        // The formula carries on from where the table stopped, doubling each level.
        Assert.Equal(600, curve.CostOf(4));
        Assert.Equal(1200, curve.CostOf(5));
    }

    [Fact]
    public void ThereIsNothingToPayAtTheMaximumLevel() {
        var curve = Content.Library().Curve;

        Assert.Equal(0, curve.CostOf(10));
        Assert.Equal(0, curve.CostOf(99));
        Assert.Equal(0, curve.CostOf(0));
    }

    [Fact]
    public void TheTotalToALevelIsTheSumOfWhatCameBefore() {
        var curve = Content.Library().Curve;

        Assert.Equal(0, curve.TotalTo(1));
        Assert.Equal(100, curve.TotalTo(2));
        Assert.Equal(600, curve.TotalTo(4));
    }

    [Fact]
    public void ACatalogWithNoCurveGetsTheDefaultOne() {
        Assert.Equal(80, ProgressionLibrary.Empty.Curve.MaximumLevel);
    }

    [Fact]
    public void TwoCurvesAreReportedBecauseACharacterHasOneLevel() {
        var catalog = new DefinitionCatalogBuilder()
            .Add("progression/a", new ExperienceCurveDefinition { MaximumLevel = 10 })
            .Add("progression/b", new ExperienceCurveDefinition { MaximumLevel = 20 })
            .Build();

        Assert.Contains(
            ProgressionLibrary.Compile(catalog).Problems,
            problem => problem.Contains("2 experience curves", StringComparison.Ordinal)
        );
    }
}

public class ExperienceTests {
    [Fact]
    public void OneAwardCanCrossSeveralLevels() {
        var state = Content.State();

        var gain = state.Award(650);

        Assert.Equal(3, gain.Levels);
        Assert.Equal(4, gain.Level);
        Assert.Equal(50, state.Experience);
        Assert.Equal(650, state.TotalExperience);
        Assert.Equal(550, state.ToNextLevel);
    }

    [Fact]
    public void ExperiencePastTheCapIsWastedRatherThanBanked() {
        var state = Content.State();
        state.SetLevel(10);

        var gain = state.Award(1000);

        Assert.Equal(0, gain.Levels);
        Assert.Equal(1000, gain.Wasted);
        Assert.Equal(0, state.Experience);
        Assert.True(state.IsMaximumLevel);
    }

    [Fact]
    public void AnAwardOfNothingChangesNothing() {
        var state = Content.State();

        Assert.Equal(0, state.Award(0).Levels);
        Assert.Equal(0, state.Award(-500).Levels);
        Assert.Equal(1, state.Level);
    }

    [Fact]
    public void SettingTheLevelIsClampedToTheCurve() {
        var state = Content.State();

        state.SetLevel(999);
        Assert.Equal(10, state.Level);

        state.SetLevel(-5);
        Assert.Equal(1, state.Level);
    }
}

public class TalentTreeTests {
    static TalentTree Tree() => Content.Library().FindTree(DefId.From(Content.Tree))!;

    [Fact]
    public void ALegalAllocationIsAccepted() {
        var tree = Tree();

        var allocation = new TalentAllocation()
            .Set("kindle", 3)
            .Set("blaze", 1)
            .Set("inferno", 1);

        Assert.Equal(6, tree.CostOf(allocation));
        Assert.True(tree.Validate(allocation, 6).IsLegal);
    }

    [Fact]
    public void MorePointsThanTheCharacterHasIsRefused() {
        var tree = Tree();
        var allocation = new TalentAllocation().Set("kindle", 3);

        var verdict = tree.Validate(allocation, 2);

        Assert.Equal(TalentRejection.NotEnoughPoints, verdict.Rejection);
        Assert.Contains("spends 3", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreRanksThanANodeHasIsRefused() {
        var tree = Tree();

        var verdict = tree.Validate(new TalentAllocation().Set("kindle", 4), 10);

        Assert.Equal(TalentRejection.TooManyRanks, verdict.Rejection);
        Assert.Equal("kindle", verdict.Node);
    }

    [Fact]
    public void ANodeThisTreeDoesNotHaveIsRefused() {
        var tree = Tree();

        Assert.Equal(
            TalentRejection.UnknownNode,
            tree.Validate(new TalentAllocation().Set("nonexistent", 1), 10).Rejection
        );
    }

    [Fact]
    public void ARowGateIsATotalAndIsCheckedAgainstTheWholeAllocation() {
        var tree = Tree();

        // Blaze needs three points spent in the tree. Two is not enough — even though its own
        // prerequisite is satisfied.
        var short_ = new TalentAllocation().Set("kindle", 2).Set("blaze", 1);

        Assert.Equal(TalentRejection.RowLocked, tree.Validate(short_, 10).Rejection);

        var enough = new TalentAllocation().Set("kindle", 3).Set("blaze", 1);

        Assert.True(tree.Validate(enough, 10).IsLegal);
    }

    [Fact]
    public void APrerequisiteIsCheckedByRankRatherThanByPresence() {
        var tree = Tree();

        // Inferno's row gate is satisfied by three points in kindle, so what is left to fail is its
        // prerequisite — one rank of blaze, which is not taken.
        var missing = new TalentAllocation().Set("kindle", 3).Set("inferno", 1);

        Assert.Equal(TalentRejection.MissingPrerequisite, tree.Validate(missing, 10).Rejection);

        // And blaze itself needs *two* ranks of kindle rather than merely one, which the row gate
        // does not happen to cover.
        var thin = new TalentAllocation().Set("kindle", 3).Set("blaze", 1);

        Assert.True(tree.Validate(thin, 10).IsLegal);
    }

    [Fact]
    public void TheRowGateCountsRowsAboveItRatherThanTheWholeTree() {
        // ⚠ The point being spent on a row cannot be the point that opens it, or a three-point gate
        // is really a two-point one. Kindle is the only row above blaze, so only kindle counts.
        var tree = Tree();

        Assert.Equal(
            TalentRejection.RowLocked,
            tree.Validate(new TalentAllocation().Set("kindle", 2).Set("blaze", 1), 10).Rejection
        );

        Assert.True(tree.Validate(new TalentAllocation().Set("kindle", 3).Set("blaze", 1), 10).IsLegal);
    }

    [Fact]
    public void ARankMultipliesTheValueRatherThanRepeatingTheModifier() {
        // Five separate +2 % modifiers from one source cannot be told apart on removal, and for a
        // multiplicative bucket compose to something other than +10 %.
        var tree = Tree();
        var allocation = new TalentAllocation().Set("kindle", 3);
        var modifiers = new List<Modifier>();

        tree.Modifiers(allocation, ModifierSource.From(new(1), 1), modifiers);

        var kindle = Assert.Single(modifiers);

        Assert.Equal(AttributeId.From("Power"), kindle.Attribute);
        Assert.Equal(0.06f, kindle.Value, 5);
    }

    [Fact]
    public void TagsComeFromEveryNodeWithAnyRank() {
        var tree = Tree();
        var allocation = new TalentAllocation().Set("kindle", 1).Set("inferno", 1);
        var tags = new List<GameplayTag>();

        tree.Tags(allocation, tags);

        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void ACycleInThePrerequisitesIsRefusedAtCompileTime() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "talents/circular",
                new TalentTreeDefinition {
                    Nodes = [
                        new() { Id = "a", Requires = [new() { Node = "b" }] },
                        new() { Id = "b", Requires = [new() { Node = "a" }] }
                    ]
                }
            )
            .Build();

        Assert.Contains(
            ProgressionLibrary.Compile(catalog).Problems,
            problem => problem.Contains("cycle", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void TwoNodesWithOneIdAndAPrerequisiteThatIsNotThereAreReported() {
        var catalog = new DefinitionCatalogBuilder()
            .Add(
                "talents/broken",
                new TalentTreeDefinition {
                    Nodes = [
                        new() { Id = "a" },
                        new() { Id = "a" },
                        new() { Id = "c", Requires = [new() { Node = "nonexistent" }] },
                        new() { Id = string.Empty }
                    ]
                }
            )
            .Build();

        var problems = ProgressionLibrary.Compile(catalog).Problems;

        Assert.Contains(problems, problem => problem.Contains("two nodes called 'a'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("'nonexistent'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("no id", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAllocationIsCopiedIntoTheStateRatherThanAliased() {
        var state = Content.State();
        state.TalentPoints = 10;

        var allocation = new TalentAllocation().Set("kindle", 3);

        Assert.True(state.Allocate(DefId.From(Content.Tree), allocation).IsLegal);

        // A client that kept its copy and edited it must not change what the server accepted.
        allocation.Set("kindle", 99);

        Assert.Equal(3, state.AllocationIn(DefId.From(Content.Tree)).RanksOf("kindle"));
    }
}

public class RankedTrackTests {
    [Fact]
    public void RanksAreSortedAtCompileTimeSoAnOutOfOrderOneStillWorks() {
        // The profession's tiers are authored 1, 300, 100 — out of order on purpose.
        var track = Content.Library().FindProfession(DefId.From(Content.Smithing))!;

        Assert.Equal([1, 100, 300], track.Ranks.ToArray().Select(rank => rank.Threshold));
        Assert.Equal(-1, track.RankAt(0));
        Assert.Equal(0, track.RankAt(1));
        Assert.Equal(1, track.RankAt(150));
        Assert.Equal(2, track.RankAt(500));
    }

    [Fact]
    public void AFactionsRanksReachBelowZero() {
        var track = Content.Library().FindReputation(DefId.From(Content.Ebonhawke))!;

        Assert.Equal(0, track.RankAt(-6000));
        Assert.Equal(1, track.RankAt(0));
        Assert.Equal(2, track.RankAt(42000));
        Assert.Equal(-1, track.RankAt(-99999));
    }
}

public class ProgressionStateTests {
    [Fact]
    public void ADocExampleRequirementResolvesAgainstTheState() {
        // requires: [ Level >= 5, HasTag(Profession.Smithing), Faction.Ebonhawke >= 21000 ]
        var tags = Content.Catalog().Tags;
        var state = Content.State();

        var requirements = RequirementSet.Compile(
            [
                new() { Kind = RequirementKind.Value, Subject = "Level", Comparison = RequirementComparison.AtLeast, Value = 5f },
                new() { Kind = RequirementKind.HasTag, Subject = "Profession.Smithing" },
                new() {
                    Kind = RequirementKind.Value,
                    Subject = "Faction.Ebonhawke",
                    Comparison = RequirementComparison.AtLeast,
                    Value = 21000f
                }
            ],
            tags
        );

        Assert.False(requirements.IsMetBy(state));

        state.SetLevel(5);
        Assert.False(requirements.IsMetBy(state));

        state.Train(DefId.From(Content.Smithing), 50);
        Assert.False(requirements.IsMetBy(state));

        state.Earn(DefId.From(Content.Ebonhawke), 21000);
        Assert.True(requirements.IsMetBy(state));
    }

    [Fact]
    public void TrainingAndStandingAreClampedToTheirTracks() {
        var state = Content.State();

        Assert.Equal(500, state.Train(DefId.From(Content.Smithing), 9999));
        Assert.Equal(-6000, state.Earn(DefId.From(Content.Ebonhawke), -99999));
        Assert.Equal(42000, state.Earn(DefId.From(Content.Ebonhawke), 999999));
    }

    [Fact]
    public void ATrackThisBuildDoesNotHaveChangesNothing() {
        var state = Content.State();

        Assert.Equal(0, state.Train(DefId.From("professions/nonexistent"), 100));
        Assert.Equal(0, state.Earn(DefId.From("factions/nonexistent"), 100));
    }

    [Fact]
    public void EveryRankAndTierReachedIsATagOnTheState() {
        var tags = Content.Catalog().Tags;
        var state = Content.State();

        state.Train(DefId.From(Content.Smithing), 320);

        Assert.True(state.Tags.Contains(tags.Require("Profession.Smithing")));
        Assert.True(state.Tags.Contains(tags.Require("Profession.Smithing.Master")));
        Assert.False(state.Tags.Contains(tags.Require("Profession.Smithing.Journeyman")));
    }

    [Fact]
    public void GoingDownARankTakesTheTagBack() {
        var tags = Content.Catalog().Tags;
        var state = Content.State();

        state.Earn(DefId.From(Content.Ebonhawke), 21000);
        Assert.True(state.Tags.Contains(tags.Require("Faction.Ebonhawke.Honoured")));

        state.Earn(DefId.From(Content.Ebonhawke), -21000);
        Assert.False(state.Tags.Contains(tags.Require("Faction.Ebonhawke.Honoured")));
        Assert.True(state.Tags.Contains(tags.Require("Faction.Ebonhawke.Neutral")));
    }

    [Fact]
    public void ASpecialisationChecksItsRequirementsAgainstTheStateItself() {
        var state = Content.State();

        Assert.False(state.Specialise(DefId.From(Content.Pyromancer)));

        state.SetLevel(5);
        Assert.True(state.Specialise(DefId.From(Content.Pyromancer)));
        Assert.Equal(DefId.From(Content.Pyromancer), state.Specialisation);

        // The other one wants reputation instead, and asks the same context.
        Assert.False(state.Specialise(DefId.From(Content.Frostbinder)));

        state.Earn(DefId.From(Content.Ebonhawke), 21000);
        Assert.True(state.Specialise(DefId.From(Content.Frostbinder)));
    }

    [Fact]
    public void ASpecialisationsTagIsOnTheStateAndTheOldOnesIsNot() {
        var tags = Content.Catalog().Tags;
        var state = Content.State();
        state.SetLevel(5);
        state.Earn(DefId.From(Content.Ebonhawke), 21000);

        state.Specialise(DefId.From(Content.Pyromancer));
        Assert.True(state.Tags.Contains(tags.Require("Specialisation.Pyromancer")));

        state.Specialise(DefId.From(Content.Frostbinder));
        Assert.False(state.Tags.Contains(tags.Require("Specialisation.Pyromancer")));
        Assert.True(state.Tags.Contains(tags.Require("Specialisation.Frostbinder")));
    }

    [Fact]
    public void EverythingAProgressionGrantsArrivesAsOneSetOfModifiers() {
        var state = Content.State();
        state.SetLevel(5);
        state.TalentPoints = 10;
        state.Specialise(DefId.From(Content.Pyromancer));
        state.Allocate(DefId.From(Content.Tree), new TalentAllocation().Set("kindle", 3).Set("blaze", 1));

        var source = ModifierSource.From(DefId.From("progression"), 1);
        var modifiers = new List<Modifier>();

        Assert.Equal(3, state.Modifiers(source, modifiers));
        Assert.All(modifiers, modifier => Assert.Equal(source, modifier.Source));

        var layout = new AttributeLayoutBuilder().Add("Power", 100f).Add("CritChance", 0f, 0f, 1f).Build();
        var attributes = new AttributeSet(layout);

        foreach (var modifier in modifiers) {
            attributes.Add(modifier);
        }

        // (100 + 50) × 1.06
        Assert.Equal(159f, attributes.ValueOf(AttributeId.From("Power")), 3);
        Assert.Equal(0.05f, attributes.ValueOf(AttributeId.From("CritChance")), 5);

        // And it all comes off exactly, because a progression is one modifier source.
        attributes.RemoveBySource(source);
        Assert.Equal(100f, attributes.ValueOf(AttributeId.From("Power")));
    }

    [Fact]
    public void AnIllegalAllocationChangesNothing() {
        var state = Content.State();
        state.TalentPoints = 2;

        var verdict = state.Allocate(DefId.From(Content.Tree), new TalentAllocation().Set("kindle", 3));

        Assert.Equal(TalentRejection.NotEnoughPoints, verdict.Rejection);
        Assert.Equal(0, state.AllocationIn(DefId.From(Content.Tree)).Count);
    }

    [Fact]
    public void ATreeThisBuildDoesNotHaveIsRefused() {
        var state = Content.State();

        Assert.False(state.Allocate(DefId.From("talents/nonexistent"), new()).IsLegal);
    }
}

public class ProgressionModuleTests {
    [Fact]
    public void TheModuleBringsFiveDefinitionTypesAndNoStats() {
        var composition = new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<ProgressionModule>()
            .Build();

        Assert.Equal(6, composition.Definitions.Count);
        Assert.Equal(0, composition.Attributes.Count);
        Assert.Contains(ProgressionModule.ProfessionRoot, composition.Tags);
        Assert.Contains(ProgressionModule.FactionRoot, composition.Tags);
        Assert.Contains(ProgressionModule.SpecialisationRoot, composition.Tags);
    }
}
