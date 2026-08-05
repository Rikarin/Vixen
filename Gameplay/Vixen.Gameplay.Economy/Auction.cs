// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay.Economy;

/// <summary>One listing's identity.</summary>
/// <param name="Value">The number.</param>
public readonly record struct ListingId(Guid Value) {
    /// <summary>No listing.</summary>
    public static ListingId None => default;

    /// <summary>Whether it names one.</summary>
    public bool IsSome => Value != Guid.Empty;

    /// <summary>Mints a fresh one.</summary>
    /// <returns>The id.</returns>
    public static ListingId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => IsSome ? Value.ToString("N")[..8] : "no listing";
}

/// <summary>Why an auction operation was refused.</summary>
public enum AuctionRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>There is no such listing.</summary>
    NotFound,

    /// <summary>It is over.</summary>
    Closed,

    /// <summary>The numbers do not make a listing.</summary>
    Malformed,

    /// <summary>A seller may not bid on their own listing.</summary>
    OwnListing,

    /// <summary>The bid is not above the standing one.</summary>
    TooLow,

    /// <summary>It has no buyout.</summary>
    NoBuyout,

    /// <summary>Somebody has bid, so it may not be withdrawn.</summary>
    HasBids,

    /// <summary>They cannot pay.</summary>
    Insufficient,

    /// <summary>The ledger refused it.</summary>
    Refused
}

/// <summary>Where a listing is.</summary>
public enum ListingStatus {
    /// <summary>Running.</summary>
    Open,

    /// <summary>Somebody bought or outbid everybody else.</summary>
    Sold,

    /// <summary>Nobody did, and it has gone back.</summary>
    Expired,

    /// <summary>The seller took it down.</summary>
    Withdrawn
}

/// <summary>One thing for sale.</summary>
public sealed class AuctionListing {
    internal AuctionListing(
        ListingId id,
        PlayerId seller,
        DefId asset,
        long count,
        DefId currency,
        long startingBid,
        long buyout,
        long deposit,
        float listedAt,
        float expires
    ) {
        Id = id;
        Seller = seller;
        Asset = asset;
        Count = count;
        Currency = currency;
        StartingBid = startingBid;
        Buyout = buyout;
        Deposit = deposit;
        ListedAt = listedAt;
        Expires = expires;
    }

    /// <summary>Its id.</summary>
    public ListingId Id { get; }

    /// <summary>Who is selling.</summary>
    public PlayerId Seller { get; }

    /// <summary>What.</summary>
    public DefId Asset { get; }

    /// <summary>How many.</summary>
    public long Count { get; }

    /// <summary>What it is priced in.</summary>
    public DefId Currency { get; }

    /// <summary>The least anybody may bid.</summary>
    public long StartingBid { get; }

    /// <summary>What it takes to end it now, or zero for a listing with no buyout.</summary>
    public long Buyout { get; }

    /// <summary>What the seller staked to list it.</summary>
    public long Deposit { get; }

    /// <summary>When it went up.</summary>
    public float ListedAt { get; }

    /// <summary>When it comes down.</summary>
    public float Expires { get; }

    /// <summary>Where it is.</summary>
    public ListingStatus Status { get; internal set; } = ListingStatus.Open;

    /// <summary>The standing bid, or zero.</summary>
    public long HighBid { get; internal set; }

    /// <summary>Who made it, or <see cref="PlayerId.None" />.</summary>
    public PlayerId HighBidder { get; internal set; }

    /// <summary>How many bids there have been. Part of a bid's idempotency key.</summary>
    public int Bids { get; internal set; }

    /// <summary>What the next bid has to be at least.</summary>
    public long NextBid => HighBid > 0 ? HighBid + 1 : StartingBid;

    /// <summary>What one of it went for, for the price model.</summary>
    public long UnitPrice => Count > 0 ? HighBid / Count : HighBid;
}

/// <summary>A market: an order book, a deposit, a fee, and settlement by mail.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28's shape.</b> The fee is <em>"the primary currency sink"</em>, and settlement goes
///         into mail because the seller is usually not online when their listing sells.
///     </para>
///     <para>
///         ⚠ <b>The goods leave the seller when the listing goes up</b> — the same escrow rule mail
///         has, and the same reason: a seller who could list a sword and then sell it to a vendor has
///         listed something they no longer own.
///     </para>
///     <para>
///         ⚠ <b>A bid escrows the money, and outbidding refunds the previous bidder in the <em>same</em>
///         intent.</b> Two operations — take the new bid, then give the old one back — is a window in
///         which the refund can fail and one player's gold is simply gone. One intent has no window.
///     </para>
///     <para>
///         ⚠ <b>The deposit comes back on a sale and is kept on an expiry.</b> That asymmetry is the
///         whole point of a deposit: it prices listing something nobody wants, which is the thing that
///         makes an auction house unusable when it is free.
///     </para>
/// </remarks>
public sealed class AuctionHouse {
    readonly Dictionary<ListingId, AuctionListing> listings = [];
    readonly IEconomyLedger ledger;
    readonly PostOffice post;

    /// <summary>Makes one.</summary>
    /// <param name="ledger">Where movements are recorded.</param>
    /// <param name="post">Where settlements are delivered.</param>
    /// <param name="feeRate">What fraction of a sale is destroyed. The primary sink.</param>
    /// <param name="market">Where sales are recorded for pricing, or null.</param>
    public AuctionHouse(IEconomyLedger ledger, PostOffice post, float feeRate = 0.05f, IMarketModel? market = null) {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(post);

        this.ledger = ledger;
        this.post = post;
        FeeRate = Math.Clamp(feeRate, 0f, 1f);
        Market = market;
    }

    /// <summary>What fraction of a sale is destroyed.</summary>
    public float FeeRate { get; }

    /// <summary>Where sales are recorded for pricing.</summary>
    public IMarketModel? Market { get; }

    /// <summary>Everything on the book, in id order so a page is stable.</summary>
    public IEnumerable<AuctionListing> Listings =>
        listings.Values.Where(listing => listing.Status == ListingStatus.Open).OrderBy(listing => listing.Id.Value);

    /// <summary>How many listings it has ever held.</summary>
    public int Count => listings.Count;

    /// <summary>One listing.</summary>
    /// <param name="id">Which.</param>
    /// <returns>It, or null.</returns>
    public AuctionListing? Find(ListingId id) => listings.GetValueOrDefault(id);

    /// <summary>Everything on the book of one asset.</summary>
    /// <param name="asset">What.</param>
    /// <returns>The listings, cheapest per unit first.</returns>
    public IEnumerable<AuctionListing> For(DefId asset) =>
        Listings.Where(listing => listing.Asset == asset)
            .OrderBy(listing => listing.Count > 0 ? listing.StartingBid / listing.Count : listing.StartingBid);

    /// <summary>Puts something up.</summary>
    /// <param name="seller">Who.</param>
    /// <param name="asset">What.</param>
    /// <param name="count">How many.</param>
    /// <param name="currency">What it is priced in.</param>
    /// <param name="startingBid">The least anybody may bid.</param>
    /// <param name="buyout">What ends it now, or zero.</param>
    /// <param name="deposit">What the seller stakes.</param>
    /// <param name="hours">How long it runs.</param>
    /// <param name="now">The clock.</param>
    /// <param name="operation">What makes this listing distinct from the same one retried.</param>
    /// <param name="listing">The listing, when it went up.</param>
    /// <returns>The refusal, or <see cref="AuctionRefusal.None" />.</returns>
    public AuctionRefusal List(
        PlayerId seller,
        DefId asset,
        long count,
        DefId currency,
        long startingBid,
        long buyout,
        long deposit,
        float hours,
        float now,
        string operation,
        out AuctionListing? listing
    ) {
        listing = null;

        if (!seller.IsSome || !asset.IsSome || !currency.IsSome || count <= 0 || startingBid <= 0 || hours <= 0f) {
            return AuctionRefusal.Malformed;
        }

        if (buyout > 0 && buyout < startingBid) {
            return AuctionRefusal.Malformed;
        }

        var house = EconomyAccount.Of(EconomyAccount.Escrow);
        var account = EconomyAccount.Of(seller);

        var movements = new List<AssetMove> {
            new(account, asset, -count),
            new(house, asset, count)
        };

        if (deposit > 0) {
            movements.Add(new(account, currency, -deposit));
            movements.Add(new(house, currency, deposit));
        }

        var result = ledger.Post(new($"auction/{operation}/list", movements, $"{seller} lists {count} of {asset}"));

        if (!result.Ok) {
            return result.Verdict == EconomyVerdict.Insufficient ? AuctionRefusal.Insufficient : AuctionRefusal.Refused;
        }

        listing = new(
            ListingId.New(),
            seller,
            asset,
            count,
            currency,
            startingBid,
            Math.Max(0, buyout),
            Math.Max(0, deposit),
            now,
            now + (hours * 3600f)
        );

        listings.Add(listing.Id, listing);

        return AuctionRefusal.None;
    }

    /// <summary>Bids on something.</summary>
    /// <param name="bidder">Who.</param>
    /// <param name="id">Which listing.</param>
    /// <param name="amount">How much.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The refusal, or <see cref="AuctionRefusal.None" />.</returns>
    public AuctionRefusal Bid(PlayerId bidder, ListingId id, long amount, float now) {
        if (Find(id) is not { } listing) {
            return AuctionRefusal.NotFound;
        }

        if (listing.Status != ListingStatus.Open || now >= listing.Expires) {
            return AuctionRefusal.Closed;
        }

        if (bidder == listing.Seller) {
            return AuctionRefusal.OwnListing;
        }

        if (amount < listing.NextBid) {
            return AuctionRefusal.TooLow;
        }

        var house = EconomyAccount.Of(EconomyAccount.Escrow);
        var movements = new List<AssetMove> {
            new(EconomyAccount.Of(bidder), listing.Currency, -amount),
            new(house, listing.Currency, amount)
        };

        if (listing.HighBidder.IsSome) {
            // The refund is in this intent, not the next one. A window between taking the new bid and
            // returning the old one is a window in which somebody's gold is simply gone.
            movements.Add(new(house, listing.Currency, -listing.HighBid));
            movements.Add(new(EconomyAccount.Of(listing.HighBidder), listing.Currency, listing.HighBid));
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"auction/{listing.Id.Value:N}/bid/{listing.Bids}");
        var result = ledger.Post(new(key, movements, $"{bidder} bids {amount}"));

        if (!result.Ok) {
            return result.Verdict == EconomyVerdict.Insufficient ? AuctionRefusal.Insufficient : AuctionRefusal.Refused;
        }

        listing.HighBid = amount;
        listing.HighBidder = bidder;
        listing.Bids++;

        return AuctionRefusal.None;
    }

    /// <summary>Ends a listing now at its buyout price.</summary>
    /// <param name="buyer">Who.</param>
    /// <param name="id">Which listing.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The refusal, or <see cref="AuctionRefusal.None" />.</returns>
    public AuctionRefusal Buyout(PlayerId buyer, ListingId id, float now) {
        if (Find(id) is not { } listing) {
            return AuctionRefusal.NotFound;
        }

        if (listing.Buyout <= 0) {
            return AuctionRefusal.NoBuyout;
        }

        var refusal = Bid(buyer, id, Math.Max(listing.Buyout, listing.NextBid), now);

        return refusal != AuctionRefusal.None ? refusal : Close(listing, now);
    }

    /// <summary>Takes a listing down, which keeps the deposit.</summary>
    /// <param name="seller">Who.</param>
    /// <param name="id">Which listing.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The refusal, or <see cref="AuctionRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Refused once somebody has bid.</b> A seller who could withdraw after seeing a bid could
    ///     use the auction house to discover what somebody would pay and then sell to them privately —
    ///     and, worse, could cancel every auction they were about to lose.
    /// </remarks>
    public AuctionRefusal Withdraw(PlayerId seller, ListingId id, float now) {
        if (Find(id) is not { } listing || listing.Seller != seller) {
            return AuctionRefusal.NotFound;
        }

        if (listing.Status != ListingStatus.Open) {
            return AuctionRefusal.Closed;
        }

        if (listing.HighBidder.IsSome) {
            return AuctionRefusal.HasBids;
        }

        return Return(listing, now, ListingStatus.Withdrawn, keepDeposit: true);
    }

    /// <summary>Closes whatever has run out, selling to the high bidder or sending it back.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>How many listings closed.</returns>
    public int Expire(float now) {
        var closed = 0;

        foreach (var listing in listings.Values.ToArray()) {
            if (listing.Status != ListingStatus.Open || now < listing.Expires) {
                continue;
            }

            var refusal = listing.HighBidder.IsSome
                ? Close(listing, now)
                : Return(listing, now, ListingStatus.Expired, keepDeposit: true);

            if (refusal == AuctionRefusal.None) {
                closed++;
            }
        }

        return closed;
    }

    /// <summary>What the fee on a sale is.</summary>
    /// <param name="amount">What it sold for.</param>
    /// <returns>What is destroyed.</returns>
    public long FeeOn(long amount) => (long)(amount * FeeRate);

    AuctionRefusal Close(AuctionListing listing, float now) {
        var house = EconomyAccount.Of(EconomyAccount.Escrow);
        var fee = FeeOn(listing.HighBid);
        var proceeds = listing.HighBid - fee;

        // The fee is destroyed here and nowhere else — doc 28's primary currency sink.
        var movements = new List<AssetMove> {
            new(house, listing.Currency, -listing.HighBid),
            new(EconomyAccount.Of(EconomyAccount.Sink), listing.Currency, fee),
            new(EconomyAccount.Of(EconomyAccount.Mail), listing.Currency, proceeds),
            new(house, listing.Asset, -listing.Count),
            new(EconomyAccount.Of(EconomyAccount.Mail), listing.Asset, listing.Count)
        };

        if (listing.Deposit > 0) {
            movements.Add(new(house, listing.Currency, -listing.Deposit));
            movements.Add(new(EconomyAccount.Of(EconomyAccount.Mail), listing.Currency, listing.Deposit));
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"auction/{listing.Id.Value:N}/close");
        var result = ledger.Post(new(key, movements, $"{listing.Id} sold for {listing.HighBid}"));

        if (!result.Ok) {
            return AuctionRefusal.Refused;
        }

        // Both halves are already in the mail account, so these letters carry rather than move — the
        // same arrangement a returned letter uses.
        post.Deliver(
            listing.Seller,
            "Auction successful",
            [new(listing.Currency, proceeds + listing.Deposit)],
            now
        );

        post.Deliver(listing.HighBidder, "Auction won", [new(listing.Asset, listing.Count)], now);

        listing.Status = ListingStatus.Sold;
        Market?.Record(new(listing.Asset, listing.UnitPrice, listing.Count, now));

        return AuctionRefusal.None;
    }

    AuctionRefusal Return(AuctionListing listing, float now, ListingStatus status, bool keepDeposit) {
        var house = EconomyAccount.Of(EconomyAccount.Escrow);
        var movements = new List<AssetMove> {
            new(house, listing.Asset, -listing.Count),
            new(EconomyAccount.Of(EconomyAccount.Mail), listing.Asset, listing.Count)
        };

        if (listing.Deposit > 0) {
            // Kept means destroyed rather than pocketed by a world account that grows for ever.
            var to = keepDeposit
                ? EconomyAccount.Of(EconomyAccount.Sink)
                : EconomyAccount.Of(EconomyAccount.Mail);

            movements.Add(new(house, listing.Currency, -listing.Deposit));
            movements.Add(new(to, listing.Currency, listing.Deposit));
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"auction/{listing.Id.Value:N}/return");
        var result = ledger.Post(new(key, movements, $"{listing.Id} came back"));

        if (!result.Ok) {
            return AuctionRefusal.Refused;
        }

        var attachments = keepDeposit || listing.Deposit == 0
            ? new List<MailAttachment> { new(listing.Asset, listing.Count) }
            : [new(listing.Asset, listing.Count), new(listing.Currency, listing.Deposit)];

        post.Deliver(listing.Seller, "Auction unsuccessful", attachments, now);

        listing.Status = status;

        return AuctionRefusal.None;
    }
}
