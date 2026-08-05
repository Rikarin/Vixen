// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Economy;

/// <summary>Where a trade is.</summary>
public enum TradeStatus {
    /// <summary>Open, and either side may still change what they are offering.</summary>
    Open,

    /// <summary>Both have confirmed the same revision. Nothing may change; it is waiting to settle.</summary>
    Locked,

    /// <summary>Settled.</summary>
    Completed,

    /// <summary>Called off, or refused by the ledger.</summary>
    Cancelled
}

/// <summary>Why a trade operation was refused.</summary>
public enum TradeRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>They are not in this trade.</summary>
    NotAParty,

    /// <summary>It is not open any more.</summary>
    NotOpen,

    /// <summary>The confirmation quoted a revision that is no longer the current one.</summary>
    Stale,

    /// <summary>Both sides have not confirmed.</summary>
    NotConfirmed,

    /// <summary>An offer is not a positive number of something.</summary>
    BadOffer,

    /// <summary>One of them does not hold what they are offering.</summary>
    Insufficient,

    /// <summary>One of them has blocked the other.</summary>
    Severed,

    /// <summary>The ledger refused the swap.</summary>
    Refused
}

/// <summary>What one side is putting up.</summary>
public sealed class TradeOffer {
    readonly Dictionary<uint, long> assets = [];

    /// <summary>Whose it is.</summary>
    public PlayerId Owner { get; internal set; }

    /// <summary>How many distinct things are on the table.</summary>
    public int Count => assets.Count;

    /// <summary>Whether there is nothing on it.</summary>
    public bool IsEmpty => assets.Count == 0;

    /// <summary>What is on it, in asset order so two servers describe it the same way.</summary>
    public IEnumerable<(DefId Asset, long Amount)> Assets =>
        assets.Where(entry => entry.Value > 0)
            .Select(entry => (new DefId(entry.Key), entry.Value))
            .OrderBy(entry => entry.Item1.Value);

    /// <summary>How much of something is on it.</summary>
    /// <param name="asset">What.</param>
    /// <returns>How much.</returns>
    public long AmountOf(DefId asset) => assets.GetValueOrDefault(asset.Value);

    internal void Set(DefId asset, long amount) {
        if (amount <= 0) {
            assets.Remove(asset.Value);
        } else {
            assets[asset.Value] = amount;
        }
    }

    internal void Clear() => assets.Clear();
}

/// <summary>A two-sided trade with a confirm-lock.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 is emphatic and it is right: the confirm-lock is not UI polish.</b> It is the
///         mechanism that makes the classic swap-at-the-last-moment scam impossible — the one where
///         somebody replaces a legendary with a grey the instant before you click accept.
///     </para>
///     <para>
///         ⚠ <b>A confirmation quotes the revision it saw, and that is the part the document does not
///         say.</b> "Any change re-opens both confirmations" is necessary and not sufficient: it loses
///         the race where a change and a confirmation cross in flight, because the confirmation
///         arrives after the change has already cleared the flags and simply sets one again — against
///         goods its sender never saw. Making the client quote a revision turns that race into a
///         <see cref="TradeRefusal.Stale" />, and it is the only form of the rule that holds without
///         assuming an ordering the network does not give.
///     </para>
///     <para>
///         ⚠ <b>Nothing here moves anything.</b> <see cref="Settle" /> produces one
///         <see cref="EconomyIntent" /> and the ledger decides; a trade that half-applied is the
///         duplication bug the whole design exists to prevent, and one intent is the only arrangement
///         in which it cannot happen.
///     </para>
/// </remarks>
public sealed class TradeSession {
    readonly TradeOffer left = new();
    readonly TradeOffer right = new();

    int leftConfirmed = -1;
    int rightConfirmed = -1;

    /// <summary>Opens a trade between two players.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <param name="id">What names it, and what its idempotency key is built from.</param>
    public TradeSession(PlayerId left, PlayerId right, string id) {
        Left = left;
        Right = right;
        Id = id ?? string.Empty;
        this.left.Owner = left;
        this.right.Owner = right;
    }

    /// <summary>One party.</summary>
    public PlayerId Left { get; }

    /// <summary>The other.</summary>
    public PlayerId Right { get; }

    /// <summary>What names it.</summary>
    public string Id { get; }

    /// <summary>Where it is.</summary>
    public TradeStatus Status { get; private set; } = TradeStatus.Open;

    /// <summary>How many times what is on the table has changed. What a confirmation quotes.</summary>
    public int Revision { get; private set; }

    /// <summary>What the left is offering.</summary>
    public TradeOffer LeftOffer => left;

    /// <summary>What the right is offering.</summary>
    public TradeOffer RightOffer => right;

    /// <summary>Whether both have confirmed the current revision.</summary>
    public bool IsLocked => leftConfirmed == Revision && rightConfirmed == Revision;

    /// <summary>Raised whenever what is on the table changes, so both clients redraw.</summary>
    public event Action<TradeSession>? Changed;

    /// <summary>Whether somebody has confirmed the current revision.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether they have.</returns>
    public bool HasConfirmed(PlayerId player) =>
        player == Left ? leftConfirmed == Revision : player == Right && rightConfirmed == Revision;

    /// <summary>What a player is offering.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their offer, or null when they are not in this trade.</returns>
    public TradeOffer? OfferOf(PlayerId player) => player == Left ? left : player == Right ? right : null;

    /// <summary>Puts something on the table, or takes it off with a zero.</summary>
    /// <param name="player">Who.</param>
    /// <param name="asset">What.</param>
    /// <param name="amount">How much. Zero removes it.</param>
    /// <returns>The refusal, or <see cref="TradeRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Every change bumps the revision, including one that puts the table back exactly as it
    ///     was.</b> Detecting a no-op and leaving the confirmations standing would be a way to clear a
    ///     confirmation and set it again without the other side seeing anything move.
    /// </remarks>
    public TradeRefusal Offer(PlayerId player, DefId asset, long amount) {
        if (OfferOf(player) is not { } offer) {
            return TradeRefusal.NotAParty;
        }

        // ⚠ Allowed while *locked*, and that is doc 28's sentence read literally: "any change
        // re-opens both confirmations" only means anything if a change is possible once both have
        // confirmed. Refusing here would make the swap impossible by making the gesture impossible,
        // which is a different and weaker guarantee — it would also refuse somebody legitimately
        // changing their mind in the moment before the last button. What makes it safe is that the
        // change bumps the revision, so the settle that follows is settling something nobody has
        // confirmed.
        if (Status is TradeStatus.Completed or TradeStatus.Cancelled) {
            return TradeRefusal.NotOpen;
        }

        if (amount < 0 || !asset.IsSome) {
            return TradeRefusal.BadOffer;
        }

        offer.Set(asset, amount);
        Bump();

        return TradeRefusal.None;
    }

    /// <summary>Says yes to what is on the table right now.</summary>
    /// <param name="player">Who.</param>
    /// <param name="revision">The revision they were looking at.</param>
    /// <returns>The refusal, or <see cref="TradeRefusal.None" />.</returns>
    public TradeRefusal Confirm(PlayerId player, int revision) {
        if (player != Left && player != Right) {
            return TradeRefusal.NotAParty;
        }

        if (Status != TradeStatus.Open) {
            return TradeRefusal.NotOpen;
        }

        if (revision != Revision) {
            return TradeRefusal.Stale;
        }

        if (player == Left) {
            leftConfirmed = revision;
        } else {
            rightConfirmed = revision;
        }

        if (IsLocked) {
            Status = TradeStatus.Locked;
        }

        Changed?.Invoke(this);

        return TradeRefusal.None;
    }

    /// <summary>Takes a confirmation back.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The refusal, or <see cref="TradeRefusal.None" />.</returns>
    /// <remarks>
    ///     Allowed while locked, because "both have said yes and neither has clicked the last button"
    ///     is a state somebody must be able to get out of.
    /// </remarks>
    public TradeRefusal Unconfirm(PlayerId player) {
        if (player != Left && player != Right) {
            return TradeRefusal.NotAParty;
        }

        if (Status is TradeStatus.Completed or TradeStatus.Cancelled) {
            return TradeRefusal.NotOpen;
        }

        if (player == Left) {
            leftConfirmed = -1;
        } else {
            rightConfirmed = -1;
        }

        Status = TradeStatus.Open;
        Changed?.Invoke(this);

        return TradeRefusal.None;
    }

    /// <summary>Calls it off.</summary>
    /// <param name="player">Who, or <see cref="PlayerId.None" /> for the realm.</param>
    /// <returns>The refusal, or <see cref="TradeRefusal.None" />.</returns>
    public TradeRefusal Cancel(PlayerId player) {
        if (player.IsSome && player != Left && player != Right) {
            return TradeRefusal.NotAParty;
        }

        if (Status is TradeStatus.Completed) {
            return TradeRefusal.NotOpen;
        }

        Status = TradeStatus.Cancelled;
        Changed?.Invoke(this);

        return TradeRefusal.None;
    }

    /// <summary>What the swap would be, as one intent.</summary>
    /// <returns>The intent, or null when it is not locked.</returns>
    /// <remarks>
    ///     ⚠ <b>The key names the trade <em>and its revision</em>.</b> A retry of the same confirmed
    ///     swap replays; a different set of goods under the same trade id is a different operation and
    ///     must not be mistaken for a duplicate of it.
    /// </remarks>
    public EconomyIntent? Compose() {
        if (Status != TradeStatus.Locked) {
            return null;
        }

        var movements = new List<AssetMove>();
        var from = EconomyAccount.Of(Left);
        var to = EconomyAccount.Of(Right);

        foreach (var (asset, amount) in left.Assets) {
            movements.Add(new(from, asset, -amount));
            movements.Add(new(to, asset, amount));
        }

        foreach (var (asset, amount) in right.Assets) {
            movements.Add(new(to, asset, -amount));
            movements.Add(new(from, asset, amount));
        }

        return new($"trade/{Id}/{Revision}", movements, $"trade between {Left} and {Right}");
    }

    /// <summary>Does the swap.</summary>
    /// <param name="ledger">Where it is recorded.</param>
    /// <param name="result">What the ledger said.</param>
    /// <returns>The refusal, or <see cref="TradeRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>A ledger refusal cancels the trade rather than reopening it.</b> The most common
    ///     refusal is that somebody no longer holds what they offered — they sold it in another window
    ///     while this one was locked — and reopening would put both parties back in front of a table
    ///     that is now a lie.
    /// </remarks>
    public TradeRefusal Settle(IEconomyLedger ledger, out EconomyResult result) {
        ArgumentNullException.ThrowIfNull(ledger);

        result = default;

        if (Status == TradeStatus.Completed) {
            return TradeRefusal.NotOpen;
        }

        if (Compose() is not { } intent) {
            return Status == TradeStatus.Open ? TradeRefusal.NotConfirmed : TradeRefusal.NotOpen;
        }

        result = ledger.Post(intent);

        if (!result.Ok) {
            Status = TradeStatus.Cancelled;

            return result.Verdict == EconomyVerdict.Insufficient ? TradeRefusal.Insufficient : TradeRefusal.Refused;
        }

        Status = TradeStatus.Completed;
        Changed?.Invoke(this);

        return TradeRefusal.None;
    }

    void Bump() {
        Revision++;
        leftConfirmed = -1;
        rightConfirmed = -1;
        Status = TradeStatus.Open;
        Changed?.Invoke(this);
    }
}
