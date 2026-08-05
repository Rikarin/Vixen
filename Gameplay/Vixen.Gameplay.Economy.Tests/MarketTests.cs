// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Gameplay.Economy.Tests;

/// <summary>A ledger with everybody rich, so a refusal in these tests is the rule under test.</summary>
public abstract class MarketFixture {
    protected static readonly DefId Gold = DefId.From(Content.Gold);
    protected static readonly DefId Sword = DefId.From(Content.Sword);
    protected static readonly DefId Potion = DefId.From(Content.Potion);

    protected MarketFixture() {
        var movements = new List<AssetMove>();

        for (ulong who = 1; who <= 4; who++) {
            foreach (var asset in (DefId[])[Gold, Sword, Potion]) {
                movements.Add(new(EconomyAccount.Of(EconomyAccount.Vendor), asset, -10_000));
                movements.Add(new(EconomyAccount.Of(Content.Player(who)), asset, 10_000));
            }
        }

        Ledger.Post(new("mint", movements));
    }

    protected MemoryEconomyLedger Ledger { get; } = new();

    protected long Held(ulong who, DefId asset) =>
        Ledger.Balance(EconomyAccount.Of(Content.Player(who)), asset);

    protected long InEscrow(DefId asset) => Ledger.Balance(EconomyAccount.Of(EconomyAccount.Escrow), asset);

    protected long InMail(DefId asset) => Ledger.Balance(EconomyAccount.Of(EconomyAccount.Mail), asset);

    protected long Destroyed(DefId asset) => Ledger.Balance(EconomyAccount.Of(EconomyAccount.Sink), asset);
}

public class MailTests : MarketFixture {
    readonly PostOffice post;

    public MailTests() => post = new(Ledger, capacity: 3);

    MailMessage Send(ulong from, ulong to, string operation, long cod = 0, params MailAttachment[] attachments) {
        var refusal = post.Send(
            Content.Player(from),
            Content.Player(to),
            "hello",
            attachments,
            0f,
            operation,
            out var mail,
            cod: cod,
            codCurrency: cod > 0 ? Gold : default
        );

        Assert.Equal(MailRefusal.None, refusal);

        return mail!;
    }

    [Fact]
    public void AnAttachmentLeavesTheSenderWhenTheLetterIsSent() {
        // ⚠ Otherwise a sender attaches a sword, posts it, sells the sword, and the recipient claims
        // a second one.
        var before = Held(1, Sword);

        Send(1, 2, "a", attachments: new MailAttachment(Sword, 3));

        Assert.Equal(before - 3, Held(1, Sword));
        Assert.Equal(3, InMail(Sword));

        // The recipient has what they started with and not one more, until they claim.
        Assert.Equal(10_000, Held(2, Sword));
    }

    [Fact]
    public void ClaimingTakesEverythingAtOnce() {
        var mail = Send(1, 2, "a", attachments: [new(Sword, 3), new(Potion, 5)]);

        Assert.Equal(MailRefusal.None, post.Claim(Content.Player(2), mail.Id, "c"));
        Assert.Equal(3, Held(2, Sword) - 10_000);
        Assert.Equal(5, Held(2, Potion) - 10_000);
        Assert.Equal(0, InMail(Sword));
        Assert.True(mail.IsClaimed);
    }

    [Fact]
    public void ClaimingIsAllOrNothingAndIncludesTheCashOnDelivery() {
        // ⚠ Taking the goods in one operation and paying in another is a scam with two halves.
        var mail = Send(1, 2, "a", cod: 250, attachments: new MailAttachment(Sword, 1));
        var sender = Held(1, Gold);
        var recipient = Held(2, Gold);

        Assert.Equal(MailRefusal.None, post.Claim(Content.Player(2), mail.Id, "c"));
        Assert.Equal(sender + 250, Held(1, Gold));
        Assert.Equal(recipient - 250, Held(2, Gold));
    }

    [Fact]
    public void ARecipientWhoCannotPayGetsNeitherTheGoodsNorTheBill() {
        var mail = Send(1, 3, "a", cod: 100_000, attachments: new MailAttachment(Sword, 1));

        Assert.Equal(MailRefusal.Insufficient, post.Claim(Content.Player(3), mail.Id, "c"));
        Assert.Equal(10_000, Held(3, Sword));
        Assert.Equal(1, InMail(Sword));
        Assert.False(mail.IsClaimed);
    }

    [Fact]
    public void ARetriedClaimTakesNothingTwice() {
        var mail = Send(1, 2, "a", attachments: new MailAttachment(Sword, 3));

        post.Claim(Content.Player(2), mail.Id, "c");

        Assert.Equal(MailRefusal.AlreadyClaimed, post.Claim(Content.Player(2), mail.Id, "c"));
        Assert.Equal(10_003, Held(2, Sword));
    }

    [Fact]
    public void SomebodyElsesLetterIsNotFound() {
        var mail = Send(1, 2, "a", attachments: new MailAttachment(Sword, 1));

        Assert.Equal(MailRefusal.NotFound, post.Claim(Content.Player(3), mail.Id, "c"));
    }

    [Fact]
    public void AFullMailboxRefusesTheNextLetter() {
        Send(1, 2, "a");
        Send(1, 2, "b");
        Send(1, 2, "c");

        Assert.Equal(
            MailRefusal.Full,
            post.Send(Content.Player(1), Content.Player(2), "no", [], 0f, "d", out _)
        );
    }

    [Fact]
    public void ALetterToNobodyOrToYourselfIsMalformed() {
        Assert.Equal(
            MailRefusal.Malformed,
            post.Send(Content.Player(1), Content.Player(1), "me", [], 0f, "a", out _)
        );

        Assert.Equal(
            MailRefusal.Malformed,
            post.Send(Content.Player(1), PlayerId.None, "nobody", [], 0f, "b", out _)
        );
    }

    [Fact]
    public void CashOnDeliveryWithNothingAttachedIsMalformed() =>
        Assert.Equal(
            MailRefusal.Malformed,
            post.Send(Content.Player(1), Content.Player(2), "pay me", [], 0f, "a", out _, cod: 100, codCurrency: Gold)
        );

    [Fact]
    public void AnUnclaimedAttachmentIsNotDeletable() {
        // ⚠ "Delete" is a button somebody presses to clear a full mailbox.
        var mail = Send(1, 2, "a", attachments: new MailAttachment(Sword, 1));

        Assert.Equal(MailRefusal.AlreadyClaimed, post.Delete(Content.Player(2), mail.Id));

        post.Claim(Content.Player(2), mail.Id, "c");

        Assert.Equal(MailRefusal.None, post.Delete(Content.Player(2), mail.Id));
    }

    [Fact]
    public void AnExpiredLetterGoesBackToItsSenderWithNoChargeOnIt() {
        // ⚠ Nobody took the goods, so nobody owes anything.
        var mail = Send(1, 2, "a", cod: 250, attachments: new MailAttachment(Sword, 2));

        Assert.Equal(1, post.Expire(mail.Expires + 1f));

        var back = Assert.Single(post.Of(Content.Player(1)));

        Assert.Equal(0, back.Cod);
        Assert.Equal(2, back.Attachments[0].Amount);

        // Still exactly two swords in the world's mail account — the return moved nothing.
        Assert.Equal(2, InMail(Sword));
        Assert.Empty(post.Of(Content.Player(2)));
    }

    [Fact]
    public void AnExpiredLetterThatWasAlreadyClaimedJustGoes() {
        var mail = Send(1, 2, "a", attachments: new MailAttachment(Sword, 1));

        post.Claim(Content.Player(2), mail.Id, "c");

        Assert.Equal(1, post.Expire(mail.Expires + 1f));
        Assert.Empty(post.Of(Content.Player(1)));
        Assert.Empty(post.Of(Content.Player(2)));
    }

    [Fact]
    public void TheWorldMayPostAndNothingLeavesAPlayer() {
        var refusal = post.Send(
            PlayerId.None,
            Content.Player(2),
            "a gift",
            [new(Sword, 1)],
            0f,
            "a",
            out var mail
        );

        Assert.Equal(MailRefusal.None, refusal);
        Assert.Equal(MailRefusal.None, post.Claim(Content.Player(2), mail!.Id, "c"));
        Assert.Equal(10_001, Held(2, Sword));
    }
}

public class AuctionTests : MarketFixture {
    readonly PostOffice post;
    readonly MovingAverageMarket market = new(window: 4);
    readonly AuctionHouse house;

    public AuctionTests() {
        post = new(Ledger, capacity: 100);
        house = new(Ledger, post, feeRate: 0.1f, market);
    }

    AuctionListing List(ulong seller = 1, long buyout = 0, long deposit = 10, float hours = 1f) {
        var refusal = house.List(
            Content.Player(seller),
            Sword,
            2,
            Gold,
            startingBid: 100,
            buyout,
            deposit,
            hours,
            0f,
            $"l{house.Count}",
            out var listing
        );

        Assert.Equal(AuctionRefusal.None, refusal);

        return listing!;
    }

    [Fact]
    public void ListingEscrowsTheGoodsAndTheDeposit() {
        // ⚠ A seller who could list a sword and then vendor it has listed something they do not own.
        List();

        Assert.Equal(9_998, Held(1, Sword));
        Assert.Equal(9_990, Held(1, Gold));
        Assert.Equal(2, InEscrow(Sword));
        Assert.Equal(10, InEscrow(Gold));
    }

    [Fact]
    public void ABadListingIsRefusedAndNothingMoves() {
        Assert.Equal(
            AuctionRefusal.Malformed,
            house.List(Content.Player(1), Sword, 0, Gold, 100, 0, 0, 1f, 0f, "a", out _)
        );

        Assert.Equal(
            AuctionRefusal.Malformed,
            house.List(Content.Player(1), Sword, 1, Gold, 100, 50, 0, 1f, 0f, "b", out _)
        );

        Assert.Equal(10_000, Held(1, Sword));
    }

    [Fact]
    public void ASellerMayNotBidOnTheirOwnListing() {
        var listing = List();

        Assert.Equal(AuctionRefusal.OwnListing, house.Bid(Content.Player(1), listing.Id, 200, 0f));
    }

    [Fact]
    public void ABidBelowTheStandingOneIsRefused() {
        var listing = List();

        Assert.Equal(AuctionRefusal.TooLow, house.Bid(Content.Player(2), listing.Id, 99, 0f));
        Assert.Equal(AuctionRefusal.None, house.Bid(Content.Player(2), listing.Id, 100, 0f));
        Assert.Equal(AuctionRefusal.TooLow, house.Bid(Content.Player(3), listing.Id, 100, 0f));
    }

    [Fact]
    public void OutbiddingRefundsThePreviousBidderInTheSameIntent() {
        // ⚠ Two operations is a window in which the refund can fail and somebody's gold is gone.
        var listing = List();

        house.Bid(Content.Player(2), listing.Id, 500, 0f);

        Assert.Equal(9_500, Held(2, Gold));

        house.Bid(Content.Player(3), listing.Id, 800, 0f);

        Assert.Equal(10_000, Held(2, Gold));
        Assert.Equal(9_200, Held(3, Gold));
        Assert.Equal(810, InEscrow(Gold));
    }

    [Fact]
    public void ASaleDestroysTheFeeAndPaysTheRestByMail() {
        var listing = List(deposit: 10);

        house.Bid(Content.Player(2), listing.Id, 1000, 0f);

        Assert.Equal(1, house.Expire(listing.Expires + 1f));
        Assert.Equal(ListingStatus.Sold, listing.Status);

        // A tenth of a thousand, destroyed and gone for ever — doc 28's primary currency sink.
        Assert.Equal(100, Destroyed(Gold));

        var seller = Assert.Single(post.Of(Content.Player(1)));
        var buyer = Assert.Single(post.Of(Content.Player(2)));

        Assert.Equal(910, seller.Attachments[0].Amount);
        Assert.Equal(2, buyer.Attachments[0].Amount);

        post.Claim(Content.Player(1), seller.Id, "s");
        post.Claim(Content.Player(2), buyer.Id, "b");

        Assert.Equal(9_990 + 910, Held(1, Gold));
        Assert.Equal(10_002, Held(2, Sword));
        Assert.Equal(0, InEscrow(Gold));
        Assert.Equal(0, InEscrow(Sword));
    }

    [Fact]
    public void ABuyoutEndsItNow() {
        var listing = List(buyout: 500);

        Assert.Equal(AuctionRefusal.None, house.Buyout(Content.Player(2), listing.Id, 0f));
        Assert.Equal(ListingStatus.Sold, listing.Status);
        Assert.Equal(500, listing.HighBid);
        Assert.Equal(50, Destroyed(Gold));
    }

    [Fact]
    public void AListingWithNoBuyoutRefusesOne() {
        var listing = List();

        Assert.Equal(AuctionRefusal.NoBuyout, house.Buyout(Content.Player(2), listing.Id, 0f));
    }

    [Fact]
    public void AnUnsoldListingGoesBackAndTheDepositIsKept() {
        // ⚠ The asymmetry is the point of a deposit: it prices listing something nobody wants.
        var listing = List(deposit: 10);

        Assert.Equal(1, house.Expire(listing.Expires + 1f));
        Assert.Equal(ListingStatus.Expired, listing.Status);
        Assert.Equal(10, Destroyed(Gold));

        var back = Assert.Single(post.Of(Content.Player(1)));

        Assert.Single(back.Attachments.ToArray());
        Assert.Equal(2, back.Attachments[0].Amount);
    }

    [Fact]
    public void AListingWithABidMayNotBeWithdrawn() {
        // ⚠ Otherwise a seller cancels every auction they were about to lose.
        var listing = List();

        Assert.Equal(AuctionRefusal.None, house.Withdraw(Content.Player(1), listing.Id, 0f));

        var second = List();

        house.Bid(Content.Player(2), second.Id, 200, 0f);

        Assert.Equal(AuctionRefusal.HasBids, house.Withdraw(Content.Player(1), second.Id, 0f));
    }

    [Fact]
    public void SomebodyElsesListingIsNotWithdrawable() {
        var listing = List();

        Assert.Equal(AuctionRefusal.NotFound, house.Withdraw(Content.Player(2), listing.Id, 0f));
    }

    [Fact]
    public void ABidAfterTheEndIsRefused() {
        var listing = List();

        Assert.Equal(AuctionRefusal.Closed, house.Bid(Content.Player(2), listing.Id, 500, listing.Expires + 1f));
    }

    [Fact]
    public void ASaleTellsThePriceModelWhatOneWentFor() {
        var listing = List(buyout: 500);

        house.Buyout(Content.Player(2), listing.Id, 0f);

        // Two sold for five hundred, so one is worth two hundred and fifty.
        Assert.Equal(250, market.Suggest(Sword));
    }

    [Fact]
    public void NothingIsEverLostOrMintedByTheAuctionHouse() {
        var random = new GameplayRandom(0xA0C7ul);
        var gold0 = Ledger.Total(Gold);
        var sword0 = Ledger.Total(Sword);
        var now = 0f;

        for (var step = 0; step < 600; step++) {
            now += random.NextFloat() * 900f;

            switch (random.NextInt(4)) {
                case 0:
                    house.List(
                        Content.Player((ulong)random.NextInt(1, 5)),
                        Sword,
                        random.NextInt(1, 4),
                        Gold,
                        random.NextInt(50, 500),
                        random.NextInt(2) == 0 ? random.NextInt(500, 2000) : 0,
                        10,
                        1f,
                        now,
                        $"o{step}",
                        out _
                    );

                    break;

                case 1:
                    foreach (var listing in house.Listings.ToArray()) {
                        house.Bid(Content.Player((ulong)random.NextInt(1, 5)), listing.Id, listing.NextBid, now);
                    }

                    break;

                case 2:
                    foreach (var listing in house.Listings.ToArray()) {
                        house.Buyout(Content.Player((ulong)random.NextInt(1, 5)), listing.Id, now);
                    }

                    break;

                default:
                    house.Expire(now);

                    break;
            }

            foreach (var mail in post.Of(Content.Player((ulong)random.NextInt(1, 5))).ToArray()) {
                post.Claim(mail.To, mail.Id, $"c{step}/{mail.Id.Value:N}");
            }

            // The sink is a real account, so gold destroyed is still gold the total sees.
            Assert.Equal(gold0, Ledger.Total(Gold));
            Assert.Equal(sword0, Ledger.Total(Sword));
        }

        Assert.True(house.Count > 40, $"only {house.Count} listings");
        Assert.True(Destroyed(Gold) > 0, "no fee was ever destroyed");
    }
}

public class PriceModelTests {
    static readonly DefId Sword = DefId.From(Content.Sword);
    static readonly DefId Potion = DefId.From(Content.Potion);

    [Fact]
    public void NothingSoldMeansTheFallback() {
        var market = new MovingAverageMarket();

        Assert.Equal(42, market.Suggest(Sword, 42));
        Assert.Equal(0, market.SalesOf(Sword));
    }

    [Fact]
    public void ItIsWeightedByCount() {
        // ⚠ One sale of a hundred says more about the price than one sale of one — and an unweighted
        // mean lets somebody move the reference by listing a single one at an absurd number.
        var market = new MovingAverageMarket();

        market.Record(new(Sword, 100, 99, 0f));
        market.Record(new(Sword, 10_000, 1, 1f));

        Assert.Equal(199, market.Suggest(Sword));
    }

    [Fact]
    public void OnlyTheLastFewSalesCount() {
        var market = new MovingAverageMarket(window: 3);

        for (var sale = 0; sale < 10; sale++) {
            market.Record(new(Sword, 100, 1, sale));
        }

        market.Record(new(Sword, 400, 1, 10f));
        market.Record(new(Sword, 400, 1, 11f));
        market.Record(new(Sword, 400, 1, 12f));

        Assert.Equal(3, market.SalesOf(Sword));
        Assert.Equal(400, market.Suggest(Sword));
    }

    [Fact]
    public void NonsenseIsNotRecorded() {
        var market = new MovingAverageMarket();

        market.Record(new(Sword, 0, 1, 0f));
        market.Record(new(Sword, 100, 0, 0f));
        market.Record(new(default, 100, 1, 0f));

        Assert.Equal(0, market.SalesOf(Sword));
    }

    [Fact]
    public void EachThingIsPricedOnItsOwn() {
        var market = new MovingAverageMarket();

        market.Record(new(Sword, 500, 1, 0f));
        market.Record(new(Potion, 5, 1, 0f));

        Assert.Equal(500, market.Suggest(Sword));
        Assert.Equal(5, market.Suggest(Potion));
        Assert.True(market.Forget(Sword));
        Assert.Equal(7, market.Suggest(Sword, 7));
    }
}
