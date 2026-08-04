// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Net.Sessions;

namespace Vixen.Live.Realms;

/// <summary>Somebody the realm let in.</summary>
/// <remarks>
///     The join between the two identities doc 27 keeps apart: <see cref="Key" /> is who the database
///     thinks they are and is the same on every realm they visit, <see cref="Id" /> is who this
///     session numbers them as and means nothing anywhere else.
/// </remarks>
public sealed class RealmPlayer {
    /// <summary>Who, durably.</summary>
    public PlayerKey Key { get; }

    /// <summary>Who, as this realm's session numbers them. Assigned when they finish joining.</summary>
    public PlayerId Id { get; internal set; }

    /// <summary>The lease epoch their ticket named (ADR-021).</summary>
    public long LeaseEpoch { get; }

    /// <summary>When the ticket was accepted.</summary>
    public DateTimeOffset AdmittedAt { get; }

    /// <summary>Whether they can be moved to another shard right now.</summary>
    /// <remarks>
    ///     The realm's cache of what the game's readiness predicate last answered — doc 27 § Drain.
    ///     Cached rather than asked on demand because a draining shard asks about every player on a
    ///     cadence, and "am I in a boss fight" is a question a game answers by walking its own state.
    /// </remarks>
    public TransferReadiness Readiness { get; set; } = TransferReadiness.Ready;

    internal RealmPlayer(PlayerKey key, long leaseEpoch, DateTimeOffset admittedAt) {
        Key = key;
        LeaseEpoch = leaseEpoch;
        AdmittedAt = admittedAt;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Key} as {Id}, epoch {LeaseEpoch}");
}

/// <summary>Why somebody was not let in.</summary>
public enum AdmissionRefusal : byte {
    /// <summary>They were.</summary>
    None = 0,

    /// <summary>They presented no ticket, or something that was not one.</summary>
    NoTicket = 1,

    /// <summary>The ticket did not survive <see cref="TransferTicketSigner.Validate" />.</summary>
    BadTicket = 2,

    /// <summary>The shard is at its hard cap.</summary>
    Full = 3,

    /// <summary>
    ///     The shard is draining. It takes no arrivals, which is the whole mechanism behind both
    ///     elastic consolidation and rolling upgrades.
    /// </summary>
    Draining = 4,

    /// <summary>Somebody is already here as that player.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than replaced, and the difference matters.</b> A second session for
    ///     one character is either a transfer that has not finished or an attempt at duplication;
    ///     both are cases where the safe answer is that the player stays where they already are,
    ///     which is doc 27 § Transfer's own asymmetry — every abort leaves them somewhere valid.
    /// </remarks>
    AlreadyHere = 5
}

/// <summary>The door: a ticket, a capacity check, and a name to hold the session's player against.</summary>
/// <remarks>
///     <para>
///         An <c>ISessionAuthenticator</c>, which is doc 16's existing seam and needs no new
///         mechanism: the session hands over whatever the client sent at the handshake, and this
///         decides. What arrives is an encoded <see cref="TransferTicket" />; what comes back is
///         accept-as-somebody or a named refusal.
///     </para>
///     <para>
///         ⚠ <b>Synchronous, and it has to stay that way.</b> The interface allows
///         <c>Pending</c> for an authenticator that has to ask an identity provider — and this one
///         never does, because the ticket is self-contained and the key is already here. That is the
///         property ADR-020 was designed for: admission costs an HMAC, so a transfer's second session
///         opens in the time it takes to hash a hundred bytes rather than in a round trip to the
///         orchestrator.
///     </para>
/// </remarks>
public sealed class PlayerAdmission : ISessionAuthenticator {
    readonly Dictionary<PlayerKey, RealmPlayer> admitted = [];
    readonly Dictionary<uint, RealmPlayer> byPlayerId = [];
    readonly TransferTicketSigner signer;
    readonly ShardId shard;
    readonly ShardCapacity capacity;

    /// <summary>The realm's clock, for expiry. Replaceable so a test does not have to wait.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Whether the shard is taking arrivals.</summary>
    public bool IsDraining { get; set; }

    /// <summary>How many are here.</summary>
    public int Count => admitted.Count;

    /// <summary>Everybody here.</summary>
    public IReadOnlyCollection<RealmPlayer> Players => admitted.Values;

    /// <summary>Why the last refusal happened, for the heartbeat and the log.</summary>
    public AdmissionRefusal LastRefusal { get; private set; }

    /// <summary>How many have been turned away, by reason.</summary>
    /// <remarks>
    ///     Counted rather than only logged: a shard refusing everybody because its clock disagrees
    ///     with the orchestrator's presents as "nobody can join" and is diagnosed in one glance from
    ///     a histogram of <see cref="AdmissionRefusal.BadTicket" />.
    /// </remarks>
    public IReadOnlyDictionary<AdmissionRefusal, int> Refusals => refusals;

    readonly Dictionary<AdmissionRefusal, int> refusals = [];

    /// <summary>Stands a door up.</summary>
    /// <param name="spec">The shard being entered, for the ticket's target and the capacity.</param>
    /// <param name="signer">The cluster key. Held, not owned — the host disposes it.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public PlayerAdmission(RealmSpec spec, TransferTicketSigner signer) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(signer);

        this.signer = signer;
        shard = spec.Shard;
        capacity = spec.Capacity;
    }

    /// <inheritdoc />
    public AuthenticationDecision Authenticate(in AuthenticationRequest request) {
        if (IsDraining) {
            return Refuse(AdmissionRefusal.Draining, "This shard is draining.");
        }

        if (!capacity.Admits(admitted.Count)) {
            return Refuse(AdmissionRefusal.Full, "This shard is full.");
        }

        if (!TransferTicket.TryDecode(Text(request.Payload), out var ticket, out _)) {
            return Refuse(AdmissionRefusal.NoTicket, "A ticket is required.");
        }

        var status = signer.Validate(ticket!, shard, Now());

        if (status != TicketStatus.Valid) {
            // The reason is named to the client on purpose: "expired" is something it can act on by
            // asking the gate for another, and "wrong shard" is something it can act on by asking
            // where it should have gone. Neither tells an attacker anything the ticket did not.
            return Refuse(AdmissionRefusal.BadTicket, $"That ticket is {status}.");
        }

        if (admitted.ContainsKey(ticket!.Player)) {
            return Refuse(AdmissionRefusal.AlreadyHere, "That character is already on this shard.");
        }

        admitted.Add(ticket.Player, new(ticket.Player, ticket.LeaseEpoch, Now()));
        LastRefusal = AdmissionRefusal.None;

        // The identity the session will carry. It is what Bind reads back, and it is why the two
        // identities never have to be reconciled by a lookup somewhere else.
        return AuthenticationDecision.As(ticket.Player.ToString());
    }

    /// <summary>Ties a session player to the ticket that got them in.</summary>
    /// <param name="player">The session's player, once it has joined.</param>
    /// <returns>The realm's record of them, or <see langword="null" /> if they were not admitted here.</returns>
    /// <remarks>
    ///     Called from the session's <c>PlayerJoined</c>. The identity string is the bridge, which is
    ///     the one thing <c>ISessionAuthenticator</c> hands forward — so the realm never keeps a
    ///     connection-to-character table of its own for the twenty milliseconds between the two
    ///     events.
    /// </remarks>
    public RealmPlayer? Bind(NetworkPlayer player) {
        ArgumentNullException.ThrowIfNull(player);

        if (!PlayerKey.TryParse(player.Identity, out var key) || !admitted.TryGetValue(key, out var realmPlayer)) {
            return null;
        }

        realmPlayer.Id = player.Id;
        byPlayerId[player.Id.Value] = realmPlayer;

        return realmPlayer;
    }

    /// <summary>Forgets somebody who has left.</summary>
    /// <param name="id">Who, as the session numbered them.</param>
    /// <returns>The record, or <see langword="null" /> if they were not here.</returns>
    public RealmPlayer? Release(PlayerId id) {
        if (!byPlayerId.Remove(id.Value, out var player)) {
            return null;
        }

        admitted.Remove(player.Key);

        return player;
    }

    /// <summary>Finds somebody by their session identity.</summary>
    /// <param name="id">Who, as the session numbered them.</param>
    /// <param name="player">Them, on success.</param>
    /// <returns>Whether they are here.</returns>
    public bool TryGet(PlayerId id, out RealmPlayer? player) => byPlayerId.TryGetValue(id.Value, out player);

    /// <summary>Finds somebody by who they durably are.</summary>
    /// <param name="key">Which character.</param>
    /// <param name="player">Them, on success.</param>
    /// <returns>Whether they are here.</returns>
    public bool TryGet(PlayerKey key, out RealmPlayer? player) => admitted.TryGetValue(key, out player);

    AuthenticationDecision Refuse(AdmissionRefusal refusal, string reason) {
        LastRefusal = refusal;
        refusals[refusal] = refusals.GetValueOrDefault(refusal) + 1;

        return AuthenticationDecision.Refuse(reason);
    }

    static string Text(ReadOnlySpan<byte> payload) {
        // A handshake payload is a stranger's bytes. Anything that is not UTF-8 decodes to
        // replacement characters and fails to parse as a ticket, which is the same refusal as an
        // empty payload and one fewer exception path than validating the encoding separately.
        try {
            return Encoding.UTF8.GetString(payload);
        } catch (ArgumentException) {
            return "";
        }
    }
}
