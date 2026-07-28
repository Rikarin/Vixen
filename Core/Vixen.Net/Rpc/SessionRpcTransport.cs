// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Sessions;

namespace Vixen.Net.Rpc;

/// <summary>Sends a router's calls through a session.</summary>
/// <remarks>
///     <para>
///         The join between the two halves, and the reason it is a class of its own rather than the
///         session implementing <see cref="IRpcTransport" /> directly: a call has to be marked as a
///         call before it goes into the session's opaque payload space, or the receiver cannot tell
///         it from a snapshot or from something the game sent. Wiring the session up as a transport
///         without that would be a connection that looked right and mixed three streams together.
///     </para>
///     <para>
///         The mirror of it is one line on the receiving side: unwrap, and hand a
///         <see cref="PayloadKind.Rpc" /> to <see cref="RpcRouter.Receive" />.
///     </para>
/// </remarks>
public sealed class SessionRpcTransport : IRpcTransport {
    readonly NetworkSession session;
    readonly byte[] buffer;

    /// <summary>The session calls go through.</summary>
    public NetworkSession Session => session;

    /// <summary>Calls that did not fit in a packet and were not sent.</summary>
    public long DroppedCount { get; private set; }

    /// <summary>Wraps a session.</summary>
    /// <param name="session">The session.</param>
    public SessionRpcTransport(NetworkSession session) {
        ArgumentNullException.ThrowIfNull(session);

        this.session = session;
        buffer = new byte[session.Transport.Capabilities.MaxPayloadBytes];
    }

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) {
        if (TryMark(payload, out var marked)) {
            session.SendToServer(marked, channel);
        }
    }

    /// <inheritdoc />
    public void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) {
        if (TryMark(payload, out var marked)) {
            session.SendToPlayer(player, marked, channel);
        }
    }

    /// <inheritdoc />
    public void SendToAll(ReadOnlySpan<byte> payload, Channel channel) {
        if (TryMark(payload, out var marked)) {
            session.SendToAll(marked, channel);
        }
    }

    bool TryMark(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> marked) {
        if (NetworkPayload.TryWrap(PayloadKind.Rpc, payload, buffer, out marked)) {
            return true;
        }

        DroppedCount++;

        return false;
    }
}
