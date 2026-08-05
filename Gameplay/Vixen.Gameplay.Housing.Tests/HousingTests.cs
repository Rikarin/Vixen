// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Gameplay.Housing.Tests;

/// <summary>One cottage, one guild hall, and five pieces that each exercise one rule.</summary>
public static class Content {
    public const string Cottage = "housing/cottage";
    public const string Hall = "housing/guild-hall";
    public const string Manor = "housing/manor";
    public const string Chair = "furniture/chair";
    public const string Painting = "furniture/painting";
    public const string Chandelier = "furniture/chandelier";
    public const string Forge = "furniture/forge";
    public const string Fountain = "furniture/fountain";

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Level.Twenty")
            .Add(
                Cottage,
                new PlotDefinition {
                    DisplayName = "A cottage",
                    Budget = 20,
                    Surfaces = HouseSurface.Floor | HouseSurface.Wall | HouseSurface.Ceiling,
                    SnapGrid = 0.5f,
                    SnapDegrees = 15f,
                    Tag = "House.Inside"
                }
            )
            .Add(
                Hall,
                new PlotDefinition {
                    DisplayName = "A guild hall",
                    Budget = 100,
                    Surfaces = HouseSurface.Anywhere,
                    SnapGrid = 0f,
                    SnapDegrees = 0f
                }
            )
            .Add(
                Manor,
                new PlotDefinition {
                    DisplayName = "A manor",
                    Budget = 50,
                    Surfaces = HouseSurface.Floor | HouseSurface.Wall,
                    EnterTier = HouseTier.Visitor,
                    UseTier = HouseTier.Guest,
                    DecorateTier = HouseTier.Guest,
                    AdministerTier = HouseTier.Resident
                }
            )
            .Add(Chair, new FurnitureDefinition { DisplayName = "A chair", Cost = 2, Surfaces = HouseSurface.Floor })
            .Add(
                Painting,
                new FurnitureDefinition { DisplayName = "A painting", Cost = 1, Surfaces = HouseSurface.Wall }
            )
            .Add(
                Chandelier,
                new FurnitureDefinition {
                    DisplayName = "A chandelier",
                    Cost = 5,
                    Surfaces = HouseSurface.Ceiling,
                    MaximumPerPlot = 1
                }
            )
            .Add(
                Forge,
                new FurnitureDefinition {
                    DisplayName = "A forge",
                    Cost = 4,
                    Surfaces = HouseSurface.Floor,
                    Tag = "House.Has.Forge",
                    Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Level.Twenty" }]
                }
            )
            .Add(
                Fountain,
                new FurnitureDefinition { DisplayName = "A fountain", Cost = 8, Surfaces = HouseSurface.Outdoors }
            )
            .Build();
}

sealed class Levelled : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        value = 0f;

        return false;
    }
}

public class HousingTests {
    static readonly PlayerId Owner = new(1);
    static readonly PlayerId Resident = new(2);
    static readonly PlayerId Guest = new(3);
    static readonly PlayerId Stranger = new(4);

    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly HousingLibrary library;
    readonly HousePlot house;

    public HousingTests() {
        library = HousingLibrary.Compile(catalog);
        house = new(library, Plot(Content.Cottage), HouseOwner.Of(Owner));
    }

    Plot Plot(string address) => library.FindPlot(DefId.From(address))!;

    Furniture Piece(string address) => library.FindFurniture(DefId.From(address))!;

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    // ── The snap ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APositionIsPutOnTheGrid() {
        var snapped = house.Plot.Snap(new(1.2f, 0f, -0.4f));

        Assert.Equal(1.0f, snapped.X, 4);
        Assert.Equal(-0.5f, snapped.Z, 4);
    }

    [Fact]
    public void APlotWithNoGridLeavesAPositionAlone() {
        var free = Plot(Content.Hall);
        var position = new Vector3(1.2345f, 0f, -0.4321f);

        Assert.Equal(position, free.Snap(position));
    }

    [Fact]
    public void AFacingIsPutOnTheAllowedIncrements() {
        // 20° rounds to 15°, not to 30°.
        Assert.Equal(MathUtil.DegreesToRadians(15f), house.Plot.SnapYaw(MathUtil.DegreesToRadians(20f)), 4);
        Assert.Equal(MathUtil.DegreesToRadians(30f), house.Plot.SnapYaw(MathUtil.DegreesToRadians(24f)), 4);
    }

    [Fact]
    public void APieceIsStoredSnappedRatherThanWhereThePlayerPointed() {
        // ⚠ The rule. A plot that validated the raw point and stored the rounded one is a house whose
        // furniture drifts every time somebody logs in.
        Assert.Equal(
            HousingRefusal.None,
            house.Place(
                Owner,
                Piece(Content.Chair),
                HouseSurface.Floor,
                new(1.2f, 0f, 3.4f),
                MathUtil.DegreesToRadians(20f),
                out var placed
            )
        );

        Assert.Equal(1.0f, placed.Position.X, 4);
        Assert.Equal(3.5f, placed.Position.Z, 4);
        Assert.Equal(MathUtil.DegreesToRadians(15f), placed.Yaw, 4);
        Assert.Equal(placed, house.Find(placed.Id));
    }

    // ── The budget and the surfaces ───────────────────────────────────────────────────────────

    [Fact]
    public void PlacingSpendsTheBudget() {
        Place(Content.Chair);

        Assert.Equal(2, house.Spent);
        Assert.Equal(18, house.Free);
    }

    [Fact]
    public void APieceThatDoesNotFitIsRefused() {
        for (var i = 0; i < 10; i++) {
            Assert.Equal(HousingRefusal.None, Place(Content.Chair));
        }

        Assert.Equal(0, house.Free);
        Assert.Equal(HousingRefusal.OutOfBudget, Place(Content.Chair));
    }

    [Fact]
    public void APieceCappedPerPlotIsRefusedTheSecondTime() {
        Assert.Equal(HousingRefusal.None, Place(Content.Chandelier, HouseSurface.Ceiling));
        Assert.Equal(HousingRefusal.TooMany, Place(Content.Chandelier, HouseSurface.Ceiling));
    }

    [Fact]
    public void APieceOnASurfaceItDoesNotGoOnIsRefused() =>
        Assert.Equal(HousingRefusal.WrongSurface, Place(Content.Chair, HouseSurface.Wall));

    [Fact]
    public void APieceOnASurfaceThePlotDoesNotHaveIsRefused() =>
        // The cottage has no outdoors, so a fountain has nowhere to go in it.
        Assert.Equal(HousingRefusal.WrongSurface, Place(Content.Fountain, HouseSurface.Outdoors));

    [Fact]
    public void TwoSurfacesAtOnceIsRefused() =>
        // ⚠ A caller that passes two is guessing, and a guess here is a chandelier on the lawn.
        Assert.Equal(
            HousingRefusal.WrongSurface,
            Place(Content.Chair, HouseSurface.Floor | HouseSurface.Wall)
        );

    [Fact]
    public void NoSurfaceAtAllIsRefused() =>
        Assert.Equal(HousingRefusal.WrongSurface, Place(Content.Chair, HouseSurface.Nothing));

    [Fact]
    public void APieceWhoseRequirementIsUnmetIsRefused() {
        var context = new Levelled();

        Assert.Equal(
            HousingRefusal.Requirements,
            house.Place(Owner, Piece(Content.Forge), HouseSurface.Floor, Vector3.Zero, 0f, out _, context)
        );

        context.Tags.Add(Tag("Level.Twenty"));

        Assert.Equal(
            HousingRefusal.None,
            house.Place(Owner, Piece(Content.Forge), HouseSurface.Floor, Vector3.Zero, 0f, out _, context)
        );
    }

    // ── Standing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOwnerIsTheOwnerAndAStrangerIsWhateverTheHouseIsOpenTo() {
        Assert.Equal(HouseTier.Owner, house.TierOf(Owner));
        Assert.Equal(HouseTier.None, house.TierOf(Stranger));

        Assert.Equal(HousingRefusal.None, house.SetOpenness(Owner, HouseTier.Guest));
        Assert.Equal(HouseTier.Guest, house.TierOf(Stranger));
        Assert.True(house.Can(Stranger, HouseAction.Use));
        Assert.False(house.Can(Stranger, HouseAction.Decorate));
    }

    [Fact]
    public void ABanBeatsAnOpenHouse() {
        // ⚠ The headline rule. A ban modelled as the bottom rung does nothing here, because on an
        // open house everybody is on the bottom rung and the bottom rung is admitted.
        Assert.Equal(HousingRefusal.None, house.SetOpenness(Owner, HouseTier.Guest));
        Assert.True(house.Can(Stranger, HouseAction.Enter));

        Assert.Equal(HousingRefusal.None, house.Ban(Owner, Stranger));

        Assert.False(house.Can(Stranger, HouseAction.Enter));
        Assert.True(house.IsBanned(Stranger));
    }

    [Fact]
    public void TheOwnerCannotBeBannedOrDemoted() {
        // Anybody who could would be locking somebody out of their own house for good.
        Assert.Equal(HousingRefusal.Forbidden, house.Ban(Owner, Owner));
        Assert.Equal(HousingRefusal.Forbidden, house.Grant(Owner, Owner, HouseTier.Visitor));
        Assert.Equal(HouseTier.Owner, house.TierOf(Owner));
    }

    [Fact]
    public void NobodyMayGrantStandingAtOrAboveTheirOwn() {
        Assert.Equal(HousingRefusal.None, house.Grant(Owner, Resident, HouseTier.Resident));

        // A resident cannot make somebody a resident, let alone an owner.
        Assert.Equal(HousingRefusal.Forbidden, house.Grant(Resident, Guest, HouseTier.Resident));
        Assert.Equal(HousingRefusal.Forbidden, house.Grant(Resident, Guest, HouseTier.Owner));
        Assert.Equal(HouseTier.None, house.TierOf(Guest));
    }

    [Fact]
    public void NobodyMayDemoteSomebodyWhoAlreadyMatchesThem() {
        // The manor lets a resident administer, which is where this rule is reachable: two residents
        // must not be able to demote each other.
        var manor = new HousePlot(library, Plot(Content.Manor), HouseOwner.Of(Owner));

        Assert.Equal(HousingRefusal.None, manor.Grant(Owner, Resident, HouseTier.Resident));
        Assert.Equal(HousingRefusal.None, manor.Grant(Owner, Guest, HouseTier.Resident));

        Assert.Equal(HousingRefusal.Forbidden, manor.Grant(Guest, Resident, HouseTier.None));
        Assert.Equal(HouseTier.Resident, manor.TierOf(Resident));
    }

    [Fact]
    public void OpennessCannotBeSetAboveTheSettersOwnStanding() {
        // Otherwise "open my house" is a way to hand the world more than you have yourself.
        var manor = new HousePlot(library, Plot(Content.Manor), HouseOwner.Of(Owner));

        Assert.Equal(HousingRefusal.None, manor.Grant(Owner, Resident, HouseTier.Resident));

        Assert.Equal(HousingRefusal.Forbidden, manor.SetOpenness(Resident, HouseTier.Owner));
        Assert.Equal(HousingRefusal.None, manor.SetOpenness(Resident, HouseTier.Guest));
        Assert.Equal(HouseTier.Guest, manor.TierOf(Stranger));
    }

    [Fact]
    public void ABanDropsStandingAndUnbanningDoesNotGiveItBack() {
        Assert.Equal(HousingRefusal.None, house.Grant(Owner, Guest, HouseTier.Guest));
        Assert.Equal(HousingRefusal.None, house.Ban(Owner, Guest));
        Assert.Equal(HouseTier.None, house.TierOf(Guest));

        Assert.Equal(HousingRefusal.None, house.Unban(Owner, Guest));
        Assert.False(house.IsBanned(Guest));
        Assert.Equal(HouseTier.None, house.TierOf(Guest));
    }

    [Fact]
    public void AStrangerMayNotDecorate() {
        Assert.Equal(HousingRefusal.Forbidden, Place(Content.Chair, HouseSurface.Floor, Stranger));

        Assert.Equal(HousingRefusal.None, house.Ban(Owner, Stranger));
        Assert.Equal(HousingRefusal.Banned, Place(Content.Chair, HouseSurface.Floor, Stranger));
    }

    // ── Moving, removing and clearing ─────────────────────────────────────────────────────────

    [Fact]
    public void AMoveKeepsTheIdAndCostsNothingEvenInAFullHouse() {
        // ⚠ Not a remove followed by a place: a plot furnished to the last point would otherwise be
        // one in which nothing can be nudged.
        Assert.Equal(HousingRefusal.None, Place(Content.Chair));

        var id = house.Placements[0].Id;

        for (var i = 0; i < 9; i++) {
            Assert.Equal(HousingRefusal.None, Place(Content.Chair));
        }

        Assert.Equal(0, house.Free);
        Assert.Equal(
            HousingRefusal.None,
            house.Move(Owner, id, HouseSurface.Floor, new(9f, 0f, 9f), 0f, out var moved)
        );

        Assert.Equal(id, moved.Id);
        Assert.Equal(20, house.Spent);
        Assert.Equal(9f, moved.Position.X, 4);
    }

    [Fact]
    public void AMoveOntoASurfaceThePieceDoesNotGoOnIsRefused() {
        Assert.Equal(HousingRefusal.None, Place(Content.Chair));

        Assert.Equal(
            HousingRefusal.WrongSurface,
            house.Move(Owner, house.Placements[0].Id, HouseSurface.Wall, Vector3.Zero, 0f, out _)
        );
    }

    [Fact]
    public void RemovingHandsThePieceBackAndRefundsTheBudget() {
        Assert.Equal(HousingRefusal.None, Place(Content.Chair));

        Assert.Equal(HousingRefusal.None, house.Remove(Owner, house.Placements[0].Id, out var returned));
        Assert.Equal(DefId.From(Content.Chair), returned);
        Assert.Equal(0, house.Spent);
        Assert.Empty(house.Placements);
    }

    [Fact]
    public void TwoForgesHoldTheTagTwiceAndTheFirstRemovalDoesNotDropIt() {
        // ⚠ A house with two forges must not lose its forge tag while one is still standing there —
        // and the counting is GameplayTagSet's, not this library's. The hand-written version of the
        // rule ("revoke only on the last one") revokes once for two grants and leaks the tag; this
        // test is what found that.
        var tags = new GameplayTagSet();
        var context = new Levelled();

        context.Tags.Add(Tag("Level.Twenty"));

        Assert.Equal(
            HousingRefusal.None,
            house.Place(Owner, Piece(Content.Forge), HouseSurface.Floor, Vector3.Zero, 0f, out var first, context, tags)
        );

        Assert.Equal(
            HousingRefusal.None,
            house.Place(Owner, Piece(Content.Forge), HouseSurface.Floor, Vector3.One, 0f, out var second, context, tags)
        );

        Assert.Equal(2, tags.CountOf(Tag("House.Has.Forge")));

        Assert.Equal(HousingRefusal.None, house.Remove(Owner, first.Id, out _, tags));
        Assert.True(tags.Contains(Tag("House.Has.Forge")));

        Assert.Equal(HousingRefusal.None, house.Remove(Owner, second.Id, out _, tags));
        Assert.False(tags.Contains(Tag("House.Has.Forge")));
        Assert.Equal(0, tags.CountOf(Tag("House.Has.Forge")));
    }

    [Fact]
    public void ClearingHandsEverythingBack() {
        Place(Content.Chair);
        Place(Content.Chair);
        Place(Content.Painting, HouseSurface.Wall);

        var returned = new List<DefId>();

        Assert.Equal(HousingRefusal.None, house.Clear(Owner, returned));
        Assert.Equal(3, returned.Count);
        Assert.Equal(0, house.Spent);
        Assert.Empty(house.Placements);
    }

    [Fact]
    public void APlacementIdIsNeverReused() {
        Place(Content.Chair);

        var first = house.Placements[0].Id;

        house.Remove(Owner, first, out _);
        Place(Content.Chair);

        Assert.NotEqual(first, house.Placements[0].Id);
    }

    // ── Loading a save ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASavedLayoutLoadsEvenWhenAPatchHasMadeItTooBig() {
        // ⚠ A layout that was legal when it was made must load, or a content change silently deletes
        // people's houses. Free goes negative and says so; reconciling is the caller's decision.
        var chair = DefId.From(Content.Chair);

        house.Restore([
            new(1, chair, HouseSurface.Floor, Vector3.Zero, 0f, Owner),
            new(2, chair, HouseSurface.Floor, Vector3.One, 0f, Owner),
            new(3, chair, HouseSurface.Floor, Vector3.Zero, 0f, Owner),
            new(4, chair, HouseSurface.Floor, Vector3.One, 0f, Owner),
            new(5, chair, HouseSurface.Floor, Vector3.Zero, 0f, Owner),
            new(6, chair, HouseSurface.Floor, Vector3.One, 0f, Owner),
            new(7, chair, HouseSurface.Floor, Vector3.Zero, 0f, Owner),
            new(8, chair, HouseSurface.Floor, Vector3.One, 0f, Owner),
            new(9, chair, HouseSurface.Floor, Vector3.Zero, 0f, Owner),
            new(10, chair, HouseSurface.Floor, Vector3.One, 0f, Owner),
            new(11, chair, HouseSurface.Floor, Vector3.Zero, 0f, Owner)
        ]);

        Assert.Equal(22, house.Spent);
        Assert.Equal(-2, house.Free);
        Assert.Equal(HousingRefusal.OutOfBudget, Place(Content.Chair));
    }

    [Fact]
    public void ARestoredLayoutDoesNotHandOutAnIdItAlreadyUses() {
        house.Restore([new(7, DefId.From(Content.Chair), HouseSurface.Floor, Vector3.Zero, 0f, Owner)]);

        Assert.Equal(HousingRefusal.None, Place(Content.Chair));
        Assert.Equal(8, house.Placements[1].Id);
    }

    [Fact]
    public void TheTagsAPlotGrantsComeFromWhatIsDown() {
        var tags = new GameplayTagSet();
        var context = new Levelled();

        context.Tags.Add(Tag("Level.Twenty"));
        house.Place(Owner, Piece(Content.Forge), HouseSurface.Floor, Vector3.Zero, 0f, out _, context);

        Assert.Equal(1, house.CollectTags(tags));
        Assert.True(tags.Contains(Tag("House.Has.Forge")));
    }

    // ── The revision ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARefusalDoesNotCountAsAChange() {
        var before = house.Revision;

        Assert.Equal(HousingRefusal.Forbidden, Place(Content.Chair, HouseSurface.Floor, Stranger));
        Assert.Equal(before, house.Revision);

        Assert.Equal(HousingRefusal.None, Place(Content.Chair));
        Assert.Equal(before + 1u, house.Revision);
    }

    // ── Guild housing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AGuildPlotHasNoImplicitOwnerAndWorksFromItsGrants() {
        // Doc 28: "the same thing with an IGuildGrain owner and a permission matrix instead of a
        // single owner". Mapping a guild rank onto a house tier is the guild's, applied at the edge.
        var hall = new HousePlot(library, Plot(Content.Hall), HouseOwner.OfGuild(Guid.NewGuid()));

        Assert.Equal(HouseTier.None, hall.TierOf(Owner));
        Assert.Equal(HousingRefusal.Forbidden, hall.Grant(Owner, Resident, HouseTier.Resident));

        // PlayerId.None is the world, and the world administers a guild hall on the guild's word.
        Assert.Equal(HousingRefusal.Forbidden, hall.Grant(PlayerId.None, Resident, HouseTier.Resident));
    }

    [Fact]
    public void AGuildsRankMatrixIsWhatSeatsStandingInItsHall() {
        // ⚠ Grant cannot bootstrap a guild hall — nobody outranks anybody there — so what lands
        // standing is Assign, which is the authority's rather than a player's. Doc 28's "permission
        // matrix instead of a single owner" is applied by whoever holds the guild and arrives here
        // already resolved.
        var hall = new HousePlot(library, Plot(Content.Hall), HouseOwner.OfGuild(Guid.NewGuid()));

        hall.Assign(Owner, HouseTier.Owner);
        hall.Assign(Resident, HouseTier.Resident);

        Assert.True(hall.Can(Owner, HouseAction.Administer));
        Assert.True(hall.Can(Resident, HouseAction.Decorate));
        Assert.False(hall.Can(Resident, HouseAction.Administer));

        // And once somebody is seated, the ordinary rules work: an officer may promote a member.
        Assert.Equal(HousingRefusal.None, hall.Grant(Owner, Guest, HouseTier.Guest));
        Assert.Equal(HouseTier.Guest, hall.TierOf(Guest));

        // Nobody is the guild hall's owner, so the demotion guard that protects a player's house does
        // not fire — an officer demoted by the matrix is an ordinary change.
        Assert.Equal(HousingRefusal.None, hall.Grant(Owner, Resident, HouseTier.Visitor));
        Assert.Equal(HouseTier.Visitor, hall.TierOf(Resident));
    }

    // ── Content problems ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUpsideDownLadderIsReported() {
        var library = HousingLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add(
                    "housing/backwards",
                    new PlotDefinition {
                        EnterTier = HouseTier.Resident,
                        UseTier = HouseTier.Guest,
                        DecorateTier = HouseTier.Guest,
                        AdministerTier = HouseTier.Owner
                    }
                )
                .Build()
        );

        Assert.Contains(library.Problems, problem => problem.Contains("from the doorstep", StringComparison.Ordinal));
    }

    [Fact]
    public void AHouseOpenEnoughForStrangersToRedecorateIsReported() {
        var library = HousingLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add("housing/free-for-all", new PlotDefinition { Openness = HouseTier.Resident })
                .Build()
        );

        Assert.Contains(library.Problems, problem => problem.Contains("anybody may", StringComparison.Ordinal));
    }

    [Fact]
    public void APieceNoPlotCanHoldIsReported() {
        var library = HousingLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add("housing/indoors", new PlotDefinition { Surfaces = HouseSurface.Floor })
                .Add("furniture/statue", new FurnitureDefinition { Surfaces = HouseSurface.Outdoors })
                .Build()
        );

        Assert.Contains(
            library.Problems,
            problem => problem.Contains("can never be placed anywhere", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void APlotWithNoBudgetAndAPieceOnNoSurfaceAreReported() {
        var library = HousingLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add("housing/void", new PlotDefinition { Budget = 0, Surfaces = HouseSurface.Nothing })
                .Add("furniture/ghost", new FurnitureDefinition { Surfaces = HouseSurface.Nothing })
                .Build()
        );

        Assert.Contains(library.Problems, problem => problem.Contains("nothing fits in it", StringComparison.Ordinal));
        Assert.Contains(library.Problems, problem => problem.Contains("no surfaces", StringComparison.Ordinal));
        Assert.Contains(library.Problems, problem => problem.Contains("never be placed", StringComparison.Ordinal));
    }

    // ── The oracle ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRunningTotalNeverDisagreesWithWhatIsDown() {
        // The housing version of the inventory library's conservation oracle: whatever sequence of
        // placements, moves and removals happens, Spent is what the layout actually costs.
        var random = GameplayRandom.For(0x480051Eul, 12);
        var ids = new List<int>();
        var seen = new HashSet<int>();
        var pieces = new[] {
            (Content.Chair, HouseSurface.Floor),
            (Content.Painting, HouseSurface.Wall),
            (Content.Chandelier, HouseSurface.Ceiling)
        };

        var placed = 0;
        var removed = 0;

        for (var step = 0; step < 4000; step++) {
            switch (random.NextInt(0, 10)) {
                case < 5: {
                    var (address, surface) = pieces[random.NextInt(0, pieces.Length)];

                    if (house.Place(
                            Owner,
                            Piece(address),
                            surface,
                            new(random.NextFloat() * 10f, 0f, random.NextFloat() * 10f),
                            random.NextFloat() * MathUtil.TwoPi,
                            out var made
                        ) == HousingRefusal.None) {
                        Assert.True(seen.Add(made.Id), "a placement id was handed out twice");
                        ids.Add(made.Id);
                        placed++;
                    }

                    break;
                }

                case < 8 when ids.Count > 0: {
                    var index = random.NextInt(0, ids.Count);
                    var placement = house.Find(ids[index])!.Value;

                    house.Move(
                        Owner,
                        placement.Id,
                        placement.Surface,
                        new(random.NextFloat() * 10f, 0f, random.NextFloat() * 10f),
                        random.NextFloat() * MathUtil.TwoPi,
                        out _
                    );

                    break;
                }

                case < 10 when ids.Count > 0: {
                    var index = random.NextInt(0, ids.Count);

                    Assert.Equal(HousingRefusal.None, house.Remove(Owner, ids[index], out _));
                    ids.RemoveAt(index);
                    removed++;

                    break;
                }
            }

            Assert.Equal(house.Recount(), house.Spent);
            Assert.True(house.Spent <= house.Plot.Budget, "the budget was overspent");
            Assert.Equal(ids.Count, house.Placements.Count);
        }

        // If the run never filled the house or never emptied a slot, the oracle proved nothing.
        Assert.True(placed > 100, $"only {placed} placements landed");
        Assert.True(removed > 100, $"only {removed} removals happened");
    }

    GameplayTag Tag(string name) => catalog.Tags.Require(name);

    HousingRefusal Place(string address, HouseSurface surface = HouseSurface.Floor, PlayerId? by = null) =>
        house.Place(by ?? Owner, Piece(address), surface, Vector3.Zero, 0f, out _);
}
