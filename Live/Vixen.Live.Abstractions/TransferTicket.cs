// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vixen.Live;

/// <summary>A player's permission to be admitted somewhere. Signed, expiring, opaque to them.</summary>
/// <remarks>
///     <para>
///         ADR-020: <c>NetworkSession</c>'s reconnect token with a different issuer. Doc 16 already
///         established server-issued, opaque, expiring tokens that let a <c>PlayerId</c> survive a
///         dropped <c>ConnectionId</c>; this is the same object minted by the orchestrator instead of
///         by the source session, naming the target shard, the player, the lease epoch and an expiry,
///         signed with a cluster key the realms hold and the client does not.
///     </para>
///     <para>
///         ⚠ <b>The client is a courier.</b> It carries the ticket from the gate (or from the realm
///         it is leaving) to the realm it is entering, and it can neither read anything it did not
///         already know nor forge one. Everything a ticket authorises is checked by the realm that
///         receives it, against a key the client has never seen.
///     </para>
///     <para>
///         <see cref="LeaseEpoch" /> is what makes a replayed ticket harmless (ADR-021): admission
///         names an epoch, and an epoch already superseded is a no-op rather than a second grant. The
///         expiry is a second, cruder bound on the same window.
///     </para>
/// </remarks>
public sealed record TransferTicket {
    /// <summary>Who it admits.</summary>
    public PlayerKey Player { get; init; }

    /// <summary>Which shard it admits them to.</summary>
    public ShardId Target { get; init; }

    /// <summary>Where that shard is, so the client needs nothing else to reach it.</summary>
    public RealmEndpoint Endpoint { get; init; }

    /// <summary>The lease epoch the receiving realm will acquire. Monotonic per player.</summary>
    public long LeaseEpoch { get; init; }

    /// <summary>When it stops being accepted.</summary>
    public DateTimeOffset Expires { get; init; }

    /// <summary>The cluster key's HMAC over everything above. Empty until signed.</summary>
    public ReadOnlyMemory<byte> Signature { get; init; }

    /// <summary>The ticket as the one string a client carries.</summary>
    /// <returns>What <see cref="TryDecode" /> reads, signature included.</returns>
    public string Encode() {
        var text = new StringBuilder(Canonical());

        KeyValueText.Write(text, "sig", Convert.ToHexStringLower(Signature.Span));

        return text.ToString();
    }

    /// <summary>Reads a ticket back, or says what was wrong with it.</summary>
    /// <param name="text">What <see cref="Encode" /> wrote.</param>
    /// <param name="ticket">The ticket, on success. <b>Unverified</b> — see
    /// <see cref="TransferTicketSigner.Validate" />.</param>
    /// <param name="error">Why not, otherwise.</param>
    /// <returns>Whether it decoded.</returns>
    /// <remarks>
    ///     ⚠ <b>Decoding is not validating.</b> This answers whether the bytes were a ticket, which
    ///     is a question about a stranger's input; whether it is <em>this cluster's</em> ticket, not
    ///     yet expired, for the shard being entered is <see cref="TransferTicketSigner.Validate" />
    ///     and is the only check that means anything.
    /// </remarks>
    public static bool TryDecode(string? text, out TransferTicket? ticket, out string error) {
        ticket = null;

        if (!KeyValueText.TryRead(text, out var fields, out error)) {
            return false;
        }

        if (!PlayerKey.TryParse(fields.GetValueOrDefault("player"), out var player) || !player.IsValid) {
            error = "`player` is missing or is not an account/character pair";

            return false;
        }

        if (!ShardId.TryParse(fields.GetValueOrDefault("target"), out var target) || !target.IsValid) {
            error = "`target` is missing or is not a guid";

            return false;
        }

        if (!RealmEndpoint.TryParse(fields.GetValueOrDefault("at"), out var endpoint) || !endpoint.IsValid) {
            error = "`at` is missing or is not a reachable endpoint";

            return false;
        }

        if (!long.TryParse(fields.GetValueOrDefault("epoch"), CultureInfo.InvariantCulture, out var epoch)) {
            error = "`epoch` is missing or is not a number";

            return false;
        }

        if (!long.TryParse(fields.GetValueOrDefault("expires"), CultureInfo.InvariantCulture, out var expires)) {
            error = "`expires` is missing or is not a unix timestamp";

            return false;
        }

        byte[] signature;

        try {
            signature = Convert.FromHexString(fields.GetValueOrDefault("sig", ""));
        } catch (FormatException) {
            error = "`sig` is not hexadecimal";

            return false;
        }

        ticket = new() {
            Player = player,
            Target = target,
            Endpoint = endpoint,
            LeaseEpoch = epoch,
            Expires = DateTimeOffset.FromUnixTimeMilliseconds(expires),
            Signature = signature
        };

        return true;
    }

    /// <summary>Everything the signature covers, in a fixed order.</summary>
    /// <returns>The canonical form, without the signature.</returns>
    /// <remarks>
    ///     Fixed order and no optional fields, because a canonical form that depends on what happened
    ///     to be set is one where two encoders of the same ticket disagree — and every such
    ///     disagreement is a valid ticket that fails to validate on one realm out of eight.
    /// </remarks>
    internal string Canonical() {
        var text = new StringBuilder(160);

        KeyValueText.Write(text, "player", Player.ToString());
        KeyValueText.Write(text, "target", Target.Value.ToString("D", CultureInfo.InvariantCulture));
        KeyValueText.Write(text, "at", Endpoint.ToString());
        KeyValueText.Write(text, "epoch", LeaseEpoch.ToString(CultureInfo.InvariantCulture));
        KeyValueText.Write(
            text,
            "expires",
            Expires.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        );

        return text.ToString();
    }

    /// <summary>Whether two tickets say the same thing, signature bytes included.</summary>
    /// <param name="other">The other ticket.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    ///     Hand-written because the synthesized one would compare <see cref="Signature" /> by
    ///     reference: two tickets decoded from the same string would be unequal, which is exactly the
    ///     comparison a replay test makes.
    /// </remarks>
    public bool Equals(TransferTicket? other) =>
        other is not null
        && Player == other.Player
        && Target == other.Target
        && Endpoint == other.Endpoint
        && LeaseEpoch == other.LeaseEpoch
        && Expires == other.Expires
        && Signature.Span.SequenceEqual(other.Signature.Span);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Player, Target, Endpoint, LeaseEpoch, Expires);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"ticket for {Player} to {Target} at epoch {LeaseEpoch}, until {Expires:u}"
        );
}

/// <summary>What a realm decided about a ticket somebody presented.</summary>
public enum TicketStatus : byte {
    /// <summary>Signed by this cluster, in date, and for this shard. Admit them.</summary>
    Valid = 0,

    /// <summary>It carries no signature at all.</summary>
    Unsigned = 1,

    /// <summary>The signature is not this cluster's. Somebody made it up.</summary>
    Forged = 2,

    /// <summary>Genuine, and too old.</summary>
    Expired = 3,

    /// <summary>Genuine, and for a different shard.</summary>
    WrongShard = 4
}

/// <summary>Mints tickets, and is the only thing that can tell a real one from a made-up one.</summary>
/// <remarks>
///     <para>
///         HMAC-SHA256 over <see cref="TransferTicket.Canonical" />, with a symmetric key every realm
///         in a cluster holds and no client ever does. Symmetric rather than a signature scheme
///         because the verifier and the issuer are inside the same trust boundary — the orchestrator
///         mints, the realms verify, and both are processes the operator runs. A public-key scheme
///         would buy the ability to verify without being able to mint, which nothing here needs and
///         which costs a key distribution story.
///     </para>
///     <para>
///         ⚠ <b>The key is a secret, and it is the whole of the security of admission.</b> Anyone
///         holding it can admit anyone to anything. It belongs in whatever the deployment already
///         uses for secrets and never in a <c>RealmSpec</c>, which is visible in a process listing.
///     </para>
/// </remarks>
public sealed class TransferTicketSigner : IDisposable {
    /// <summary>The shortest key this will accept — the hash's own output size.</summary>
    /// <remarks>
    ///     A shorter key is not a weaker configuration, it is a mistake: HMAC pads anything shorter,
    ///     so a four-character key looks like it works and is guessable. Refusing it at construction
    ///     is the only moment anybody is paying attention.
    /// </remarks>
    public const int MinimumKeyBytes = 32;

    readonly byte[] key;

    bool disposed;

    /// <summary>Holds a cluster key.</summary>
    /// <param name="clusterKey">The secret. Copied; the caller may clear theirs.</param>
    /// <exception cref="ArgumentException">The key is shorter than <see cref="MinimumKeyBytes" />.</exception>
    public TransferTicketSigner(ReadOnlySpan<byte> clusterKey) {
        if (clusterKey.Length < MinimumKeyBytes) {
            throw new ArgumentException(
                $"A cluster key must be at least {MinimumKeyBytes} bytes; this one is {clusterKey.Length}.",
                nameof(clusterKey)
            );
        }

        key = clusterKey.ToArray();
    }

    /// <summary>Signs a ticket.</summary>
    /// <param name="ticket">The unsigned ticket.</param>
    /// <returns>The same ticket with its <see cref="TransferTicket.Signature" /> filled in.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ticket" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">The signer's key has been released.</exception>
    public TransferTicket Sign(TransferTicket ticket) {
        ArgumentNullException.ThrowIfNull(ticket);
        ObjectDisposedException.ThrowIf(disposed, this);

        return ticket with { Signature = Mac(ticket) };
    }

    /// <summary>Decides whether a ticket admits its bearer to a given shard, right now.</summary>
    /// <param name="ticket">What the client presented.</param>
    /// <param name="shard">The shard being entered.</param>
    /// <param name="now">The realm's clock.</param>
    /// <returns>Why not, or <see cref="TicketStatus.Valid" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ticket" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">The signer's key has been released.</exception>
    /// <remarks>
    ///     The order is signature, then expiry, then shard, and it is deliberate: everything after
    ///     the first check is a statement about a ticket this cluster actually issued, so a forged
    ///     one learns nothing from the answer beyond "no".
    /// </remarks>
    public TicketStatus Validate(TransferTicket ticket, ShardId shard, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(ticket);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (ticket.Signature.Length == 0) {
            return TicketStatus.Unsigned;
        }

        if (!CryptographicOperations.FixedTimeEquals(ticket.Signature.Span, Mac(ticket))) {
            return TicketStatus.Forged;
        }

        if (ticket.Expires <= now) {
            return TicketStatus.Expired;
        }

        return ticket.Target == shard ? TicketStatus.Valid : TicketStatus.WrongShard;
    }

    /// <summary>Releases the key.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }

    byte[] Mac(TransferTicket ticket) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(ticket.Canonical()));
}
