// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Gameplay;
using Vixen.Gameplay.Economy;
using Vixen.Live.Persistence;

namespace Vixen.Live.Gameplay;

/// <summary>An intent that has been applied here and not yet written down.</summary>
/// <param name="Key">What makes replaying it free.</param>
/// <param name="Intent">What to write.</param>
/// <param name="Detail">What it was, for the support tool.</param>
public readonly record struct PendingWrite(IdempotencyKey Key, LedgerIntent Intent, string Detail);

/// <summary>Why the bridge would not take an intent.</summary>
public enum BridgeRefusal {
    /// <summary>It did.</summary>
    None,

    /// <summary>Nobody on this realm has that gameplay id.</summary>
    Unknown,

    /// <summary>The projection refused it — unbalanced, or somebody cannot pay.</summary>
    Refused,

    /// <summary>The realm no longer holds the lease, so nothing new may be started.</summary>
    Superseded
}

/// <summary>
///     Doc 28's economy on doc 27's ledger: applied in the frame, written down afterwards, and never
///     awaited.
/// </summary>
/// <remarks>
///     <para>
///         <b>The two sides do not have the same shape and could not.</b>
///         <see cref="IEconomyLedger" /> is synchronous because it is called from a rule, mid-frame,
///         several times per hit; <see cref="ILedger" /> is a database round trip. ADR-016's rule —
///         <em>"Orleans is asked, not awaited"</em> — makes a blocking adapter between them the one
///         implementation that is definitely wrong, and it is also the obvious one.
///     </para>
///     <para>
///         <b>So the in-memory projection is authoritative for the frame and the database is the
///         audit trail.</b> That is not optimism: ADR-021's lease says exactly one realm may write a
///         player, so the projection cannot disagree with the database <em>while the lease is
///         held</em>. Every accepted intent goes into an outbox, the realm drains it and posts it
///         through <c>RealmDirectory</c>, and the answer arrives at a later <c>PreUpdate</c>.
///     </para>
///     <para>
///         ⚠ <b>A <see cref="LedgerVerdict.Superseded" /> is not an undo.</b> ADR-021 says a realm
///         that loses its lease mid-combat <em>"keeps simulating, buffers durable mutations as ledger
///         intents, and either flushes them when the lease returns or hands them to the new
///         holder"</em>. So a superseded write stays in the outbox and is re-drained; rolling the
///         projection back would take an item off somebody who is still holding it.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="LedgerVerdict.Insufficient" /> or
///         <see cref="LedgerVerdict.Unbalanced" /> answer is a defect, not a refusal.</b> The
///         projection checked both before the intent was ever queued, so the database disagreeing
///         means the two have diverged — which is the one thing the lease exists to prevent. It is
///         counted and surfaced rather than swallowed, because a bridge that quietly dropped those
///         would be a bridge that loses items in a way nobody can reproduce.
///     </para>
///     <para>
///         ⚠ <b>Nothing here writes a gameplay <see cref="PlayerId" />.</b> Every account crosses
///         through <see cref="IGameplayIdentity" /> into a <see cref="PlayerKey" /> — see that type
///         for why a realm-scoped integer in a durable row is somebody else next week.
///     </para>
/// </remarks>
public sealed class LedgerBridge : IEconomyLedger {
    /// <summary>The world account a restored balance is transferred out of.</summary>
    /// <remarks>
    ///     ⚠ <b>Seeded from the database's <em>balances</em>, never replayed from its journal.</b> A
    ///     replay would re-run every intent since the account was made, which for a year-old
    ///     character is both slow and a second chance to get it wrong. A world account is what the
    ///     seed comes out of because the projection's own conservation must hold too, and doc 27
    ///     § Persistence already has world accounts going steadily negative on purpose.
    /// </remarks>
    public const string RestoreAccount = "world/restore";

    readonly List<PendingWrite> outbox = [];
    readonly HashSet<IdempotencyKey> inFlight = [];
    readonly IGameplayIdentity identity;
    readonly IEconomyLedger projection;

    long restored;

    /// <summary>Makes one over a projection.</summary>
    /// <param name="identity">Who a gameplay id is, durably.</param>
    /// <param name="projection">The in-frame balances. A <see cref="MemoryEconomyLedger" /> unless a game has its own.</param>
    /// <param name="leaseEpoch">The epoch this realm holds. Every write names it, so a late one is declined.</param>
    public LedgerBridge(IGameplayIdentity identity, IEconomyLedger projection, long leaseEpoch) {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(projection);

        this.identity = identity;
        this.projection = projection;
        LeaseEpoch = leaseEpoch;
    }

    /// <summary>The epoch this realm holds.</summary>
    public long LeaseEpoch { get; private set; }

    /// <summary>Whether the lease is still this realm's.</summary>
    public bool HoldsLease { get; private set; } = true;

    /// <summary>How many writes are waiting to be drained or answered.</summary>
    public int Pending => outbox.Count;

    /// <summary>How many answers said the projection and the database disagree. Never anything but zero.</summary>
    public int Divergences { get; private set; }

    /// <summary>How many posts were recognised from the outbox rather than from the projection.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A realm has two records of what it has already done, and this counts the ones the
    ///         exact record answered.</b> The outbox holds every operation started here and not yet
    ///         confirmed; the projection's key set holds every operation applied here inside its
    ///         <c>KeyHorizon</c>. Both give the right answer, and the outbox is asked first because it
    ///         cannot be wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Non-zero means one of two things and both are worth knowing.</b> Either a caller is
    ///         retrying an operation faster than the database is settling it, or the horizon is shorter
    ///         than a settle takes — and in the second case this is what stands between a forgotten key
    ///         and a purse that doubles. The database would still refuse the duplicate write, because
    ///         that is what its own key is for; what it cannot fix is that the realm's own balances
    ///         would already have moved twice.
    ///     </para>
    /// </remarks>
    public int Deduplicated { get; private set; }

    /// <summary>Why the last <see cref="Post" /> was refused.</summary>
    public BridgeRefusal LastRefusal { get; private set; }

    /// <summary>Raised when the database refuses a write the projection had accepted.</summary>
    /// <remarks>
    ///     The one event a realm must not ignore. See the type's remarks: it means the lease's
    ///     single-writer property has been broken, and no local recovery is correct.
    /// </remarks>
    public event Action<PendingWrite, LedgerResult>? Diverged;

    /// <summary>Applies an intent here and queues it to be written down.</summary>
    /// <param name="intent">What is to happen.</param>
    /// <returns>What the projection made of it.</returns>
    public EconomyResult Post(EconomyIntent intent) {
        ArgumentNullException.ThrowIfNull(intent);

        LastRefusal = BridgeRefusal.None;

        // ⚠ Checked before the projection is touched, so a realm that has lost its lease starts
        // nothing new — what it has already started stays in the outbox for whoever holds it next.
        if (!HoldsLease) {
            LastRefusal = BridgeRefusal.Superseded;

            return new(EconomyVerdict.Malformed, 0, "this realm no longer holds the lease");
        }

        if (Translate(intent) is not { } movements) {
            LastRefusal = BridgeRefusal.Unknown;

            return new(EconomyVerdict.Malformed, 0, "an account names somebody who is not on this realm");
        }

        var key = KeyOf(intent, movements);

        // ⚠ Before the projection, and the order is the whole of the guard. The outbox is an exact
        // record of every operation this realm has started and not finished, so asking it first
        // shrinks the window a KeyHorizon has to cover from "every retry there will ever be" to
        // "every retry after the write is durable" — and inside that window a horizon set too short
        // cannot double anything, whatever it says. Asking the projection first would mean the
        // movements had already been applied a second time by the time anything noticed.
        if (inFlight.Contains(key)) {
            Deduplicated++;

            return new(EconomyVerdict.Replayed, 0, "this operation is still in the outbox");
        }

        var result = projection.Post(intent);

        if (!result.Ok) {
            LastRefusal = BridgeRefusal.Refused;

            return result;
        }

        // A replay changed nothing here, so there is nothing new to write down either — and queueing
        // it would put a second copy of the same key in the outbox.
        if (result.Verdict == EconomyVerdict.Replayed) {
            return result;
        }

        outbox.Add(new(key, new() { Key = key, LeaseEpoch = LeaseEpoch, Movements = movements, Detail = intent.Detail }, intent.Detail));
        inFlight.Add(key);

        return result;
    }

    /// <inheritdoc />
    public long Balance(EconomyAccount account, DefId asset) => projection.Balance(account, asset);

    /// <summary>Puts a saved balance into the projection without queueing a write.</summary>
    /// <param name="account">Whose.</param>
    /// <param name="asset">What.</param>
    /// <param name="amount">How much. Zero and below do nothing.</param>
    /// <returns>Whether it took.</returns>
    /// <remarks>
    ///     ⚠ <b>There is no <c>Restore(…, 0)</c> that undoes this, and a realm that thinks there is
    ///     keeps every departed player's purse.</b> The mirror is <c>MemoryEconomyLedger.Release</c>,
    ///     which hands the rows back to <see cref="RestoreAccount" /> and drops them — because letting
    ///     a player go is not a movement of value and must not be written to the journal as one.
    /// </remarks>
    public bool Restore(EconomyAccount account, DefId asset, long amount) {
        if (amount <= 0) {
            return false;
        }

        // ⚠ Its own key space, and a fresh key every time. That is not idempotence and does not
        // pretend to be: two Restore calls for one account and asset seed it twice. The counter is
        // there so a *legitimate* re-seed — a reconnect onto a recycled session id, which produces
        // the same gameplay id — is not silently refused, and refusing to seed a player is worse than
        // the caller having to not ask twice.
        //
        // ⚠ Which makes every restore key ballast in the projection's guard: it can never match
        // anything, and it is kept for as long as a real key is. A realm that admits five hundred
        // players an hour adds five hundred keys an hour that guard nothing — one of the reasons
        // MemoryEconomyLedger's set needs a KeyHorizon rather than merely deserving one.
        var result = projection.Post(
            EconomyIntent.Transfer(
                $"restore/{account.Player.Value}/{asset.Value}/{++restored}",
                new(PlayerId.None, RestoreAccount),
                account,
                asset,
                amount,
                "restored from the database"
            )
        );

        return result.Ok;
    }

    /// <summary>Takes everything waiting to be written down.</summary>
    /// <returns>The writes, oldest first.</returns>
    /// <remarks>
    ///     ⚠ <b>They are not removed.</b> A drained write is in flight, not done; removing it here
    ///     would lose it when the grain call fails, and losing a ledger intent is losing an item.
    ///     <see cref="Settle" /> is what removes one.
    /// </remarks>
    public ImmutableArray<PendingWrite> Drain() => [.. outbox];

    /// <summary>Applies what the database said about a write.</summary>
    /// <param name="key">Which write.</param>
    /// <param name="result">What it said.</param>
    /// <returns>Whether that key was waiting.</returns>
    public bool Settle(IdempotencyKey key, LedgerResult result) {
        var index = outbox.FindIndex(write => write.Key == key);

        if (index < 0) {
            return false;
        }

        var write = outbox[index];

        // Applied and Replayed are both success and the caller must not tell them apart — the whole
        // point of a key derived from the operation.
        if (result.Ok) {
            outbox.RemoveAt(index);
            inFlight.Remove(key);

            return true;
        }

        // Superseded: kept, deliberately. ADR-021's buffered mutation, waiting for the lease to come
        // back or for the handoff to carry it.
        if (result.Verdict == LedgerVerdict.Superseded) {
            HoldsLease = false;

            return true;
        }

        // Anything else means the projection and the database disagree, which the lease is supposed
        // to make impossible. Kept in the outbox as evidence rather than dropped.
        Divergences++;
        Diverged?.Invoke(write, result);

        return true;
    }

    /// <summary>Says the lease came back, at a new epoch, so the outbox may be flushed again.</summary>
    /// <param name="epoch">The epoch now held.</param>
    /// <remarks>
    ///     Every waiting write is restamped, because a write naming the dead epoch would be declined
    ///     by the same fence that declined it the first time — for ever.
    /// </remarks>
    public void Renew(long epoch) {
        LeaseEpoch = epoch;
        HoldsLease = true;

        for (var index = 0; index < outbox.Count; index++) {
            outbox[index] = outbox[index] with { Intent = outbox[index].Intent with { LeaseEpoch = epoch } };
        }
    }

    /// <summary>Says the lease is gone.</summary>
    public void Supersede() => HoldsLease = false;

    ImmutableArray<AssetMovement>? Translate(EconomyIntent intent) {
        var movements = ImmutableArray.CreateBuilder<AssetMovement>(intent.Movements.Length);

        foreach (var move in intent.Movements) {
            if (Account(move.Account) is not { } account) {
                return null;
            }

            movements.Add(new(account, new(move.Asset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), move.Delta));
        }

        return movements.DrainToImmutable();
    }

    LedgerAccount? Account(EconomyAccount account) {
        if (!account.Player.IsSome) {
            return LedgerAccount.Of(account.World);
        }

        return identity.TryResolve(account.Player, out var key) ? LedgerAccount.Of(key) : null;
    }

    IdempotencyKey KeyOf(EconomyIntent intent, ImmutableArray<AssetMovement> movements) {
        // The key's player is whoever the operation is *about*, which is the first player account it
        // touches. A world-only intent belongs to nobody and gets the none key, which the ledger
        // refuses — correctly, because an operation with no owner has nothing to be idempotent per.
        foreach (var move in intent.Movements) {
            if (move.Account.Player.IsSome && identity.TryResolve(move.Account.Player, out var key)) {
                return new(key, "gameplay", intent.Key);
            }
        }

        _ = movements;

        return new(PlayerKey.None, "gameplay", intent.Key);
    }
}
