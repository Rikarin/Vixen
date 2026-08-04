// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Live.Persistence;

/// <summary>What makes a retry write nothing the second time.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Derived from the operation, never generated.</b> Doc 27 § Persistence is explicit
///         about this and it is the whole of the mechanism: a key a caller mints fresh per attempt is
///         a different key on the retry, so the second attempt is a second trade. The key has to be
///         something both attempts compute to the same value — the auction's id, the mail's id, the
///         quest's id — which means it is a fact about <em>what is being done</em> rather than about
///         <em>this call</em>.
///     </para>
///     <para>
///         The player is in the key because <see cref="Operation" /> is only unique within whatever
///         minted it, and a mail id and an auction id could collide across two subsystems that never
///         heard of each other. <see cref="Kind" /> is in it for the same reason one level up.
///     </para>
/// </remarks>
/// <param name="Player">Whose operation. The one whose lease authorises it.</param>
/// <param name="Kind">What sort — <c>trade</c>, <c>mail-claim</c>, <c>auction-settle</c>, <c>loot</c>.</param>
/// <param name="Operation">Which one, as the thing that minted it names it.</param>
public readonly record struct IdempotencyKey(PlayerKey Player, string Kind, string Operation) {
    /// <summary>What sort of operation. Null only on <c>default</c>.</summary>
    public string Kind { get; } = Kind ?? "";

    /// <summary>Which one. Null only on <c>default</c>.</summary>
    public string Operation { get; } = Operation ?? "";

    /// <summary>Whether this names an operation at all.</summary>
    public bool IsValid => Player.IsValid && !string.IsNullOrEmpty(Kind) && !string.IsNullOrEmpty(Operation);

    /// <summary>Whether two keys name the same operation.</summary>
    /// <param name="other">The other key.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(IdempotencyKey other) =>
        Player == other.Player
        && string.Equals(Kind ?? "", other.Kind ?? "", StringComparison.Ordinal)
        && string.Equals(Operation ?? "", other.Operation ?? "", StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Player, Kind ?? "", Operation ?? "");

    /// <inheritdoc />
    public override string ToString() =>
        IsValid ? string.Create(CultureInfo.InvariantCulture, $"{Player}:{Kind}:{Operation}") : "no operation";
}

/// <summary>A movement of value somebody is asking for. Applied whole or not at all.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Several movements, not one, and that is not a convenience.</b> A trade takes a sword
///         off one character and puts gold on another; a purchase moves gold to
///         <see cref="LedgerAccount.Vendor" /> and an item out of <see cref="LedgerAccount.Loot" />.
///         If those were two appends, a crash between them is a lost sword, and no amount of retrying
///         fixes a half-applied trade because the retry's idempotency key already exists.
///     </para>
///     <para>
///         <see cref="LeaseEpoch" /> is ADR-021 reaching the database: a realm may only move a
///         character's value while it holds that character's lease, and an intent naming a superseded
///         epoch is refused. Without it, a realm that lost its lease mid-combat and is still
///         simulating would keep writing — which is exactly the window the lease exists to close.
///     </para>
/// </remarks>
public sealed record LedgerIntent {
    /// <summary>What this is, and what makes the retry free.</summary>
    public IdempotencyKey Key { get; init; }

    /// <summary>The epoch the acting realm holds for <see cref="IdempotencyKey.Player" />.</summary>
    public long LeaseEpoch { get; init; }

    /// <summary>When the realm decided it. Its clock, recorded rather than trusted.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>The legs, which must sum to zero per asset.</summary>
    public ImmutableArray<AssetMovement> Movements { get; init; } = [];

    /// <summary>Why, for the support tool. Free text, and the only field nothing depends on.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Whether the legs balance — the invariant a store refuses to break.</summary>
    /// <returns>Whether every asset's deltas sum to zero.</returns>
    /// <remarks>
    ///     Checked here so that a game can assert it before offering a trade to a player, and checked
    ///     again by every store because an invariant enforced only by its callers is a convention.
    /// </remarks>
    public bool IsBalanced() {
        if (Movements.IsDefaultOrEmpty) {
            return false;
        }

        var sums = new Dictionary<AssetId, long>();

        foreach (var movement in Movements) {
            if (!movement.Account.IsValid || !movement.Asset.IsValid) {
                return false;
            }

            sums[movement.Asset] = sums.GetValueOrDefault(movement.Asset) + movement.Delta;
        }

        foreach (var sum in sums.Values) {
            if (sum != 0) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The commonest shape: one asset, from one account to another.</summary>
    /// <param name="key">What operation this is.</param>
    /// <param name="epoch">The acting realm's lease epoch.</param>
    /// <param name="at">Its clock.</param>
    /// <param name="from">Who loses it.</param>
    /// <param name="to">Who gains it.</param>
    /// <param name="asset">What.</param>
    /// <param name="quantity">How much. Must be positive.</param>
    /// <returns>The intent, balanced by construction.</returns>
    public static LedgerIntent Transfer(
        IdempotencyKey key,
        long epoch,
        DateTimeOffset at,
        LedgerAccount from,
        LedgerAccount to,
        AssetId asset,
        long quantity
    ) =>
        new() {
            Key = key,
            LeaseEpoch = epoch,
            At = at,
            Movements = [new(from, asset, -quantity), new(to, asset, quantity)]
        };

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Key} at epoch {LeaseEpoch}: {(Movements.IsDefault ? 0 : Movements.Length)} movement(s)"
        );
}

/// <summary>What a store did with an intent.</summary>
public enum LedgerVerdict : byte {
    /// <summary>Written. The balances moved.</summary>
    Applied = 0,

    /// <summary>This key has been seen. Nothing was written, and that is success.</summary>
    Replayed = 1,

    /// <summary>The legs do not sum to zero, or name nowhere. A bug in the caller.</summary>
    Unbalanced = 2,

    /// <summary>The acting realm's lease has been superseded. ADR-021's late write.</summary>
    Superseded = 3,

    /// <summary>Somebody does not have what the intent takes off them.</summary>
    Insufficient = 4
}

/// <summary>What happened, and where in the journal it landed.</summary>
/// <param name="Verdict">Applied, replayed, or why not.</param>
/// <param name="Sequence">The journal position of the first row, or zero.</param>
/// <param name="Detail">What was wrong, when something was.</param>
public readonly record struct LedgerResult(LedgerVerdict Verdict, long Sequence, string Detail = "") {
    /// <summary>Whether the caller may proceed as though it were written — including a replay.</summary>
    public bool Ok => Verdict is LedgerVerdict.Applied or LedgerVerdict.Replayed;

    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrEmpty(Detail)
            ? string.Create(CultureInfo.InvariantCulture, $"{Verdict} at #{Sequence}")
            : string.Create(CultureInfo.InvariantCulture, $"{Verdict} at #{Sequence}: {Detail}");
}

/// <summary>One row of the journal. Append-only, and never updated.</summary>
/// <param name="Sequence">Its position. Monotonic across the whole ledger.</param>
/// <param name="Key">The operation that wrote it.</param>
/// <param name="Account">Whose holding changed.</param>
/// <param name="Asset">Of what.</param>
/// <param name="Delta">By how much.</param>
/// <param name="Balance">What that account held of that asset afterwards.</param>
/// <param name="At">The acting realm's clock when it decided.</param>
/// <param name="Recorded">The store's clock when it landed. Not the same, and both matter.</param>
/// <param name="Detail">The intent's free text.</param>
public sealed record LedgerEntry(
    long Sequence,
    IdempotencyKey Key,
    LedgerAccount Account,
    AssetId Asset,
    long Delta,
    long Balance,
    DateTimeOffset At,
    DateTimeOffset Recorded,
    string Detail
) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"#{Sequence} {Account} {Delta:+#;-#;0} {Asset} → {Balance} ({Key.Kind})"
        );
}

/// <summary>Which rows the support tool wants. Every field narrows; none is required.</summary>
/// <remarks>
///     Doc 27 § Diagnostics: <i>"by player, by item, by time — the support tool"</i>. A query with
///     nothing set walks the whole journal newest-first, which is what an operator opening the tool
///     with no idea what they are looking for should get.
/// </remarks>
public sealed record LedgerQuery {
    /// <summary>Only this account's rows.</summary>
    public LedgerAccount Account { get; init; }

    /// <summary>Only this asset's.</summary>
    public AssetId Asset { get; init; }

    /// <summary>Only rows recorded at or after this.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only rows recorded strictly before this.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Only this operation's — the "what happened to my sword" query, once you have the id.</summary>
    public IdempotencyKey Operation { get; init; }

    /// <summary>At most this many, newest first.</summary>
    public int Limit { get; init; } = 200;
}

/// <summary>The journal every movement of value is written to. Doc 27 § Persistence.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Balances are a projection of this, not a second source of truth.</b> A store keeps a
///         running balance so a read is not a scan, and <see cref="ReconcileAsync" /> exists to prove
///         the two agree — which is the only thing that makes a cached balance safe to believe.
///     </para>
///     <para>
///         Every method takes a <c>CancellationToken</c> and none of them is on a frame path: this is
///         reached from a grain or a gate, never from a system body (ADR-016).
///     </para>
/// </remarks>
public interface ILedger {
    /// <summary>Applies an intent, or recognises that it already was.</summary>
    /// <param name="intent">What to move.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     ⚠ <b>A replay is success.</b> The caller cannot tell whether the first attempt reached the
    ///     database, and the whole point of the idempotency key is that it does not have to.
    /// </remarks>
    Task<LedgerResult> AppendAsync(LedgerIntent intent, CancellationToken cancellation);

    /// <summary>What an account holds of an asset.</summary>
    /// <param name="account">Whose.</param>
    /// <param name="asset">Of what.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The quantity. Zero for an account that has never held any.</returns>
    Task<long> BalanceAsync(LedgerAccount account, AssetId asset, CancellationToken cancellation);

    /// <summary>Everything an account holds.</summary>
    /// <param name="account">Whose.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Asset to quantity, omitting the zeroes.</returns>
    Task<IReadOnlyDictionary<AssetId, long>> HoldingsAsync(LedgerAccount account, CancellationToken cancellation);

    /// <summary>Reads the journal.</summary>
    /// <param name="query">What to narrow to.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The rows, newest first.</returns>
    Task<IReadOnlyList<LedgerEntry>> HistoryAsync(LedgerQuery query, CancellationToken cancellation);

    /// <summary>Checks the running balances against the journal they were derived from.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Every account and asset where the two disagree. Empty is the healthy answer.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the conservation oracle doc 27 § Testing asks for, as an operation rather
    ///     than only as a test.</b> A fleet that has been running for a month wants to be able to ask
    ///     the question the CI job asks after every randomised transfer, and the answer being cheap
    ///     to obtain is what makes it something a nightly job actually runs.
    /// </remarks>
    Task<IReadOnlyList<LedgerDiscrepancy>> ReconcileAsync(CancellationToken cancellation);
}

/// <summary>A running balance that does not match the rows behind it.</summary>
/// <param name="Account">Whose.</param>
/// <param name="Asset">Of what.</param>
/// <param name="Stored">What the balance says.</param>
/// <param name="Journalled">What the journal sums to.</param>
public readonly record struct LedgerDiscrepancy(LedgerAccount Account, AssetId Asset, long Stored, long Journalled) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Account} holds {Stored} {Asset}, journal says {Journalled}");
}
