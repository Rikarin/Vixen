// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Persistence;

/// <summary>Who somebody is to this service. <b>Not how they proved it.</b></summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no password field here, and there is not going to be one.</b> A game engine
///         that shipped a credential store would be shipping a liability its authors do not operate:
///         hashing parameters that age, breach response, password reset, multi-factor, account
///         recovery — every one of which is a product decision and a legal one. What the gate needs
///         is the answer to <i>"which account is this request for"</i>, and that answer comes from
///         whatever the deployment already trusts: an OIDC provider, Steam, EOS, a platform SDK, or
///         the game's own existing account service.
///     </para>
///     <para>
///         So <see cref="Handle" /> is the identity that authority hands back, and this table maps it
///         to the account the world knows. The seam is <c>IAccountAuthority</c> in
///         <c>Vixen.Live.Gate</c>. This is the same position doc 16 took on Steam and EOS transports
///         and doc 27 M-Q1 restated: the engine ships the seam and one honest development
///         implementation, not the integration.
///     </para>
/// </remarks>
/// <param name="Id">The account. What <see cref="PlayerKey.Account" /> holds.</param>
/// <param name="Handle">What the authority calls them. Unique, and opaque to this layer.</param>
/// <param name="Created">When the account first appeared.</param>
/// <param name="Suspended">Whether the gate should refuse them, and everything downstream too.</param>
public sealed record AccountRecord(Guid Id, string Handle, DateTimeOffset Created, bool Suspended) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Handle} ({Id:D}){(Suspended ? ", suspended" : "")}");
}

/// <summary>One character: who they are, where they were, and the fence on writing them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing that is a <em>quantity of an asset</em> belongs in <see cref="Profile" />.</b>
///         Inventory and currency are balances, balances are a projection of <see cref="ILedger" />,
///         and putting a gold count here as well would be two numbers that mean one thing and drift.
///         The rule that decides: if the support tool would ever be asked <i>where did this come
///         from</i>, it is a ledger asset. If the answer is only ever <i>the player chose it</i> —
///         appearance, keybinds, quest flags, the position they logged out at — it is profile.
///     </para>
///     <para>
///         <see cref="Profile" /> is an opaque blob because its schema is doc 28's and the game's,
///         and a column per gameplay concept would make every content change a migration of this
///         assembly. That is the trade named in doc 27 § Persistence's third rule from the other
///         direction: grain state is not gameplay, and gameplay whose shape only the game knows is
///         not this layer's schema.
///     </para>
/// </remarks>
/// <param name="Key">Account and character. The key everything else is joined on.</param>
/// <param name="Name">The character's name, as other players see it.</param>
/// <param name="Created">When they were made.</param>
/// <param name="LastSeen">When a realm last held their lease.</param>
/// <param name="Region">Their latency zone — doc 27 M-Q5's opaque string.</param>
/// <param name="HomeMap">The map address they log in on. Where they were when they left.</param>
/// <param name="LeaseEpoch">The epoch of the last write this row accepted. The fence.</param>
/// <param name="Profile">The game's own state. Opaque here, and never queried by this layer.</param>
public sealed record PlayerRecord(
    PlayerKey Key,
    string Name,
    DateTimeOffset Created,
    DateTimeOffset LastSeen,
    string Region,
    string HomeMap,
    long LeaseEpoch,
    ReadOnlyMemory<byte> Profile
) {
    /// <summary>Whether two rows say the same thing, profile bytes included.</summary>
    /// <param name="other">The other row.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    ///     Hand-written for the reason <c>TransferTicket.Equals</c> is: the synthesized one compares
    ///     <see cref="Profile" /> by reference, so two reads of one row would be unequal.
    /// </remarks>
    public bool Equals(PlayerRecord? other) =>
        other is not null
        && Key == other.Key
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Created == other.Created
        && LastSeen == other.LastSeen
        && string.Equals(Region, other.Region, StringComparison.Ordinal)
        && string.Equals(HomeMap, other.HomeMap, StringComparison.Ordinal)
        && LeaseEpoch == other.LeaseEpoch
        && Profile.Span.SequenceEqual(other.Profile.Span);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Name, Created, LastSeen, Region, HomeMap, LeaseEpoch);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} ({Key}) on {HomeMap} at epoch {LeaseEpoch}");
}

/// <summary>What a repository did with a write.</summary>
public enum WriteOutcome : byte {
    /// <summary>It landed.</summary>
    Written = 0,

    /// <summary>A newer lease has written since. ADR-021's late write, and not an error.</summary>
    Superseded = 1,

    /// <summary>There is no such row.</summary>
    Missing = 2,

    /// <summary>Something unique is already taken — a handle, a character name.</summary>
    Taken = 3
}

/// <summary>Accounts, and the characters on them.</summary>
public interface IAccountRepository {
    /// <summary>Reads an account.</summary>
    /// <param name="id">Which.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The account, or null.</returns>
    Task<AccountRecord?> ReadAsync(Guid id, CancellationToken cancellation);

    /// <summary>Finds the account an authority's handle maps to.</summary>
    /// <param name="handle">What the authority calls them.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The account, or null if this handle has never been seen.</returns>
    Task<AccountRecord?> ByHandleAsync(string handle, CancellationToken cancellation);

    /// <summary>Makes an account for a handle that has none.</summary>
    /// <param name="handle">What the authority calls them.</param>
    /// <param name="now">The gate's clock.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The account, and whether it was made or already existed.</returns>
    /// <remarks>
    ///     ⚠ <b>Returns the existing account rather than failing.</b> First login and every login
    ///     after it are the same call, and two gates racing a first login must not produce two
    ///     accounts for one person — which is why this is one round trip that the store makes atomic
    ///     rather than a read followed by an insert.
    /// </remarks>
    Task<(AccountRecord Account, bool Created)> EnsureAsync(
        string handle,
        DateTimeOffset now,
        CancellationToken cancellation
    );

    /// <summary>Suspends an account, or lifts it.</summary>
    /// <param name="id">Which.</param>
    /// <param name="suspended">Whether they are.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Whether the row was there.</returns>
    Task<WriteOutcome> SetSuspendedAsync(Guid id, bool suspended, CancellationToken cancellation);
}

/// <summary>Characters. The single-writer rule of doc 27 § Persistence, made a return value.</summary>
public interface IPlayerRepository {
    /// <summary>Reads a character.</summary>
    /// <param name="key">Which.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The row, or null.</returns>
    Task<PlayerRecord?> ReadAsync(PlayerKey key, CancellationToken cancellation);

    /// <summary>Every character on an account — the list the gate serves at character select.</summary>
    /// <param name="account">Whose.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The rows, oldest first.</returns>
    Task<IReadOnlyList<PlayerRecord>> ForAccountAsync(Guid account, CancellationToken cancellation);

    /// <summary>Makes a character.</summary>
    /// <param name="record">The row. Its <see cref="PlayerRecord.LeaseEpoch" /> starts the fence.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns><see cref="WriteOutcome.Taken" /> if the key or the name is already used.</returns>
    Task<WriteOutcome> CreateAsync(PlayerRecord record, CancellationToken cancellation);

    /// <summary>Writes a character, if the writer still holds the lease.</summary>
    /// <param name="record">The row, carrying the epoch it is written under.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>
    ///     <see cref="WriteOutcome.Superseded" /> when a higher epoch has already written, which is
    ///     the ordinary end of a transfer rather than a failure.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The fence is monotonic and is raised by use.</b> A write at epoch <i>n</i> refuses
    ///     every later write below <i>n</i>, forever. That is what makes a realm which lost its lease
    ///     mid-combat harmless: it keeps simulating, its buffered writes arrive late, and the
    ///     database declines them without anybody having to notice in time.
    /// </remarks>
    Task<WriteOutcome> WriteAsync(PlayerRecord record, CancellationToken cancellation);

    /// <summary>The epoch this character's row will accept a write at or above.</summary>
    /// <param name="key">Which character.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The fence. Zero for a character that has never been written.</returns>
    Task<long> FenceAsync(PlayerKey key, CancellationToken cancellation);
}

/// <summary>Everything behind one connection, so a caller composes one object rather than three.</summary>
/// <remarks>
///     ⚠ <b>The three are one store, and the ledger's fence is why.</b> An append names the acting
///     realm's lease epoch and the fence it is checked against is the character's row — so a
///     deployment that put the journal in one database and the characters in another would have no
///     way to make the check and the write one transaction. Handing them out together is the
///     interface admitting what the implementations already require.
/// </remarks>
public interface IPersistence : IAsyncDisposable {
    /// <summary>Accounts.</summary>
    IAccountRepository Accounts { get; }

    /// <summary>Characters.</summary>
    IPlayerRepository Players { get; }

    /// <summary>The journal.</summary>
    ILedger Ledger { get; }

    /// <summary>Brings the schema up to date, and says nothing if it already is.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The version it is now at.</returns>
    Task<int> MigrateAsync(CancellationToken cancellation);
}
