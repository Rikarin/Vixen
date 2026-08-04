// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Economy;

/// <summary>One row of a vendor's stock list.</summary>
[DataContract("VendorStock")]
public sealed class VendorStockDefinition {
    /// <summary>The address of what is sold.</summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>The address of what it is paid for in.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>How much, each.</summary>
    public long Price { get; set; } = 1;

    /// <summary>How many are in stock. Zero for an unlimited row.</summary>
    public int Quantity { get; set; }

    /// <summary>How long until one sold comes back, in seconds. Zero for never.</summary>
    public float RestockSeconds { get; set; }

    /// <summary>What has to be true to buy it.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];
}

/// <summary>A vendor: a stock list, a buyback window, and what it pays for things.</summary>
[DataContract("VendorDefinition")]
public sealed record VendorDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What being able to use it is. Empty for one anybody may use.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>What it sells.</summary>
    public List<VendorStockDefinition> Stock { get; set; } = [];

    /// <summary>How many things it will hold for somebody who sold them by accident.</summary>
    public int BuybackSlots { get; set; } = 12;

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var row in Stock) {
            foreach (var requirement in row.Requires) {
                if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                    tags.Add(requirement.Subject);
                }
            }
        }
    }
}

/// <summary>One stock row, compiled.</summary>
public sealed class VendorStock {
    internal VendorStock(VendorStockDefinition definition, int index, RequirementSet requirements) {
        Definition = definition;
        Index = index;
        Requirements = requirements;
        Item = DefId.From(definition.Item);
        Currency = DefId.From(definition.Currency);
    }

    /// <summary>What it was compiled from.</summary>
    public VendorStockDefinition Definition { get; }

    /// <summary>Which row of the list it is.</summary>
    public int Index { get; }

    /// <summary>What is sold.</summary>
    public DefId Item { get; }

    /// <summary>What it is paid for in.</summary>
    public DefId Currency { get; }

    /// <summary>How much, each. Never below zero.</summary>
    public long Price => Math.Max(0, Definition.Price);

    /// <summary>How many are in stock, or zero for unlimited.</summary>
    public int Quantity => Math.Max(0, Definition.Quantity);

    /// <summary>Whether it never runs out.</summary>
    public bool IsUnlimited => Quantity == 0;

    /// <summary>How long until one sold comes back.</summary>
    public float RestockSeconds => MathF.Max(0f, Definition.RestockSeconds);

    /// <summary>What has to be true to buy it.</summary>
    public RequirementSet Requirements { get; }
}

/// <summary>A vendor, compiled.</summary>
public sealed class Vendor {
    readonly VendorStock[] stock;

    internal Vendor(VendorDefinition definition, GameplayTag tag, VendorStock[] stock) {
        Definition = definition;
        Tag = tag;
        this.stock = stock;
    }

    /// <summary>What it was compiled from.</summary>
    public VendorDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What being able to use it is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What it sells.</summary>
    public ReadOnlySpan<VendorStock> Stock => stock;

    /// <summary>How many things it holds for buyback.</summary>
    public int BuybackSlots => Math.Max(0, Definition.BuybackSlots);
}

/// <summary>Why a purchase or a sale was refused.</summary>
public enum VendorRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>There is no such row.</summary>
    UnknownStock,

    /// <summary>The count is not a positive number.</summary>
    BadCount,

    /// <summary>It has sold out and has not restocked.</summary>
    OutOfStock,

    /// <summary>A requirement on the row is not met.</summary>
    Requirements,

    /// <summary>They cannot afford it.</summary>
    Insufficient,

    /// <summary>There is nothing in that buyback slot.</summary>
    NothingToBuyBack,

    /// <summary>The ledger refused it.</summary>
    Refused
}

/// <summary>What a vendor is holding for somebody who sold something.</summary>
/// <param name="Item">What.</param>
/// <param name="Count">How many.</param>
/// <param name="Currency">What they were paid in.</param>
/// <param name="Paid">How much they were paid, which is what buying it back costs.</param>
public readonly record struct BuybackEntry(DefId Item, int Count, DefId Currency, long Paid);

/// <summary>One vendor's live state: what is left, when it comes back, and the buyback window.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Per vendor <em>instance</em>, not per definition.</b> A shard's vendor running out is a
///         fact about that shard; sharing the counter across a fleet would make a limited-stock item a
///         race between continents.
///     </para>
///     <para>
///         ⚠ <b>Buyback is per player, and it is a list rather than a container.</b> This library never
///         holds an item — see the README — so what is recorded is what was sold and what was paid for
///         it, and the caller puts the item back where it came from.
///     </para>
/// </remarks>
public sealed class VendorState {
    readonly int[] remaining;
    readonly float[] restockAt;
    readonly Dictionary<PlayerId, List<BuybackEntry>> buyback = [];

    /// <summary>Makes a fresh one with everything in stock.</summary>
    /// <param name="vendor">Which vendor.</param>
    public VendorState(Vendor vendor) {
        ArgumentNullException.ThrowIfNull(vendor);

        Vendor = vendor;
        remaining = new int[vendor.Stock.Length];
        restockAt = new float[vendor.Stock.Length];

        for (var row = 0; row < remaining.Length; row++) {
            remaining[row] = vendor.Stock[row].Quantity;
        }
    }

    /// <summary>Which vendor.</summary>
    public Vendor Vendor { get; }

    /// <summary>How many of a row are left. Unlimited rows report their price count as −1.</summary>
    /// <param name="row">Which row.</param>
    /// <returns>How many, or −1 for unlimited.</returns>
    public int Remaining(int row) =>
        (uint)row >= (uint)remaining.Length ? 0 : Vendor.Stock[row].IsUnlimited ? -1 : remaining[row];

    /// <summary>What a vendor is holding for somebody.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their buyback list, newest first.</returns>
    public IReadOnlyList<BuybackEntry> BuybackFor(PlayerId player) =>
        buyback.TryGetValue(player, out var entries) ? entries : [];

    /// <summary>Puts back whatever has restocked.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>How many rows came back.</returns>
    public int Restock(float now) {
        var restocked = 0;

        for (var row = 0; row < remaining.Length; row++) {
            var entry = Vendor.Stock[row];

            if (entry.IsUnlimited || entry.RestockSeconds <= 0f || remaining[row] >= entry.Quantity) {
                continue;
            }

            if (now < restockAt[row]) {
                continue;
            }

            remaining[row] = entry.Quantity;
            restocked++;
        }

        return restocked;
    }

    /// <summary>Whether somebody may buy something, and why not.</summary>
    /// <param name="row">Which row.</param>
    /// <param name="count">How many.</param>
    /// <param name="context">What their requirements are evaluated against, or null to skip them.</param>
    /// <returns>The refusal, or <see cref="VendorRefusal.None" />.</returns>
    public VendorRefusal CanBuy(int row, int count, IRequirementContext? context) {
        if ((uint)row >= (uint)remaining.Length) {
            return VendorRefusal.UnknownStock;
        }

        if (count <= 0) {
            return VendorRefusal.BadCount;
        }

        var entry = Vendor.Stock[row];

        if (!entry.IsUnlimited && remaining[row] < count) {
            return VendorRefusal.OutOfStock;
        }

        if (context is not null && !entry.Requirements.IsMetBy(context)) {
            return VendorRefusal.Requirements;
        }

        return VendorRefusal.None;
    }

    /// <summary>Buys something.</summary>
    /// <param name="buyer">Who.</param>
    /// <param name="row">Which row.</param>
    /// <param name="count">How many.</param>
    /// <param name="ledger">Where it is recorded.</param>
    /// <param name="context">What their requirements are evaluated against.</param>
    /// <param name="now">The clock, for the restock timer.</param>
    /// <param name="operation">What makes this purchase distinct from the same one retried.</param>
    /// <returns>The refusal, or <see cref="VendorRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The stock is decremented only after the ledger says yes.</b> Taking it first and
    ///     putting it back on refusal is two writes with a window in between, and the window is where
    ///     a limited-stock item goes missing without anybody getting it.
    /// </remarks>
    public VendorRefusal Buy(
        PlayerId buyer,
        int row,
        int count,
        IEconomyLedger ledger,
        IRequirementContext? context,
        float now,
        string operation
    ) {
        ArgumentNullException.ThrowIfNull(ledger);

        var refusal = CanBuy(row, count, context);

        if (refusal != VendorRefusal.None) {
            return refusal;
        }

        var entry = Vendor.Stock[row];
        var vendor = EconomyAccount.Of(EconomyAccount.Vendor);
        var account = EconomyAccount.Of(buyer);
        var cost = entry.Price * count;

        var result = ledger.Post(
            new(
                $"vendor/{Vendor.Id.Value}/{row}/{operation}",
                [
                    new(account, entry.Currency, -cost),
                    new(vendor, entry.Currency, cost),
                    new(vendor, entry.Item, -count),
                    new(account, entry.Item, count)
                ],
                $"{buyer} buys {count} of {entry.Definition.Item}"
            )
        );

        if (!result.Ok) {
            return result.Verdict == EconomyVerdict.Insufficient ? VendorRefusal.Insufficient : VendorRefusal.Refused;
        }

        if (result.Verdict == EconomyVerdict.Applied && !entry.IsUnlimited) {
            remaining[row] -= count;

            if (remaining[row] < entry.Quantity && entry.RestockSeconds > 0f) {
                restockAt[row] = now + entry.RestockSeconds;
            }
        }

        return VendorRefusal.None;
    }

    /// <summary>Sells something to the vendor, which records it for buyback.</summary>
    /// <param name="seller">Who.</param>
    /// <param name="item">What.</param>
    /// <param name="count">How many.</param>
    /// <param name="currency">What they are paid in.</param>
    /// <param name="price">How much, in total.</param>
    /// <param name="ledger">Where it is recorded.</param>
    /// <param name="operation">What makes this sale distinct from the same one retried.</param>
    /// <returns>The refusal, or <see cref="VendorRefusal.None" />.</returns>
    public VendorRefusal Sell(
        PlayerId seller,
        DefId item,
        int count,
        DefId currency,
        long price,
        IEconomyLedger ledger,
        string operation
    ) {
        ArgumentNullException.ThrowIfNull(ledger);

        if (count <= 0 || price < 0) {
            return VendorRefusal.BadCount;
        }

        var vendor = EconomyAccount.Of(EconomyAccount.Vendor);
        var account = EconomyAccount.Of(seller);

        var result = ledger.Post(
            new(
                $"vendor/{Vendor.Id.Value}/sell/{operation}",
                [
                    new(account, item, -count),
                    new(vendor, item, count),
                    new(vendor, currency, -price),
                    new(account, currency, price)
                ],
                $"{seller} sells {count} of {item}"
            )
        );

        if (!result.Ok) {
            return result.Verdict == EconomyVerdict.Insufficient ? VendorRefusal.Insufficient : VendorRefusal.Refused;
        }

        if (result.Verdict != EconomyVerdict.Applied || Vendor.BuybackSlots == 0) {
            return VendorRefusal.None;
        }

        if (!buyback.TryGetValue(seller, out var entries)) {
            entries = [];
            buyback.Add(seller, entries);
        }

        entries.Insert(0, new(item, count, currency, price));

        // The oldest falls off the end, which is what makes it a window rather than a second bank.
        while (entries.Count > Vendor.BuybackSlots) {
            entries.RemoveAt(entries.Count - 1);
        }

        return VendorRefusal.None;
    }

    /// <summary>Buys something back at exactly what it was sold for.</summary>
    /// <param name="player">Who.</param>
    /// <param name="slot">Which entry.</param>
    /// <param name="ledger">Where it is recorded.</param>
    /// <param name="operation">What makes this distinct from the same one retried.</param>
    /// <returns>The refusal, or <see cref="VendorRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>At what it was sold for, not at what it is worth.</b> Buyback exists because somebody
    ///     sold something by accident; charging a spread on the undo turns a mistake into a fee and
    ///     makes the price of a mis-click depend on how quickly it was noticed.
    /// </remarks>
    public VendorRefusal BuyBack(PlayerId player, int slot, IEconomyLedger ledger, string operation) {
        ArgumentNullException.ThrowIfNull(ledger);

        if (!buyback.TryGetValue(player, out var entries) || (uint)slot >= (uint)entries.Count) {
            return VendorRefusal.NothingToBuyBack;
        }

        var entry = entries[slot];
        var vendor = EconomyAccount.Of(EconomyAccount.Vendor);
        var account = EconomyAccount.Of(player);

        var result = ledger.Post(
            new(
                $"vendor/{Vendor.Id.Value}/buyback/{operation}",
                [
                    new(account, entry.Currency, -entry.Paid),
                    new(vendor, entry.Currency, entry.Paid),
                    new(vendor, entry.Item, -entry.Count),
                    new(account, entry.Item, entry.Count)
                ],
                $"{player} buys back {entry.Count} of {entry.Item}"
            )
        );

        if (!result.Ok) {
            return result.Verdict == EconomyVerdict.Insufficient ? VendorRefusal.Insufficient : VendorRefusal.Refused;
        }

        if (result.Verdict == EconomyVerdict.Applied) {
            entries.RemoveAt(slot);
        }

        return VendorRefusal.None;
    }
}
