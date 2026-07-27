// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>What a transport reports, during <see cref="ITransport.Poll" /> and nowhere else.</summary>
/// <remarks>
///     <para>
///         An interface rather than events or delegates, and a <see cref="ReadOnlySpan{T}" /> rather
///         than an array: the receiving path allocates nothing per packet, and the payload's
///         lifetime is exactly the call, which is a rule a reader can check rather than a convention
///         a reader has to know. A handler that needs the bytes afterwards copies them, and the
///         signature is what tells it so.
///     </para>
///     <para>
///         Every callback names the <see cref="TransportRole" /> it concerns, because a listen
///         server polls one transport and gets both halves' events out of it.
///     </para>
/// </remarks>
public interface ITransportEvents {
    /// <summary>A connection was established.</summary>
    /// <param name="role">
    ///     <see cref="TransportRole.Server" /> when a client connected to us,
    ///     <see cref="TransportRole.Client" /> when we connected to a server.
    /// </param>
    /// <param name="connection">
    ///     The server-assigned id. On the client this is our own id, which is how a client learns
    ///     what number it is.
    /// </param>
    void OnConnected(TransportRole role, ConnectionId connection);

    /// <summary>A connection ended, or an attempt to make one failed.</summary>
    /// <param name="role">Which half of this transport is reporting.</param>
    /// <param name="connection">
    ///     The connection that ended, or <see cref="ConnectionId.None" /> when the attempt never got
    ///     far enough to be given a number.
    /// </param>
    /// <param name="reason">Why.</param>
    void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason);

    /// <summary>A payload arrived.</summary>
    /// <param name="role">Which half of this transport received it.</param>
    /// <param name="connection">
    ///     Who sent it — the client, on the server; our own id, on the client.
    /// </param>
    /// <param name="channel">The channel it was sent on.</param>
    /// <param name="payload">
    ///     The bytes, valid until this call returns and not one instruction longer. Copy what you
    ///     need to keep.
    /// </param>
    void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload);
}
