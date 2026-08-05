// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Economy;

/// <summary>One side of a movement: a player, or a place in the world that is not one.</summary>
/// <remarks>
///     <para>
///         <b>The gameplay-side mirror of doc 27's <c>LedgerAccount</c>, and it has to be a mirror
///         rather than the thing itself</b> — the ledger is <c>Live/</c>'s and doc 28's spine says
///         <c>Gameplay/</c> may not reference it. So this library says what must move and the realm
///         translates, which is task #27's bridge and the same shape <c>IPityStore</c> has.
///     </para>
///     <para>
///         ⚠ <b>Exactly one of the two, which is what makes a movement checkable.</b> An account that
///         was both a player and a world sink is one a balanced intent could route round; an account
///         that was neither is a movement into nowhere that still balances.
///     </para>
/// </remarks>
/// <param name="Player">Whose it is, or <see cref="PlayerId.None" />.</param>
/// <param name="World">What it is called, for an account that is not a player's.</param>
public readonly record struct EconomyAccount(PlayerId Player, string World) {
    /// <summary>Where a vendor's goods and takings come from and go.</summary>
    public const string Vendor = "world/vendor";

    /// <summary>Where currency goes to be destroyed. The thing an economy needs most of.</summary>
    public const string Sink = "world/sink";

    /// <summary>Where a trade's goods sit while both parties are deciding.</summary>
    public const string Escrow = "world/escrow";

    /// <summary>Where an unclaimed attachment waits.</summary>
    public const string Mail = "world/mail";

    /// <summary>No account.</summary>
    public static EconomyAccount Nowhere => default;

    /// <summary>Whether it names exactly one thing.</summary>
    public bool IsValid => Player.IsSome ^ !string.IsNullOrEmpty(World);

    /// <summary>Whether it is somebody's.</summary>
    public bool IsPlayer => Player.IsSome;

    /// <summary>A player's account.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The account.</returns>
    public static EconomyAccount Of(PlayerId player) => new(player, string.Empty);

    /// <summary>A world account.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The account.</returns>
    public static EconomyAccount Of(string name) => new(PlayerId.None, name ?? string.Empty);

    /// <inheritdoc />
    public override string ToString() =>
        IsPlayer ? $"player/{Player}" : string.IsNullOrEmpty(World) ? "nowhere" : World;
}

/// <summary>One line of an intent: this much of this, into or out of this account.</summary>
/// <param name="Account">Whose.</param>
/// <param name="Asset">What. An item's address or a currency's, hashed — the wire already carries these.</param>
/// <param name="Delta">How much. Negative leaves the account.</param>
public readonly record struct AssetMove(EconomyAccount Account, DefId Asset, long Delta);

/// <summary>How an intent was received.</summary>
public enum EconomyVerdict {
    /// <summary>It happened.</summary>
    Applied,

    /// <summary>It had already happened, and nothing happened twice.</summary>
    Replayed,

    /// <summary>Its movements do not sum to zero for some asset.</summary>
    Unbalanced,

    /// <summary>A player's account does not hold what it is being asked to give up.</summary>
    Insufficient,

    /// <summary>It names an account that is not one, or has no movements, or has no key.</summary>
    Malformed
}

/// <summary>What came of posting an intent.</summary>
/// <param name="Verdict">How it was received.</param>
/// <param name="Sequence">Where it landed, or the sequence it had the first time.</param>
/// <param name="Detail">What was wrong, in a sentence.</param>
public readonly record struct EconomyResult(EconomyVerdict Verdict, long Sequence, string Detail = "") {
    /// <summary>Whether the world is now in the state the intent asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>True for a replay, which is the whole point of the key.</b> A caller retrying a mail
    ///     claim wants "the mail is claimed", and "it was already claimed by this same operation" is
    ///     that answer rather than a failure to handle.
    /// </remarks>
    public bool Ok => Verdict is EconomyVerdict.Applied or EconomyVerdict.Replayed;
}

/// <summary>An indivisible set of movements, named by a key that makes replaying it free.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28: "every one of those is a ledger transaction with an idempotency key".</b> A
///         duplicated auction settlement, a retried mail claim and a trade confirmation that arrives
///         twice are all no-ops the second time by construction, not by a check somebody remembered.
///     </para>
///     <para>
///         ⚠ <b>Balanced per <em>asset</em>, not overall.</b> Summing every delta together would let
///         a hundred gold leaving one account be paid for by a hundred ore arriving in another, which
///         is a duplication bug with a receipt.
///     </para>
/// </remarks>
public sealed class EconomyIntent {
    readonly AssetMove[] movements;

    /// <summary>Makes one.</summary>
    /// <param name="key">What makes replaying it free. Two intents with one key are one intent.</param>
    /// <param name="movements">What moves.</param>
    /// <param name="detail">What it was, for an audit trail.</param>
    public EconomyIntent(string key, IEnumerable<AssetMove> movements, string detail = "") {
        ArgumentNullException.ThrowIfNull(movements);

        Key = key ?? string.Empty;
        this.movements = [.. movements];
        Detail = detail ?? string.Empty;
    }

    /// <summary>What makes replaying it free.</summary>
    public string Key { get; }

    /// <summary>What moves.</summary>
    public ReadOnlySpan<AssetMove> Movements => movements;

    /// <summary>What it was.</summary>
    public string Detail { get; }

    /// <summary>Whether every asset's movements sum to zero.</summary>
    /// <returns>Whether they do.</returns>
    public bool IsBalanced() {
        var sums = new Dictionary<uint, long>();

        foreach (var move in movements) {
            sums[move.Asset.Value] = sums.GetValueOrDefault(move.Asset.Value) + move.Delta;
        }

        foreach (var sum in sums.Values) {
            if (sum != 0) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A one-line transfer, which is most of what an economy does.</summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="from">Who is giving.</param>
    /// <param name="to">Who is receiving.</param>
    /// <param name="asset">What.</param>
    /// <param name="amount">How much. Must be positive.</param>
    /// <param name="detail">What it was.</param>
    /// <returns>The intent.</returns>
    public static EconomyIntent Transfer(
        string key,
        EconomyAccount from,
        EconomyAccount to,
        DefId asset,
        long amount,
        string detail = ""
    ) =>
        new(key, [new(from, asset, -amount), new(to, asset, amount)], detail);
}

/// <summary>Where movements are recorded, and the only thing that decides whether they happened.</summary>
/// <remarks>
///     ⚠ <b>Synchronous, unlike doc 27's <c>ILedger</c>, and that is deliberate.</b> A realm's rule
///     runs in a frame and cannot await a database; the realm's implementation of this either fronts
///     the durable ledger with an in-memory view it reconciles, or refuses and retries. Making the
///     gameplay-side call awaitable would put the frame's timing at the mercy of a round trip, which
///     is the thing doc 27 built the lease epoch to avoid needing.
/// </remarks>
public interface IEconomyLedger {
    /// <summary>Records an intent, or says why not.</summary>
    /// <param name="intent">What is to happen.</param>
    /// <returns>What came of it.</returns>
    EconomyResult Post(EconomyIntent intent);

    /// <summary>What an account holds of something.</summary>
    /// <param name="account">Whose.</param>
    /// <param name="asset">What.</param>
    /// <returns>How much.</returns>
    long Balance(EconomyAccount account, DefId asset);
}

/// <summary>An <see cref="IEconomyLedger" /> in memory. For tests, and for a single-process game.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Built with <see cref="KeyHorizon.Never" />, so a long-running realm leaks until it
///         says otherwise.</b> Every applied key is kept for ever by default, which over a week of
///         uptime is every key of that week — <c>Samples/14-Mmo</c>'s soak measures it at about a
///         megabyte a minute. Pass a <see cref="KeyHorizon" /> and call <see cref="Forget" /> off the
///         frame path; see that type for why the default is the leak rather than a chosen number.
///     </para>
///     <para>
///         ⚠ <b>A player's account may not go negative and a world account may.</b> That asymmetry
///         is what makes a world account a <em>source</em> or a <em>sink</em>: a vendor's stock comes
///         from somewhere and a fee goes nowhere, and modelling either with a real balance would mean
///         seeding the world with every coin it will ever mint. A player who cannot pay is refused,
///         which is the check the whole thing exists for.
///     </para>
/// </remarks>
public sealed class MemoryEconomyLedger : IEconomyLedger {
    readonly Dictionary<(EconomyAccount Account, uint Asset), long> balances = [];
    readonly Dictionary<string, long> applied = new(StringComparer.Ordinal);

    /// <summary>The keys of each generation, so a sweep drops a share rather than scanning.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty for an unbounded horizon, and not merely unused.</b> A ledger nobody sweeps must
    ///     not pay a second reference per key to keep a list it will never read.
    /// </remarks>
    readonly List<string>[] generations;

    long sequence;
    long forgotten;
    int generation;
    DateTimeOffset opened;
    bool swept;

    /// <summary>One that forgets nothing.</summary>
    public MemoryEconomyLedger() : this(KeyHorizon.Never) { }

    /// <summary>One that forgets a key once no retry can still carry it.</summary>
    /// <param name="horizon">How long a key is kept. See the type for why there is no default.</param>
    public MemoryEconomyLedger(KeyHorizon horizon) {
        Horizon = horizon;
        generations = horizon.IsBounded ? new List<string>[KeyHorizon.Buckets] : [];

        for (var index = 0; index < generations.Length; index++) {
            generations[index] = [];
        }
    }

    /// <summary>How long an applied key is remembered.</summary>
    public KeyHorizon Horizon { get; }

    /// <summary>How many intents have been applied, not counting replays.</summary>
    public long Applied => sequence;

    /// <summary>How many distinct keys it is holding.</summary>
    public int Keys => applied.Count;

    /// <summary>How many keys have been dropped past the horizon.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth reporting even though it is never alarming on its own.</b> A shard whose
    ///     <see cref="Keys" /> is flat and whose <see cref="Forgotten" /> is zero is one whose sweep
    ///     is not being called — which looks exactly like a healthy shard until the memory graph does
    ///     not come back down.
    /// </remarks>
    public long Forgotten => forgotten;

    /// <inheritdoc />
    public long Balance(EconomyAccount account, DefId asset) =>
        balances.GetValueOrDefault((account, asset.Value));

    /// <summary>Everything an account holds, in address order of the asset id.</summary>
    /// <param name="account">Whose.</param>
    /// <returns>The holdings.</returns>
    public IEnumerable<(DefId Asset, long Amount)> Holdings(EconomyAccount account) =>
        balances
            .Where(entry => entry.Key.Account == account && entry.Value != 0)
            .Select(entry => (new DefId(entry.Key.Asset), entry.Value))
            .OrderBy(entry => entry.Item1.Value);

    /// <summary>What every account holds of one asset, added up.</summary>
    /// <param name="asset">What.</param>
    /// <returns>The total, which conservation says never changes.</returns>
    public long Total(DefId asset) {
        var total = 0L;

        foreach (var entry in balances) {
            if (entry.Key.Asset == asset.Value) {
                total += entry.Value;
            }
        }

        return total;
    }

    /// <summary>Hands everything an account holds to another, and stops keeping rows for it.</summary>
    /// <param name="account">Whose view is being dropped — a player who has left this realm.</param>
    /// <param name="into">Where the balances go. The world account the realm seeded them out of.</param>
    /// <returns>How many rows were dropped.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The mirror of a realm seeding a player's balances when they arrive, and a realm that
    ///         does not do it holds every departed player's purse for ever.</b> Five hundred players
    ///         travelling between shards is a row per player per asset per arrival, none of which any
    ///         query will ever name again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not an <see cref="EconomyIntent" />, and it must not be one.</b> Nothing has moved:
    ///         the player still owns what they left with, and the realm that admits them next will seed
    ///         it from the database. An intent would put a key in the journal saying value changed
    ///         hands, which is a lie an auditor would later have to explain — and two realms writing
    ///         one for the same handover would make the ledger disagree with itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The balances are moved rather than deleted, which is why this takes a
    ///         destination.</b> Deleting the rows would leave whatever seeded them holding the negative
    ///         of a purse nothing balances, and <see cref="Total" /> — the ledger's own arithmetic
    ///         check — would stop summing to zero. A leak fixed by breaking the invariant that finds
    ///         duplication bugs is a bad trade.
    ///     </para>
    /// </remarks>
    public int Release(EconomyAccount account, EconomyAccount into) {
        if (!account.IsValid || !into.IsValid || account == into) {
            return 0;
        }

        var dropped = 0;

        foreach (var (asset, amount) in Holdings(account).ToArray()) {
            balances.Remove((account, asset.Value));
            balances[(into, asset.Value)] = Balance(into, asset) + amount;
            dropped++;
        }

        // A row that has already been spent to nothing is still a row, and a realm that only dropped
        // the non-zero ones would leak one per player who left with an empty purse.
        foreach (var key in balances.Keys.Where(entry => entry.Account == account).ToArray()) {
            balances.Remove(key);
            dropped++;
        }

        return dropped;
    }

    /// <inheritdoc />
    public EconomyResult Post(EconomyIntent intent) {
        ArgumentNullException.ThrowIfNull(intent);

        if (intent.Key.Length == 0 || intent.Movements.Length == 0) {
            return new(EconomyVerdict.Malformed, 0, "An intent needs a key and at least one movement.");
        }

        if (applied.TryGetValue(intent.Key, out var already)) {
            return new(EconomyVerdict.Replayed, already);
        }

        foreach (ref readonly var move in intent.Movements) {
            if (!move.Account.IsValid) {
                return new(EconomyVerdict.Malformed, 0, $"'{move.Account}' is not an account.");
            }
        }

        if (!intent.IsBalanced()) {
            return new(EconomyVerdict.Unbalanced, 0, "The movements do not sum to zero for every asset.");
        }

        // Checked against the *whole* intent before any of it is written, because an intent whose
        // third movement overdraws must leave the first two undone — the same all-or-nothing rule the
        // container algebra has, and for the same reason.
        var after = new Dictionary<(EconomyAccount, uint), long>();

        foreach (ref readonly var move in intent.Movements) {
            var key = (move.Account, move.Asset.Value);

            after[key] = after.TryGetValue(key, out var running)
                ? running + move.Delta
                : Balance(move.Account, move.Asset) + move.Delta;
        }

        foreach (var (key, value) in after) {
            if (key.Item1.IsPlayer && value < 0) {
                return new(
                    EconomyVerdict.Insufficient,
                    0,
                    $"{key.Item1} would be left holding {value} of {new DefId(key.Item2)}."
                );
            }
        }

        foreach (var (key, value) in after) {
            balances[key] = value;
        }

        applied.Add(intent.Key, ++sequence);

        if (generations.Length > 0) {
            generations[generation].Add(intent.Key);
        }

        return new(EconomyVerdict.Applied, sequence);
    }

    /// <summary>Drops every key that is older than the horizon guarantees.</summary>
    /// <param name="now">The caller's clock.</param>
    /// <returns>How many keys were dropped.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Called off the frame path, on whatever cadence a realm already has for housekeeping.</b>
    ///         It costs one generation's worth of dictionary removals when a generation is due and a
    ///         subtraction when one is not, so calling it every tick is cheap and calling it every
    ///         minute is equally correct — the horizon is measured in <paramref name="now" /> and not
    ///         in calls.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is forgotten until this is called, which is deliberate.</b> Sweeping
    ///         lazily inside <see cref="Post" /> would put the cost on a rule mid-frame, and — worse —
    ///         would make a quiet realm's keys expire on the next busy one's clock. A realm that never
    ///         calls this leaks, and leaking is the failure this type is willing to have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A clock that jumps forward past the whole horizon empties the set rather than
    ///         catching up one generation per call.</b> Both are correct — everything held is older
    ///         than the horizon either way — and the difference is that the second spends the next
    ///         eight calls doing it, which is a stall a host that paused the process for an hour does
    ///         not need on top of the pause. A clock that jumps <em>backwards</em> forgets nothing
    ///         until it has caught up, which is the safe direction.
    ///     </para>
    /// </remarks>
    public int Forget(DateTimeOffset now) {
        if (generations.Length == 0) {
            return 0;
        }

        // The first call is what starts the clock. Taking a construction-time stamp instead would
        // mean a ledger built at boot and first swept an hour later drops its whole first hour.
        if (!swept) {
            swept = true;
            opened = now;

            return 0;
        }

        var dropped = 0;
        var rotations = 0;

        while (now - opened >= Horizon.Interval && rotations < generations.Length) {
            generation = (generation + 1) % generations.Length;

            var stale = generations[generation];

            foreach (var key in stale) {
                if (applied.Remove(key)) {
                    dropped++;
                }
            }

            stale.Clear();
            opened += Horizon.Interval;
            rotations++;
        }

        if (rotations == generations.Length) {
            opened = now;
        }

        forgotten += dropped;

        return dropped;
    }
}
