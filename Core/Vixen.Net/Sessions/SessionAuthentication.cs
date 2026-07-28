// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Sessions;

/// <summary>What a connection is asking to be let in with.</summary>
public readonly ref struct AuthenticationRequest {
    /// <summary>The connection it arrived on.</summary>
    public ConnectionId Connection { get; }

    /// <summary>
    ///     Whatever the client sent — a platform ticket, a JWT, a lobby code. Opaque here on purpose:
    ///     the session does not know what your identity provider is and should not learn.
    /// </summary>
    public ReadOnlySpan<byte> Payload { get; }

    /// <summary>
    ///     Whether the client also presented a reconnect token the server recognised. An
    ///     authenticator may want to skip a round trip to its identity provider for those.
    /// </summary>
    public bool IsReconnect { get; }

    /// <summary>Creates a request.</summary>
    /// <param name="connection">The connection it arrived on.</param>
    /// <param name="payload">What the client sent.</param>
    /// <param name="isReconnect">Whether a recognised reconnect token came with it.</param>
    public AuthenticationRequest(ConnectionId connection, ReadOnlySpan<byte> payload, bool isReconnect) {
        Connection = connection;
        Payload = payload;
        IsReconnect = isReconnect;
    }
}

/// <summary>What an authenticator decided, or that it has not decided yet.</summary>
public enum AuthenticationOutcome : byte {
    /// <summary>Still working — ask again next tick.</summary>
    Pending = 0,

    /// <summary>Let them in.</summary>
    Accept = 1,

    /// <summary>Do not.</summary>
    Reject = 2
}

/// <summary>An authenticator's answer.</summary>
/// <param name="Outcome">Accept, reject, or not yet.</param>
/// <param name="Identity">
///     Who the server should consider them to be, on <see cref="AuthenticationOutcome.Accept" />.
/// </param>
/// <param name="Reason">Why not, on <see cref="AuthenticationOutcome.Reject" />. Sent to the client.</param>
public readonly record struct AuthenticationDecision(
    AuthenticationOutcome Outcome,
    string Identity,
    string Reason
) {
    /// <summary>Let them in, as nobody in particular.</summary>
    public static AuthenticationDecision Accept { get; } = new(AuthenticationOutcome.Accept, "", "");

    /// <summary>Not decided yet. The session will ask again next tick, until it times out.</summary>
    public static AuthenticationDecision Pending { get; } = new(AuthenticationOutcome.Pending, "", "");

    /// <summary>Let them in as somebody.</summary>
    /// <param name="identity">Who the server should consider them to be.</param>
    /// <returns>The decision.</returns>
    public static AuthenticationDecision As(string identity) => new(AuthenticationOutcome.Accept, identity, "");

    /// <summary>Refuse them.</summary>
    /// <param name="reason">Why, which the client is told.</param>
    /// <returns>The decision.</returns>
    public static AuthenticationDecision Refuse(string reason) => new(AuthenticationOutcome.Reject, "", reason);
}

/// <summary>Decides who is allowed in.</summary>
/// <remarks>
///     <para>
///         <b>Asked repeatedly rather than awaited.</b> Real authentication talks to an identity
///         provider over the network and cannot answer inside the frame it was asked in, so the hook
///         is allowed to say <see cref="AuthenticationOutcome.Pending" /> and be asked again on the
///         next session update. An implementation kicks its own request off on the first call and
///         reports the answer whenever it has one.
///     </para>
///     <para>
///         That is a polling interface where the obvious design is <c>Task&lt;bool&gt;</c>, and the
///         reason is the frame loop: a completion arriving on a thread pool thread halfway through a
///         frame would have to be marshalled back to the session anyway, and every layer that
///         touches it would then have to be thread-safe for the sake of an event that happens twice a
///         minute. Pending-and-ask-again keeps the session single-threaded and keeps the waiting
///         visible in <see cref="SessionOptions.AuthenticationTimeout" />.
///     </para>
/// </remarks>
public interface ISessionAuthenticator {
    /// <summary>Decides, or says it is still deciding.</summary>
    /// <param name="request">Who is asking, and with what.</param>
    /// <returns>The decision.</returns>
    AuthenticationDecision Authenticate(in AuthenticationRequest request);
}
