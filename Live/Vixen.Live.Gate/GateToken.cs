// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vixen.Live.Gate;

/// <summary>A signed-in session, as a bearer token the client carries and cannot read.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a <see cref="TransferTicket" />, and the difference is which plane it belongs
///         to.</b> A ticket admits one character to one shard for about a minute and is checked by a
///         realm; this admits one account to the <em>gate</em> for hours and is checked by the gate.
///         Making them one type would mean a realm could be handed something that authorises reading
///         an account's character list, which is exactly the confusion ADR-017's assembly split
///         exists to prevent.
///     </para>
///     <para>
///         ⚠ <b>Stateless, and therefore not revocable before it expires.</b> There is no session
///         table, so signing out is a client-side forget and a compromised token works until
///         <see cref="Expires" />. That is the trade a stateless token always makes, and the bound on
///         it is the lifetime — hours rather than weeks. Suspension is checked against the account on
///         every request that matters rather than against the token, so a banned account stops being
///         able to play immediately even though its token still parses.
///     </para>
/// </remarks>
/// <param name="Account">Whose.</param>
/// <param name="Expires">When it stops being accepted.</param>
public sealed record GateToken(Guid Account, DateTimeOffset Expires) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"session for {Account:D} until {Expires:u}");
}

/// <summary>Why a token was not accepted.</summary>
public enum TokenStatus : byte {
    /// <summary>Signed by this gate and in date.</summary>
    Valid = 0,

    /// <summary>Not the shape of a token at all.</summary>
    Malformed = 1,

    /// <summary>The right shape, and not this gate's signature.</summary>
    Forged = 2,

    /// <summary>Genuine, and too old.</summary>
    Expired = 3
}

/// <summary>Mints session tokens, and is the only thing that can tell a real one from a made-up one.</summary>
/// <remarks>
///     ⚠ <b>The key is a secret and it is the whole of the security of the service plane.</b> Anyone
///     holding it can sign in as anybody. It belongs in whatever the deployment already uses for
///     secrets. Sharing it with the realms' cluster key would mean a realm could mint gate sessions;
///     they are separate keys for the same reason they are separate types.
/// </remarks>
public sealed class GateTokenSigner : IDisposable {
    /// <summary>The shortest key this accepts — the hash's own output size.</summary>
    public const int MinimumKeyBytes = 32;

    readonly byte[] key;

    bool disposed;

    /// <summary>Holds a signing key.</summary>
    /// <param name="signingKey">The secret. Copied; the caller may clear theirs.</param>
    /// <exception cref="ArgumentException">Shorter than <see cref="MinimumKeyBytes" />.</exception>
    public GateTokenSigner(ReadOnlySpan<byte> signingKey) {
        if (signingKey.Length < MinimumKeyBytes) {
            throw new ArgumentException(
                $"A gate signing key must be at least {MinimumKeyBytes} bytes; this one is {signingKey.Length}.",
                nameof(signingKey)
            );
        }

        key = signingKey.ToArray();
    }

    /// <summary>Signs a session.</summary>
    /// <param name="token">Who, and until when.</param>
    /// <returns>The string the client carries.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">The key has been released.</exception>
    public string Encode(GateToken token) {
        ArgumentNullException.ThrowIfNull(token);
        ObjectDisposedException.ThrowIf(disposed, this);

        var body = Canonical(token);

        return $"{body}.{Convert.ToHexStringLower(Mac(body))}";
    }

    /// <summary>Reads a token back, and says whether to believe it.</summary>
    /// <param name="text">What the client presented, without any <c>Bearer </c> prefix.</param>
    /// <param name="now">The gate's clock.</param>
    /// <param name="token">The session, when valid.</param>
    /// <returns>Why not, or <see cref="TokenStatus.Valid" />.</returns>
    /// <exception cref="ObjectDisposedException">The key has been released.</exception>
    /// <remarks>
    ///     Signature before expiry, as <c>TransferTicketSigner.Validate</c> does and for the same
    ///     reason: everything after the first check is a statement about a token this gate issued, so
    ///     a forged one learns nothing from the answer beyond "no".
    /// </remarks>
    public TokenStatus TryDecode(string? text, DateTimeOffset now, out GateToken? token) {
        ObjectDisposedException.ThrowIf(disposed, this);

        token = null;

        if (string.IsNullOrEmpty(text)) {
            return TokenStatus.Malformed;
        }

        var mark = text.LastIndexOf('.');

        if (mark <= 0) {
            return TokenStatus.Malformed;
        }

        var body = text[..mark];
        var parts = body.Split('.');

        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out var account)
            || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var expires)) {
            return TokenStatus.Malformed;
        }

        byte[] signature;

        try {
            signature = Convert.FromHexString(text[(mark + 1)..]);
        } catch (FormatException) {
            return TokenStatus.Malformed;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Mac(body))) {
            return TokenStatus.Forged;
        }

        var session = new GateToken(account, DateTimeOffset.FromUnixTimeMilliseconds(expires));

        if (session.Expires <= now) {
            return TokenStatus.Expired;
        }

        token = session;

        return TokenStatus.Valid;
    }

    /// <summary>Releases the key.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }

    static string Canonical(GateToken token) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{token.Account:N}.{token.Expires.ToUnixTimeMilliseconds()}"
        );

    byte[] Mac(string body) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body));
}
