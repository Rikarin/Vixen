// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Economy;

/// <summary>One sale, as a price model sees it.</summary>
/// <param name="Asset">What.</param>
/// <param name="UnitPrice">What <em>one</em> went for.</param>
/// <param name="Count">How many, which is how much the sale is worth as evidence.</param>
/// <param name="At">When, on the caller's clock.</param>
/// <remarks>
///     ⚠ <b>A unit price, not a total.</b> Recording totals makes a stack of a hundred look like a
///     hundred-fold price rise, and an average over a mixture of stack sizes is then a number about
///     nothing.
/// </remarks>
public readonly record struct TradeRecord(DefId Asset, long UnitPrice, long Count, float At);

/// <summary>Somewhere prices come from.</summary>
/// <remarks>
///     <b>Doc 28's optional seam, and G-R6's bounded ambition:</b> <em>"a price model, not an economy
///     simulation"</em>. What it is for is a vendor whose buy price responds to supply, and a client
///     that can say "this is about what it goes for" — not for modelling a market.
/// </remarks>
public interface IMarketModel {
    /// <summary>Records a sale.</summary>
    /// <param name="sale">What happened.</param>
    void Record(in TradeRecord sale);

    /// <summary>What one of something is worth.</summary>
    /// <param name="asset">What.</param>
    /// <param name="fallback">What to answer when nothing has been sold.</param>
    /// <returns>The price.</returns>
    long Suggest(DefId asset, long fallback = 0);
}

/// <summary>A moving average over the last few sales of each thing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Weighted by count.</b> One sale of a hundred ore says more about what ore is worth
///         than one sale of a single one, and an unweighted mean lets somebody move the reference
///         price by listing one of something at an absurd number.
///     </para>
///     <para>
///         ⚠ <b>A fixed window of recent sales rather than a decay over time.</b> A time decay needs a
///         clock every read and answers differently depending on when it is asked, which makes a
///         vendor's displayed price flicker while nobody trades. A window of the last <em>n</em> only
///         changes when something actually sells.
///     </para>
/// </remarks>
public sealed class MovingAverageMarket : IMarketModel {
    readonly Dictionary<uint, Queue<TradeRecord>> history = [];

    /// <summary>Makes one.</summary>
    /// <param name="window">How many sales of each thing it remembers.</param>
    public MovingAverageMarket(int window = 20) => Window = Math.Max(1, window);

    /// <summary>How many sales of each thing it remembers.</summary>
    public int Window { get; }

    /// <summary>How many things it has seen sold.</summary>
    public int Assets => history.Count;

    /// <summary>How many sales of something it is remembering.</summary>
    /// <param name="asset">What.</param>
    /// <returns>How many.</returns>
    public int SalesOf(DefId asset) => history.TryGetValue(asset.Value, out var sales) ? sales.Count : 0;

    /// <inheritdoc />
    public void Record(in TradeRecord sale) {
        if (!sale.Asset.IsSome || sale.UnitPrice <= 0 || sale.Count <= 0) {
            return;
        }

        if (!history.TryGetValue(sale.Asset.Value, out var sales)) {
            sales = new();
            history.Add(sale.Asset.Value, sales);
        }

        sales.Enqueue(sale);

        while (sales.Count > Window) {
            sales.Dequeue();
        }
    }

    /// <inheritdoc />
    public long Suggest(DefId asset, long fallback = 0) {
        if (!history.TryGetValue(asset.Value, out var sales) || sales.Count == 0) {
            return fallback;
        }

        var total = 0L;
        var weight = 0L;

        foreach (var sale in sales) {
            total += sale.UnitPrice * sale.Count;
            weight += sale.Count;
        }

        return weight > 0 ? total / weight : fallback;
    }

    /// <summary>Forgets everything about something. What a content removal does.</summary>
    /// <param name="asset">What.</param>
    /// <returns>Whether there was anything.</returns>
    public bool Forget(DefId asset) => history.Remove(asset.Value);
}
