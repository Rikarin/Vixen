// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay;
using NetworkPlayerId = Vixen.Net.Sessions.PlayerId;

namespace Vixen.Live.Gameplay;

/// <summary>Which durable player a gameplay rule is talking about.</summary>
/// <remarks>
///     <para>
///         <b>There are three player identities in this engine and only two of them were joined.</b>
///         <c>Vixen.Net.Sessions.PlayerId</c> is a <c>uint</c> the session assigns;
///         <c>Vixen.Live.Abstractions.PlayerKey</c> is two <see cref="Guid" />s the database is keyed
///         by; <c>Vixen.Gameplay.PlayerId</c> is a <c>ulong</c> every rule in doc 28 passes around.
///         <c>RealmPlayer</c> already joins the first and the second. Nothing joined the third, and
///         it is the one every durable write starts from.
///     </para>
///     <para>
///         <b>The join is a widening, not a hash.</b> A gameplay id <em>is</em> its session id in a
///         wider integer. Hashing a 256-bit <see cref="PlayerKey" /> into 64 bits would collide, and
///         two players who collided would write each other's inventory — so the map is a table
///         populated at admission and nothing is derived.
///     </para>
///     <para>
///         ⚠ <b>A gameplay <see cref="PlayerId" /> is realm-scoped and must never reach the
///         database.</b> Doc 28 says a <c>PlayerId</c> is *"stable for as long as a party invite or a
///         mute list has to be"*, and those two are not the same length: a party lasts a session and
///         a mute list outlives every realm the player will ever be on. So durable state is keyed by
///         <see cref="PlayerKey" />, this is where the translation happens, and a saved row carrying
///         a raw gameplay id would mean somebody else on the next realm.
///     </para>
/// </remarks>
public interface IGameplayIdentity {
    /// <summary>Who a gameplay rule's player is, durably.</summary>
    /// <param name="player">The gameplay id.</param>
    /// <param name="key">Their durable identity.</param>
    /// <returns>Whether this realm has anybody by that id.</returns>
    bool TryResolve(PlayerId player, out PlayerKey key);

    /// <summary>What a durable player is called while they are here.</summary>
    /// <param name="key">Their durable identity.</param>
    /// <returns>The gameplay id, or <see cref="PlayerId.None" /> when they are not on this realm.</returns>
    PlayerId PlayerFor(PlayerKey key);
}

/// <summary>A join a test can drive, and what a realm fills in at admission.</summary>
/// <remarks>
///     ⚠ <b>Admitting the same key twice replaces the id rather than keeping both.</b> A reconnect
///     inside the window is the same character on a new session, and leaving the old id resolvable
///     would let a stale rule write to somebody who is no longer there.
/// </remarks>
public sealed class GameplayIdentityMap : IGameplayIdentity {
    readonly Dictionary<ulong, PlayerKey> byPlayer = [];
    readonly Dictionary<PlayerKey, PlayerId> byKey = [];

    /// <summary>How many are on this realm.</summary>
    public int Count => byKey.Count;

    /// <summary>Everybody here, in id order.</summary>
    public IEnumerable<KeyValuePair<PlayerId, PlayerKey>> Players =>
        byPlayer.OrderBy(pair => pair.Key).Select(pair => new KeyValuePair<PlayerId, PlayerKey>(new(pair.Key), pair.Value));

    /// <summary>Records that somebody was let in.</summary>
    /// <param name="key">Their durable identity.</param>
    /// <param name="session">What the session calls them.</param>
    /// <returns>What gameplay calls them.</returns>
    public PlayerId Admit(PlayerKey key, NetworkPlayerId session) {
        var player = From(session);

        if (byKey.TryGetValue(key, out var already) && already != player) {
            byPlayer.Remove(already.Value);
        }

        byPlayer[player.Value] = key;
        byKey[key] = player;

        return player;
    }

    /// <summary>Forgets somebody who left.</summary>
    /// <param name="player">The gameplay id.</param>
    /// <returns>Whether they were here.</returns>
    public bool Release(PlayerId player) {
        if (!byPlayer.Remove(player.Value, out var key)) {
            return false;
        }

        // Only if it still points at the id being released: a reconnect has already moved it.
        if (byKey.TryGetValue(key, out var current) && current == player) {
            byKey.Remove(key);
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryResolve(PlayerId player, out PlayerKey key) => byPlayer.TryGetValue(player.Value, out key);

    /// <inheritdoc />
    public PlayerId PlayerFor(PlayerKey key) => byKey.GetValueOrDefault(key);

    /// <summary>Widens a session id into the one gameplay passes around.</summary>
    /// <param name="session">The session id.</param>
    /// <returns>The gameplay id.</returns>
    /// <remarks>
    ///     The whole of the conversion, and it is deliberately not a hash of anything. Zero stays
    ///     zero, which is <see cref="PlayerId.None" /> at both ends.
    /// </remarks>
    public static PlayerId From(NetworkPlayerId session) => new(session.Value);
}
