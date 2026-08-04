// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;
using Xunit;

namespace Vixen.Gameplay.Economy.Tests;

/// <summary>Two currencies that convert, two items, and a vendor that sells one of them.</summary>
public static class Content {
    public const string Gold = "currency/gold";
    public const string Silver = "currency/silver";
    public const string Sword = "items/sword";
    public const string Potion = "items/potion";
    public const string Smith = "vendors/smith";

    public static PlayerId Player(ulong who) => new(who);

    public static DefinitionCatalog Catalog() =>
        new DefinitionCatalogBuilder()
            .AddTag("Currency.Gold")
            .Add(Gold, new CurrencyDefinition { DisplayName = "Gold", Tag = "Currency.Gold", Cap = 1000 })
            .Add(
                Silver,
                new CurrencyDefinition {
                    DisplayName = "Silver",
                    DecayPerDay = 0.5f,
                    Conversions = [new() { To = Gold, Rate = 100 }]
                }
            )
            .Add(Sword, new ItemDefinition { DisplayName = "Sword" })
            .Add(Potion, new ItemDefinition { DisplayName = "Potion", MaximumStack = 20 })
            .Add(
                Smith,
                new VendorDefinition {
                    DisplayName = "Smith",
                    BuybackSlots = 2,
                    Stock = [
                        new() { Item = Potion, Currency = Gold, Price = 5 },
                        new() { Item = Sword, Currency = Gold, Price = 100, Quantity = 2, RestockSeconds = 60f },
                        new() {
                            Item = Sword,
                            Currency = Gold,
                            Price = 50,
                            Requires = [new() { Kind = RequirementKind.HasTag, Subject = "Currency.Gold" }]
                        }
                    ]
                }
            )
            .Build();
}

/// <summary>Somebody with a tag, for the vendor row that asks for one.</summary>
sealed class Holder : IRequirementContext {
    public GameplayTagSet Tags { get; } = new();

    GameplayTagSet? IRequirementContext.Tags => Tags;

    public bool TryGetValue(AttributeId subject, out float value) {
        value = 0f;

        return false;
    }
}

public class LedgerTests {
    readonly MemoryEconomyLedger ledger = new();
    readonly DefId gold = DefId.From(Content.Gold);
    readonly DefId sword = DefId.From(Content.Sword);

    static EconomyAccount Purse(ulong who) => EconomyAccount.Of(Content.Player(who));

    static EconomyAccount World => EconomyAccount.Of(EconomyAccount.Vendor);

    void Mint(ulong who, DefId asset, long amount, string key) =>
        ledger.Post(EconomyIntent.Transfer(key, World, Purse(who), asset, amount));

    [Fact]
    public void AnUnbalancedIntentIsRefused() {
        var result = ledger.Post(new("k", [new(Purse(1), gold, 100)]));

        Assert.Equal(EconomyVerdict.Unbalanced, result.Verdict);
        Assert.Equal(0, ledger.Balance(Purse(1), gold));
    }

    [Fact]
    public void BalanceIsPerAssetRatherThanOverall() {
        // ⚠ Summing every delta together would let gold leaving one account be paid for by ore
        // arriving in another, which is a duplication bug with a receipt.
        var result = ledger.Post(new("k", [new(Purse(1), gold, -100), new(Purse(2), sword, 100)]));

        Assert.Equal(EconomyVerdict.Unbalanced, result.Verdict);
    }

    [Fact]
    public void AnIntentWithNoKeyOrNoMovementsIsMalformed() {
        Assert.Equal(EconomyVerdict.Malformed, ledger.Post(new("", [new(Purse(1), gold, 0)])).Verdict);
        Assert.Equal(EconomyVerdict.Malformed, ledger.Post(new("k", [])).Verdict);
    }

    [Fact]
    public void AnAccountThatIsBothOrNeitherIsMalformed() {
        var both = new EconomyAccount(Content.Player(1), "world/vendor");

        Assert.Equal(EconomyVerdict.Malformed, ledger.Post(new("k", [new(both, gold, 0)])).Verdict);
        Assert.Equal(EconomyVerdict.Malformed, ledger.Post(new("k2", [new(EconomyAccount.Nowhere, gold, 0)])).Verdict);
    }

    [Fact]
    public void AReplayedKeyChangesNothing() {
        Mint(1, gold, 100, "mint");

        var again = ledger.Post(EconomyIntent.Transfer("mint", World, Purse(1), gold, 100));

        Assert.Equal(EconomyVerdict.Replayed, again.Verdict);
        Assert.True(again.Ok);
        Assert.Equal(100, ledger.Balance(Purse(1), gold));
        Assert.Equal(1, ledger.Applied);
    }

    [Fact]
    public void APlayerMayNotGoNegativeAndAWorldAccountMay() {
        // The asymmetry is what makes a world account a source and a sink.
        Assert.Equal(
            EconomyVerdict.Insufficient,
            ledger.Post(EconomyIntent.Transfer("k", Purse(1), World, gold, 1)).Verdict
        );

        Mint(1, gold, 5, "mint");

        Assert.True(ledger.Post(EconomyIntent.Transfer("k2", Purse(1), World, gold, 5)).Ok);
        Assert.Equal(0, ledger.Balance(Purse(1), gold));
        Assert.Equal(0, ledger.Total(gold));
    }

    [Fact]
    public void AnIntentIsAllOrNothing() {
        // ⚠ The third movement overdraws, so the first two must not land.
        Mint(1, gold, 10, "mint");

        var result = ledger.Post(
            new(
                "k",
                [
                    new(Purse(1), gold, -10),
                    new(Purse(2), gold, 10),
                    new(Purse(1), sword, -1),
                    new(Purse(2), sword, 1)
                ]
            )
        );

        Assert.Equal(EconomyVerdict.Insufficient, result.Verdict);
        Assert.Equal(10, ledger.Balance(Purse(1), gold));
        Assert.Equal(0, ledger.Balance(Purse(2), gold));
    }

    [Fact]
    public void ConservationHoldsAcrossRandomisedTransfers() {
        var random = new GameplayRandom(0xC0Aul);

        for (ulong who = 1; who <= 6; who++) {
            Mint(who, gold, 1000, $"mint/{who}");
            Mint(who, sword, 10, $"mints/{who}");
        }

        var gold0 = ledger.Total(gold);
        var sword0 = ledger.Total(sword);
        var applied = 0;
        var refused = 0;

        for (var step = 0; step < 4000; step++) {
            var from = Purse((ulong)random.NextInt(1, 7));
            var to = Purse((ulong)random.NextInt(1, 7));
            var asset = random.NextInt(2) == 0 ? gold : sword;
            var amount = random.NextInt(1, 400);

            var result = ledger.Post(EconomyIntent.Transfer($"t/{step}", from, to, asset, amount));

            if (result.Verdict == EconomyVerdict.Applied) {
                applied++;
            } else {
                refused++;
            }

            Assert.Equal(gold0, ledger.Total(gold));
            Assert.Equal(sword0, ledger.Total(sword));
        }

        Assert.True(applied > 500, $"only {applied} applied");
        Assert.True(refused > 100, $"only {refused} refused");
    }
}

public class CurrencyTests {
    readonly EconomyLibrary library = EconomyLibrary.Compile(Content.Catalog());

    Currency Gold => library.FindCurrency(DefId.From(Content.Gold))!;

    Currency Silver => library.FindCurrency(DefId.From(Content.Silver))!;

    [Fact]
    public void TheContentCompilesWithNoProblems() => Assert.Empty(library.Problems);

    [Fact]
    public void ACapReportsItsOverflowRatherThanDroppingIt() {
        // ⚠ Only the caller knows whether to mail it, refuse the reward or convert it.
        var (fits, overflow) = Gold.Fit(900, 250);

        Assert.Equal(100, fits);
        Assert.Equal(150, overflow);
    }

    [Fact]
    public void NoCapMeansEverythingFits() {
        var (fits, overflow) = Silver.Fit(1_000_000, 500);

        Assert.Equal(500, fits);
        Assert.Equal(0, overflow);
    }

    [Fact]
    public void ConversionIsIntegerAndTheRemainderStays() {
        var exchange = Silver.Convert(250, Gold.Id);

        Assert.Equal(200, exchange.Converted);
        Assert.Equal(2, exchange.Produced);
        Assert.Equal(50, exchange.Remainder);
    }

    [Fact]
    public void ThereIsNoConversionToSomethingItDoesNotConvertTo() {
        Assert.Equal(default, Gold.Convert(500, Silver.Id));
        Assert.Equal(default, Silver.Convert(0, Gold.Id));
    }

    [Fact]
    public void DecayRoundsDownSoItCanReachZero() {
        // ⚠ Rounding to nearest leaves everybody with one coin for ever, and that is not a sink.
        Assert.Equal(50, Silver.Decay(100, 1f));
        Assert.Equal(25, Silver.Decay(100, 2f));
        Assert.Equal(0, Silver.Decay(1, 1f));
        Assert.Equal(100, Gold.Decay(100, 10f));
    }
}

public class VendorTests {
    readonly DefinitionCatalog catalog = Content.Catalog();
    readonly EconomyLibrary library;
    readonly MemoryEconomyLedger ledger = new();
    readonly Holder buyer = new();

    public VendorTests() {
        library = EconomyLibrary.Compile(catalog);
        ledger.Post(
            EconomyIntent.Transfer(
                "mint",
                EconomyAccount.Of(EconomyAccount.Vendor),
                EconomyAccount.Of(Content.Player(1)),
                DefId.From(Content.Gold),
                500
            )
        );
    }

    VendorState State() => new(library.FindVendor(DefId.From(Content.Smith))!);

    long Gold(ulong who) => ledger.Balance(EconomyAccount.Of(Content.Player(who)), DefId.From(Content.Gold));

    long Held(ulong who, string item) =>
        ledger.Balance(EconomyAccount.Of(Content.Player(who)), DefId.From(item));

    [Fact]
    public void BuyingTakesTheStockAndThePrice() {
        var state = State();

        Assert.Equal(VendorRefusal.None, state.Buy(Content.Player(1), 1, 2, ledger, buyer, 0f, "a"));
        Assert.Equal(300, Gold(1));
        Assert.Equal(2, Held(1, Content.Sword));
        Assert.Equal(0, state.Remaining(1));
    }

    [Fact]
    public void AnUnlimitedRowNeverRunsOut() {
        var state = State();

        for (var attempt = 0; attempt < 20; attempt++) {
            Assert.Equal(VendorRefusal.None, state.Buy(Content.Player(1), 0, 5, ledger, buyer, 0f, $"a{attempt}"));
        }

        Assert.Equal(-1, state.Remaining(0));
        Assert.Equal(0, Gold(1));
    }

    [Fact]
    public void AnEmptyRowComesBackOnItsTimer() {
        var state = State();

        state.Buy(Content.Player(1), 1, 2, ledger, buyer, 10f, "a");

        Assert.Equal(0, state.Remaining(1));
        Assert.Equal(0, state.Restock(50f));
        Assert.Equal(1, state.Restock(70f));
        Assert.Equal(2, state.Remaining(1));
    }

    [Fact]
    public void BuyingMoreThanIsLeftIsRefused() {
        var state = State();

        Assert.Equal(VendorRefusal.OutOfStock, state.Buy(Content.Player(1), 1, 3, ledger, buyer, 0f, "a"));
        Assert.Equal(VendorRefusal.BadCount, state.Buy(Content.Player(1), 1, 0, ledger, buyer, 0f, "b"));
        Assert.Equal(VendorRefusal.UnknownStock, state.Buy(Content.Player(1), 9, 1, ledger, buyer, 0f, "c"));
    }

    [Fact]
    public void ARequirementOnARowIsChecked() {
        var state = State();

        Assert.Equal(VendorRefusal.Requirements, state.Buy(Content.Player(1), 2, 1, ledger, buyer, 0f, "a"));

        buyer.Tags.Add(catalog.Tags.Resolve("Currency.Gold"));

        Assert.Equal(VendorRefusal.None, state.Buy(Content.Player(1), 2, 1, ledger, buyer, 0f, "b"));
    }

    [Fact]
    public void TheStockIsOnlyTakenWhenTheLedgerSaysYes() {
        // ⚠ Taking it first and putting it back on refusal has a window, and the window is where a
        // limited-stock item goes missing without anybody getting it.
        var state = State();

        Assert.Equal(VendorRefusal.Insufficient, state.Buy(Content.Player(2), 1, 1, ledger, buyer, 0f, "a"));
        Assert.Equal(2, state.Remaining(1));
    }

    [Fact]
    public void ARetriedPurchaseDoesNotTakeTheStockTwice() {
        var state = State();

        Assert.Equal(VendorRefusal.None, state.Buy(Content.Player(1), 1, 1, ledger, buyer, 0f, "once"));
        Assert.Equal(VendorRefusal.None, state.Buy(Content.Player(1), 1, 1, ledger, buyer, 0f, "once"));

        Assert.Equal(1, state.Remaining(1));
        Assert.Equal(400, Gold(1));
        Assert.Equal(1, Held(1, Content.Sword));
    }

    [Fact]
    public void SellingRecordsBuybackAndTheOldestFallsOff() {
        var state = State();

        state.Buy(Content.Player(1), 0, 3, ledger, buyer, 0f, "buy");

        for (var index = 0; index < 3; index++) {
            Assert.Equal(
                VendorRefusal.None,
                state.Sell(Content.Player(1), DefId.From(Content.Potion), 1, DefId.From(Content.Gold), 2, ledger, $"s{index}")
            );
        }

        // Two slots, newest first.
        Assert.Equal(2, state.BuybackFor(Content.Player(1)).Count);
    }

    [Fact]
    public void BuybackCostsExactlyWhatWasPaid() {
        // ⚠ Buyback exists because somebody sold something by accident; a spread on the undo makes
        // the price of a mis-click depend on how fast it was noticed.
        var state = State();

        state.Buy(Content.Player(1), 0, 1, ledger, buyer, 0f, "buy");

        var before = Gold(1);

        state.Sell(Content.Player(1), DefId.From(Content.Potion), 1, DefId.From(Content.Gold), 2, ledger, "s");

        Assert.Equal(before + 2, Gold(1));
        Assert.Equal(VendorRefusal.None, state.BuyBack(Content.Player(1), 0, ledger, "b"));
        Assert.Equal(before, Gold(1));
        Assert.Equal(1, Held(1, Content.Potion));
        Assert.Empty(state.BuybackFor(Content.Player(1)));
    }

    [Fact]
    public void ThereIsNothingToBuyBackFromAnEmptyWindow() {
        var state = State();

        Assert.Equal(VendorRefusal.NothingToBuyBack, state.BuyBack(Content.Player(1), 0, ledger, "b"));
    }
}

public class TradeTests {
    readonly MemoryEconomyLedger ledger = new();
    readonly DefId gold = DefId.From(Content.Gold);
    readonly DefId sword = DefId.From(Content.Sword);
    readonly DefId potion = DefId.From(Content.Potion);

    public TradeTests() {
        ledger.Post(
            new(
                "mint",
                [
                    new(EconomyAccount.Of(EconomyAccount.Vendor), gold, -1000),
                    new(EconomyAccount.Of(Content.Player(1)), gold, 1000),
                    new(EconomyAccount.Of(EconomyAccount.Vendor), sword, -5),
                    new(EconomyAccount.Of(Content.Player(2)), sword, 5),
                    new(EconomyAccount.Of(EconomyAccount.Vendor), gold, -1_000_000),
                    new(EconomyAccount.Of(Content.Player(3)), gold, 1_000_000),
                    new(EconomyAccount.Of(EconomyAccount.Vendor), sword, -1_000_000),
                    new(EconomyAccount.Of(Content.Player(3)), sword, 1_000_000),
                    new(EconomyAccount.Of(EconomyAccount.Vendor), gold, -1_000_000),
                    new(EconomyAccount.Of(Content.Player(4)), gold, 1_000_000),
                    new(EconomyAccount.Of(EconomyAccount.Vendor), sword, -1_000_000),
                    new(EconomyAccount.Of(Content.Player(4)), sword, 1_000_000),
                    new(EconomyAccount.Of(EconomyAccount.Vendor), potion, -5),
                    new(EconomyAccount.Of(Content.Player(2)), potion, 5)
                ]
            )
        );
    }

    static TradeSession Session() => new(Content.Player(1), Content.Player(2), "t1");

    long Held(ulong who, DefId asset) => ledger.Balance(EconomyAccount.Of(Content.Player(who)), asset);

    [Fact]
    public void AChangeClearsBothConfirmations() {
        var trade = Session();

        trade.Offer(Content.Player(1), gold, 100);
        trade.Offer(Content.Player(2), sword, 1);

        Assert.Equal(TradeRefusal.None, trade.Confirm(Content.Player(1), trade.Revision));
        Assert.Equal(TradeRefusal.None, trade.Confirm(Content.Player(2), trade.Revision));
        Assert.Equal(TradeStatus.Locked, trade.Status);

        trade.Offer(Content.Player(2), sword, 0);

        Assert.Equal(TradeStatus.Open, trade.Status);
        Assert.False(trade.HasConfirmed(Content.Player(1)));
        Assert.False(trade.HasConfirmed(Content.Player(2)));
    }

    [Fact]
    public void EvenAnOfferThatChangesNothingBumpsTheRevision() {
        // ⚠ Detecting a no-op would be a way to clear a confirmation and set it again with nothing
        // visibly moving.
        var trade = Session();

        trade.Offer(Content.Player(1), gold, 100);

        var revision = trade.Revision;

        trade.Offer(Content.Player(1), gold, 100);

        Assert.NotEqual(revision, trade.Revision);
    }

    [Fact]
    public void AConfirmationQuotingAStaleRevisionIsRefused() {
        // ⚠ The part doc 28 does not say: "any change re-opens both confirmations" loses the race
        // where a change and a confirmation cross in flight.
        var trade = Session();

        trade.Offer(Content.Player(1), gold, 100);

        var seen = trade.Revision;

        trade.Offer(Content.Player(1), gold, 1);

        Assert.Equal(TradeRefusal.Stale, trade.Confirm(Content.Player(2), seen));
        Assert.False(trade.HasConfirmed(Content.Player(2)));
    }

    [Fact]
    public void ASettledTradeMovesEverything() {
        var trade = Session();

        trade.Offer(Content.Player(1), gold, 100);
        trade.Offer(Content.Player(2), sword, 1);
        trade.Confirm(Content.Player(1), trade.Revision);
        trade.Confirm(Content.Player(2), trade.Revision);

        Assert.Equal(TradeRefusal.None, trade.Settle(ledger, out var result));
        Assert.Equal(EconomyVerdict.Applied, result.Verdict);
        Assert.Equal(TradeStatus.Completed, trade.Status);
        Assert.Equal(900, Held(1, gold));
        Assert.Equal(1, Held(1, sword));
        Assert.Equal(100, Held(2, gold));
        Assert.Equal(4, Held(2, sword));
    }

    [Fact]
    public void SettlingWhatNobodyConfirmedIsRefused() {
        var trade = Session();

        trade.Offer(Content.Player(1), gold, 100);

        Assert.Equal(TradeRefusal.NotConfirmed, trade.Settle(ledger, out _));
        Assert.Equal(1000, Held(1, gold));
    }

    [Fact]
    public void ALedgerRefusalCancelsRatherThanReopening() {
        // ⚠ The usual refusal is that somebody no longer holds what they offered, and reopening puts
        // both parties in front of a table that is now a lie.
        var trade = Session();

        trade.Offer(Content.Player(1), gold, 5000);
        trade.Offer(Content.Player(2), sword, 1);
        trade.Confirm(Content.Player(1), trade.Revision);
        trade.Confirm(Content.Player(2), trade.Revision);

        Assert.Equal(TradeRefusal.Insufficient, trade.Settle(ledger, out _));
        Assert.Equal(TradeStatus.Cancelled, trade.Status);
        Assert.Equal(1000, Held(1, gold));
        Assert.Equal(5, Held(2, sword));
    }

    [Fact]
    public void ARetriedSettlementReplaysAndMovesNothingTwice() {
        var trade = Session();

        trade.Offer(Content.Player(1), gold, 100);
        trade.Confirm(Content.Player(1), trade.Revision);
        trade.Confirm(Content.Player(2), trade.Revision);
        trade.Settle(ledger, out _);

        // A second Settle is refused because it is already completed; the ledger would replay anyway.
        Assert.Equal(TradeRefusal.NotOpen, trade.Settle(ledger, out _));
        Assert.Equal(900, Held(1, gold));

        // One for the mint in the constructor and one for the trade — the retry added nothing.
        Assert.Equal(2, ledger.Applied);
    }

    [Fact]
    public void SomebodyOutsideTheTradeMayDoNothing() {
        var trade = Session();

        Assert.Equal(TradeRefusal.NotAParty, trade.Offer(Content.Player(9), gold, 1));
        Assert.Equal(TradeRefusal.NotAParty, trade.Confirm(Content.Player(9), 0));
        Assert.Equal(TradeRefusal.NotAParty, trade.Cancel(Content.Player(9)));
        Assert.Null(trade.OfferOf(Content.Player(9)));
    }

    [Fact]
    public void TheLastMomentSwapCannotHappen() {
        // doc 28 § Testing: "the trade confirm-lock rejects every last-moment swap in a randomised
        // adversarial sequence". The property asserted is the strong form — a settlement's contents
        // are always exactly what both parties last confirmed, whatever order the operations arrive
        // in and however stale the confirmations are.
        var random = new GameplayRandom(0x5CABul);
        var settled = 0;
        var swapsAttempted = 0;

        for (var run = 0; run < 400; run++) {
            // Players three and four, who between them hold enough of both assets that a refusal is a
            // real refusal rather than a poor generator — the same lesson the group oracle taught.
            var trade = new TradeSession(Content.Player(3), Content.Player(4), $"t{run}");
            var snapshots = new Dictionary<int, string>();
            var seen = new Dictionary<PlayerId, int> {
                [Content.Player(3)] = -1,
                [Content.Player(4)] = -1
            };

            var view = new Dictionary<PlayerId, string> {
                [Content.Player(3)] = string.Empty,
                [Content.Player(4)] = string.Empty
            };

            var agreed = new Dictionary<PlayerId, string> {
                [Content.Player(3)] = "never",
                [Content.Player(4)] = "never"
            };

            for (var step = 0; step < 24 && trade.Status is TradeStatus.Open or TradeStatus.Locked; step++) {
                var actor = Content.Player((ulong)random.NextInt(3, 5));

                snapshots[trade.Revision] = Describe(trade);

                // Locked is the state the property is about, so that is where the steps go — and one
                // time in four the adversary swaps the goods there instead of settling, which is the
                // gesture the whole confirm-lock exists to defeat.
                switch (trade.Status == TradeStatus.Locked ? random.NextInt(4) == 0 ? 0 : 4 : random.NextInt(4)) {
                    case 0:
                    case 1:
                        // An offer, which is also the adversary's swap when it lands after a confirm.
                        if (trade.HasConfirmed(Content.Player(3)) || trade.HasConfirmed(Content.Player(4))) {
                            swapsAttempted++;
                        }

                        trade.Offer(actor, random.NextInt(2) == 0 ? gold : sword, random.NextInt(0, 4));

                        break;

                    case 2:
                        // A confirmation quoting whatever this client last *looked at* — two times in
                        // three it refreshes its view first, and the third time it confirms goods that
                        // may have moved since. Both the revision and the contents it saw are kept, so
                        // the settle assertion can be about what the player actually agreed to rather
                        // than about what is on the table now.
                        if (random.NextInt(3) != 0) {
                            seen[actor] = trade.Revision;
                            view[actor] = Describe(trade);
                        }

                        if (trade.Confirm(actor, seen[actor]) == TradeRefusal.None) {
                            agreed[actor] = view[actor];
                        }

                        break;

                    case 3:
                        trade.Unconfirm(actor);

                        break;

                    default:
                        if (trade.Status != TradeStatus.Locked) {
                            break;
                        }

                        snapshots[trade.Revision] = Describe(trade);

                        var contents = Describe(trade);
                        var revision = trade.Revision;

                        if (trade.Settle(ledger, out _) != TradeRefusal.None) {
                            break;
                        }

                        // ⚠ The strong form: what settled is what *each party agreed to*, not merely
                        // what happens to be on the table now. Asserting the latter is vacuous — both
                        // sides of it are read from the same live state — and it is the assertion a
                        // confirm-lock test most easily ends up making by accident.
                        Assert.Equal(revision, trade.Revision);
                        Assert.Equal(snapshots[revision], contents);
                        Assert.Equal(contents, agreed[Content.Player(3)]);
                        Assert.Equal(contents, agreed[Content.Player(4)]);
                        settled++;

                        break;
                }
            }
        }

        Assert.True(settled > 20, $"only {settled} trades settled");
        Assert.True(swapsAttempted > 50, $"only {swapsAttempted} swaps were attempted");

        static string Describe(TradeSession trade) =>
            string.Join(
                '|',
                trade.LeftOffer.Assets.Select(entry => $"L{entry.Asset.Value}:{entry.Amount}")
                    .Concat(trade.RightOffer.Assets.Select(entry => $"R{entry.Asset.Value}:{entry.Amount}"))
            );
    }
}

public class EconomyLibraryTests {
    [Fact]
    public void AVendorPricedInSomethingThatIsNotACurrencyIsAProblem() {
        var problems = EconomyLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(Content.Sword, new ItemDefinition())
                    .Add(
                        Content.Smith,
                        new VendorDefinition { Stock = [new() { Item = Content.Sword, Currency = "currency/nope" }] }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("currency/nope", StringComparison.Ordinal));
    }

    [Fact]
    public void AVendorSellingSomethingThatIsNotInTheBuildIsAProblem() {
        var problems = EconomyLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(Content.Gold, new CurrencyDefinition())
                    .Add(
                        Content.Smith,
                        new VendorDefinition { Stock = [new() { Item = "items/ghost", Currency = Content.Gold }] }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("items/ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void AConversionToAMissingCurrencyIsAProblem() {
        var problems = EconomyLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(Content.Silver, new CurrencyDefinition { Conversions = [new() { To = "currency/nope" }] })
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("currency/nope", StringComparison.Ordinal));
    }

    [Fact]
    public void AConversionForwardsToACurrencyDefinedLaterIsFine() {
        // Checked after every currency is read, because a designer writes silver → gold in whichever
        // file comes first alphabetically.
        var library = EconomyLibrary.Compile(
            new DefinitionCatalogBuilder()
                .Add(Content.Silver, new CurrencyDefinition { Conversions = [new() { To = Content.Gold }] })
                .Add(Content.Gold, new CurrencyDefinition())
                .Build()
        );

        Assert.Empty(library.Problems);
    }

    [Fact]
    public void AnUnlimitedRowWithARestockTimerIsAProblem() {
        var problems = EconomyLibrary.Compile(
                new DefinitionCatalogBuilder()
                    .Add(Content.Gold, new CurrencyDefinition())
                    .Add(Content.Sword, new ItemDefinition())
                    .Add(
                        Content.Smith,
                        new VendorDefinition {
                            Stock = [new() { Item = Content.Sword, Currency = Content.Gold, RestockSeconds = 60f }]
                        }
                    )
                    .Build()
            )
            .Problems;

        Assert.Contains(problems, problem => problem.Contains("restock", StringComparison.Ordinal));
    }
}
