// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Transport;

/// <summary>
///     A way to move bytes between a server and its clients.
/// </summary>
/// <remarks>
///     <para>
///         <b>One object holds both halves.</b> A transport has a server half and a client half and
///         either, both, or neither may be running. That is not tidiness: a listen server — one
///         process that is both the host and a player — is the same transport with both halves
///         started, and if the two halves were separate objects then host mode would be a second
///         code path through every layer above. Here it is the same path with a loopback in it.
///     </para>
///     <para>
///         <b>Nothing is delivered outside <see cref="Poll" />.</b> No callbacks arrive on a socket
///         thread, no handler runs between two systems in a frame. The transport queues what
///         happened and hands it over when the frame asks, which is what lets replication be
///         ordinary code that runs at a known point in the schedule rather than code that has to be
///         thread-safe against a network thread.
///     </para>
///     <para>
///         <b>Time is a parameter, not a reading.</b> <see cref="Poll" /> is told how much time has
///         passed rather than asking a clock. A transport whose behaviour depends on time — retries,
///         timeouts, the latency injected by <see cref="NetworkSimulation" /> — is then a pure
///         function of the calls made to it, so a test that wants to observe a 200 ms round trip
///         does it in a loop rather than in 200 ms, and observes the same thing every run.
///     </para>
///     <para>
///         <b>Sending to something that is gone is not an error.</b> A connection that dropped is
///         not observed until the frame polls, so the frame that sends to it is behaving correctly
///         with the information it has. Implementations drop such a send silently. Sending a payload
///         larger than <see cref="TransportCapabilities.MaxPayloadBytes" /> throws, because no
///         information the caller could have had would make that right.
///     </para>
/// </remarks>
public interface ITransport : IDisposable {
    /// <summary>What this transport can carry, and what it will not promise.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    ///     What this transport has counted about datagrams that did not arrive, or
    ///     <see langword="null" /> from one that counts nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Defaulted to <see langword="null" /> rather than required, because most transports
    ///         genuinely cannot answer.</b> An in-process one never loses anything and has no
    ///         sequence numbers to notice a gap in; one over a stream has a stack underneath it that
    ///         hides the packets entirely. Only a transport that numbers its own datagrams knows,
    ///         and <c>UdpTransport</c> is that transport.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is the honest answer and zero is not.</b> A transport that cannot count
    ///         losses has not told anybody there are none — see <see cref="TransportLoss" />, whose
    ///         own remarks say what each of its four totals may and may not be read as.
    ///     </para>
    ///     <para>
    ///         Read on whatever thread <see cref="Poll" /> is called on, and on no other: an
    ///         implementation is free to add these up out of per-connection state that the frame owns.
    ///     </para>
    /// </remarks>
    TransportLoss? Loss => null;

    /// <summary>Whether the server half is listening.</summary>
    TransportState ServerState { get; }

    /// <summary>Whether the client half is connected.</summary>
    TransportState ClientState { get; }

    /// <summary>
    ///     Starts listening. Where — a port, an address, a relay — was fixed when the transport was
    ///     constructed, which is what keeps this interface free of anything socket-shaped.
    /// </summary>
    /// <exception cref="TransportException">The endpoint could not be listened on.</exception>
    void StartServer();

    /// <summary>
    ///     Stops listening and disconnects everyone, reporting
    ///     <see cref="DisconnectReason.ServerStopped" /> to both sides. Does nothing if the server
    ///     half is already stopped.
    /// </summary>
    void StopServer();

    /// <summary>
    ///     Begins connecting. Completion is reported to <see cref="ITransportEvents.OnConnected" />
    ///     on a later <see cref="Poll" />, or refusal to
    ///     <see cref="ITransportEvents.OnDisconnected" /> — never inline, so the caller has one code
    ///     path for "connected in a microsecond over loopback" and "connected in 90 ms over the
    ///     Atlantic".
    /// </summary>
    /// <exception cref="TransportException">The client half is already running.</exception>
    void StartClient();

    /// <summary>
    ///     Disconnects the client half, reporting <see cref="DisconnectReason.Requested" /> to this
    ///     side and <see cref="DisconnectReason.RemoteRequested" /> to the server. Does nothing if
    ///     the client half is already stopped.
    /// </summary>
    void StopClient();

    /// <summary>
    ///     Closes one connection from the server side, reporting
    ///     <see cref="DisconnectReason.Requested" /> here and
    ///     <see cref="DisconnectReason.Kicked" /> there. Does nothing if the connection is not one
    ///     of ours.
    /// </summary>
    /// <param name="connection">The connection to close.</param>
    void Disconnect(ConnectionId connection);

    /// <summary>Sends to one connected client. Does nothing if the connection is not ours.</summary>
    /// <param name="connection">Who to send to.</param>
    /// <param name="payload">
    ///     The bytes. Copied before this returns — the caller may reuse the buffer immediately.
    /// </param>
    /// <param name="channel">The delivery guarantee wanted.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="payload" /> is longer than
    ///     <see cref="TransportCapabilities.MaxPayloadBytes" />.
    /// </exception>
    void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel);

    /// <summary>Sends to the server. Does nothing if the client half is not connected.</summary>
    /// <param name="payload">
    ///     The bytes. Copied before this returns — the caller may reuse the buffer immediately.
    /// </param>
    /// <param name="channel">The delivery guarantee wanted.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="payload" /> is longer than
    ///     <see cref="TransportCapabilities.MaxPayloadBytes" />.
    /// </exception>
    void SendToServer(ReadOnlySpan<byte> payload, Channel channel);

    /// <summary>
    ///     Advances the transport by <paramref name="elapsed" /> and reports everything that has
    ///     happened since the last call, in the order it happened, to
    ///     <paramref name="events" />.
    /// </summary>
    /// <param name="elapsed">
    ///     How much time to advance by. A frame passes its delta; a test passes whatever it wants to
    ///     have happened.
    /// </param>
    /// <param name="events">Where the events go. Called synchronously, on this thread.</param>
    /// <remarks>
    ///     A handler may send, disconnect, or stop a half from inside a callback. What it sends goes
    ///     out as if sent after <see cref="Poll" /> returned; what it stops still reports the events
    ///     already queued behind the one being handled, because those describe things that already
    ///     happened.
    /// </remarks>
    void Poll(TimeSpan elapsed, ITransportEvents events);
}
