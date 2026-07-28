// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Transport;

namespace Vixen.Net.Fuzz;

/// <summary>A transport whose wire is whatever the fuzzer hands it.</summary>
/// <remarks>
///     <para>
///         <b>The bytes go in where a socket would put them, not where a test would.</b> A session
///         only ever sees inbound data through <c>Poll</c>, so injecting through <c>Poll</c> is what
///         makes the fuzzed frame the real frame: the handshake table, the ping bookkeeping, the
///         reconnect window and the message handler are all live, and a defect that needs two of
///         them to be in a particular state is reachable. Calling
///         <see cref="ITransportEvents.OnData" /> on the session directly would be a shorter path to
///         a smaller claim.
///     </para>
///     <para>
///         What it sends goes nowhere and is counted. The counts are part of the behaviour signature
///         — "this input made the server answer" is a different outcome from "this input was dropped
///         without comment", and telling them apart is what stops the corpus collapsing onto inputs
///         that are all refused at the first byte.
///     </para>
/// </remarks>
public sealed class InjectingTransport : ITransport {
    readonly Queue<Delivery> queue = new();

    /// <inheritdoc />
    /// <remarks>
    ///     Comfortably above <see cref="Mutator.MaxLength" />: the session allocates a scratch
    ///     buffer of exactly this size and frames outgoing payloads into it, so a cap below what the
    ///     fuzzer produces would make the harness the thing that refused the packet.
    /// </remarks>
    public TransportCapabilities Capabilities { get; } = new(MaxPayloadBytes: 2048, IsInProcess: true, IsLossy: true);

    /// <inheritdoc />
    public TransportState ServerState { get; private set; }

    /// <inheritdoc />
    public TransportState ClientState { get; private set; }

    /// <summary>How many payloads the peer above has tried to send.</summary>
    public long Sent { get; private set; }

    /// <summary>How many connections it has closed.</summary>
    public long Disconnects { get; private set; }

    /// <inheritdoc />
    public void StartServer() => ServerState = TransportState.Running;

    /// <inheritdoc />
    public void StopServer() => ServerState = TransportState.Stopped;

    /// <inheritdoc />
    public void StartClient() {
        ClientState = TransportState.Running;

        // A client half that never reports a connection never sends a handshake, and a session that
        // never sends a handshake never reaches the code that reads the answer to one.
        queue.Enqueue(new(DeliveryKind.Connected, TransportRole.Client, ConnectionId.None, Channel.Reliable, [], 0));
    }

    /// <inheritdoc />
    public void StopClient() {
        ClientState = TransportState.Stopped;
        queue.Enqueue(
            new(DeliveryKind.Disconnected, TransportRole.Client, ConnectionId.None, Channel.Reliable, [], 0)
        );
    }

    /// <inheritdoc />
    public void Disconnect(ConnectionId connection) {
        Disconnects++;
        queue.Enqueue(new(DeliveryKind.Disconnected, TransportRole.Server, connection, Channel.Reliable, [], 0));
    }

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) => Sent++;

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) => Sent++;

    /// <summary>Queues a connection arriving at the server half.</summary>
    /// <param name="connection">Which connection.</param>
    public void Connect(ConnectionId connection) =>
        queue.Enqueue(new(DeliveryKind.Connected, TransportRole.Server, connection, Channel.Reliable, [], 0));

    /// <summary>Queues a connection going away.</summary>
    /// <param name="role">Which half saw it.</param>
    /// <param name="connection">Which connection.</param>
    public void Drop(TransportRole role, ConnectionId connection) =>
        queue.Enqueue(new(DeliveryKind.Disconnected, role, connection, Channel.Reliable, [], 0));

    /// <summary>Queues bytes arriving.</summary>
    /// <param name="role">Which half they arrived at.</param>
    /// <param name="connection">Which connection they came in on.</param>
    /// <param name="channel">Which channel they claim to have used.</param>
    /// <param name="payload">
    ///     The buffer they are in. Not copied — the caller owns it and must leave it alone until
    ///     <see cref="Poll" /> has run, which is what lets a fuzz target reuse one buffer for every
    ///     case instead of allocating the thing it is measuring.
    /// </param>
    /// <param name="length">How many of its bytes are the payload.</param>
    /// <exception cref="ArgumentNullException"><paramref name="payload" /> is null.</exception>
    public void Deliver(TransportRole role, ConnectionId connection, Channel channel, byte[] payload, int length) {
        ArgumentNullException.ThrowIfNull(payload);
        queue.Enqueue(new(DeliveryKind.Data, role, connection, channel, payload, length));
    }

    /// <inheritdoc />
    public void Poll(TimeSpan elapsed, ITransportEvents events) {
        ArgumentNullException.ThrowIfNull(events);

        // Snapshotted by count rather than drained to empty: a handler may disconnect, and a
        // disconnect queues an event, so draining to empty would run this frame's consequences
        // inside this frame and never terminate on an input that makes the session answer itself.
        var pending = queue.Count;

        for (var i = 0; i < pending; i++) {
            var delivery = queue.Dequeue();

            switch (delivery.Kind) {
                case DeliveryKind.Connected:
                    events.OnConnected(delivery.Role, delivery.Connection);

                    break;

                case DeliveryKind.Disconnected:
                    events.OnDisconnected(delivery.Role, delivery.Connection, DisconnectReason.Requested);

                    break;

                default:
                    events.OnData(delivery.Role, delivery.Connection, delivery.Channel, delivery.Payload.AsSpan(0, delivery.Length));

                    break;
            }
        }
    }

    /// <summary>Forgets anything queued.</summary>
    public void Dispose() => queue.Clear();

    enum DeliveryKind : byte {
        Connected = 0,
        Disconnected = 1,
        Data = 2
    }

    readonly record struct Delivery(
        DeliveryKind Kind,
        TransportRole Role,
        ConnectionId Connection,
        Channel Channel,
        byte[] Payload,
        int Length
    );
}
