// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;

namespace Vixen.Net.Transport.Local;

/// <summary>
///     A transport with no socket in it: server and clients in one process, talking over queues.
/// </summary>
/// <remarks>
///     <para>
///         This is the transport the rest of the networking stack is developed and tested against,
///         and it is a shipping transport rather than a test double. Offline play is a client and a
///         server on one <see cref="LocalNetwork" />; a listen server is one transport with both
///         halves started, connected to itself. Both use the same session, replication and RPC code
///         a dedicated server uses, so "it works in single player" and "it works in multiplayer"
///         stop being two different claims.
///     </para>
///     <para>
///         <b>It is a perfect wire, on purpose.</b> Nothing is lost, duplicated or reordered, and
///         every channel is therefore honoured for free. Imperfection belongs to
///         <see cref="NetworkSimulation" />, which wraps this and injects it deliberately with a
///         seed — a transport that was unpredictably slightly wrong would make every test above it
///         unpredictably slightly flaky.
///     </para>
///     <para>
///         <b>Delivery still costs a poll.</b> A payload sent now is reported by the receiver's next
///         <see cref="Poll" />, not inside the send. Making the in-process path synchronous would
///         make it the one transport whose ordering the layers above could not be tested against.
///     </para>
///     <para>
///         Sends and connects are thread-safe. <see cref="Poll" /> is not reentrant and is meant to
///         be called from one thread — the frame owns it.
///     </para>
/// </remarks>
public sealed class LocalTransport : ITransport {
    /// <summary>
    ///     The largest payload this transport carries, in bytes.
    /// </summary>
    /// <remarks>
    ///     An in-process queue has no MTU and could carry any array, which is exactly why there is a
    ///     limit: 64 KiB is the largest a UDP datagram can be, so a payload that fits here is one
    ///     that the fragmentation layer above <c>Vixen.Net.Transport.Udp</c> can still get across a
    ///     real network. A local transport with no cap would let a bug be discovered on the day the
    ///     game first ran over sockets.
    /// </remarks>
    public const int MaxPayloadBytes = 64 * 1024;

    static readonly TransportCapabilities Capability = new(MaxPayloadBytes, IsInProcess: true, IsLossy: false);

    readonly LocalNetwork network;
    readonly Lock gate = new();
    readonly Queue<Queued> inbox = new();
    readonly Dictionary<uint, LocalTransport> clients = new();
    readonly List<Queued> dispatching = [];

    uint nextConnection = 1;
    bool connectPending;
    bool polling;
    bool disposed;
    LocalTransport? upstream;
    ConnectionId self;

    /// <summary>The address the server half binds, and the client half looks for.</summary>
    public string Address { get; }

    /// <summary>The network both halves reach each other through.</summary>
    public LocalNetwork Network => network;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => Capability;

    /// <inheritdoc />
    public TransportState ServerState { get; private set; }

    /// <inheritdoc />
    public TransportState ClientState { get; private set; }

    /// <summary>How many clients the server half currently has.</summary>
    public int ConnectionCount {
        get {
            lock (gate) {
                return clients.Count;
            }
        }
    }

    /// <summary>Creates a transport on a network.</summary>
    /// <param name="network">The network its halves reach each other through.</param>
    /// <param name="address">
    ///     What the server half listens on and the client half connects to. Two transports on one
    ///     network may share an address only if at most one of them is listening.
    /// </param>
    public LocalTransport(LocalNetwork network, string address = LocalNetwork.DefaultAddress) {
        this.network = network;
        Address = address;
    }

    /// <inheritdoc />
    public void StartServer() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (ServerState != TransportState.Stopped) {
                throw new TransportException($"The server half is already {ServerState}.");
            }

            ServerState = TransportState.Starting;
        }

        try {
            network.Bind(Address, this);
        } catch {
            lock (gate) {
                ServerState = TransportState.Stopped;
            }

            throw;
        }

        lock (gate) {
            ServerState = TransportState.Running;
        }
    }

    /// <inheritdoc />
    public void StopServer() {
        LocalTransport[] dropped;

        lock (gate) {
            if (ServerState is TransportState.Stopped or TransportState.Stopping) {
                return;
            }

            ServerState = TransportState.Stopping;
            dropped = new LocalTransport[clients.Count];

            var index = 0;
            foreach (var (id, client) in clients) {
                dropped[index++] = client;
                inbox.Enqueue(Queued.Disconnected(TransportRole.Server, new(id), DisconnectReason.ServerStopped));
            }

            clients.Clear();
        }

        network.Unbind(Address, this);

        // Outside the lock: in a listen server one of these is this transport, and telling it its
        // server went away takes the same lock we would still be holding.
        foreach (var client in dropped) {
            client.ServerGone(DisconnectReason.ServerStopped);
        }

        lock (gate) {
            ServerState = TransportState.Stopped;
        }
    }

    /// <inheritdoc />
    public void StartClient() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (ClientState != TransportState.Stopped) {
                throw new TransportException($"The client half is already {ClientState}.");
            }

            ClientState = TransportState.Starting;
            connectPending = true;
        }
    }

    /// <inheritdoc />
    public void StopClient() {
        LocalTransport? server;
        ConnectionId id;

        lock (gate) {
            if (ClientState == TransportState.Stopped) {
                return;
            }

            server = upstream;
            id = self;
            upstream = null;
            self = ConnectionId.None;
            connectPending = false;
            ClientState = TransportState.Stopped;
            inbox.Enqueue(Queued.Disconnected(TransportRole.Client, id, DisconnectReason.Requested));
        }

        server?.ClientGone(id, DisconnectReason.RemoteRequested);
    }

    /// <inheritdoc />
    public void Disconnect(ConnectionId connection) {
        LocalTransport? client;

        lock (gate) {
            if (ServerState != TransportState.Running || !clients.Remove(connection.Value, out client)) {
                return;
            }

            inbox.Enqueue(Queued.Disconnected(TransportRole.Server, connection, DisconnectReason.Requested));
        }

        client.ServerGone(DisconnectReason.Kicked);
    }

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) {
        CheckPayloadSize(payload);

        LocalTransport? client;

        lock (gate) {
            if (ServerState != TransportState.Running || !clients.TryGetValue(connection.Value, out client)) {
                return;
            }
        }

        client.Deliver(TransportRole.Client, connection, channel, payload);
    }

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) {
        CheckPayloadSize(payload);

        LocalTransport? server;
        ConnectionId id;

        lock (gate) {
            if (ClientState != TransportState.Running) {
                return;
            }

            server = upstream;
            id = self;
        }

        server?.Deliver(TransportRole.Server, id, channel, payload);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <paramref name="elapsed" /> is ignored: nothing here waits for time to pass. It is in the
    ///     signature because every other transport needs it, and a local transport that took a
    ///     different one would stop being substitutable for them.
    /// </remarks>
    public void Poll(TimeSpan elapsed, ITransportEvents events) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ResolvePendingConnect();

        lock (gate) {
            if (polling) {
                throw new TransportException("Poll is already running on this transport. It is not reentrant.");
            }

            polling = true;

            while (inbox.Count > 0) {
                dispatching.Add(inbox.Dequeue());
            }
        }

        var index = 0;

        try {
            for (; index < dispatching.Count; index++) {
                var queued = dispatching[index];

                switch (queued.Kind) {
                    case QueuedKind.Connected:
                        events.OnConnected(queued.Role, queued.Connection);
                        break;

                    case QueuedKind.Disconnected:
                        events.OnDisconnected(queued.Role, queued.Connection, queued.Reason);
                        break;

                    case QueuedKind.Data:
                        var payload = queued.Buffer!;

                        try {
                            events.OnData(queued.Role, queued.Connection, queued.Channel, payload.AsSpan(0, queued.Length));
                        } finally {
                            ArrayPool<byte>.Shared.Return(payload);
                        }

                        break;

                    default:
                        throw new TransportException($"Unknown queued event kind {queued.Kind}.");
                }
            }
        } finally {
            // A handler that threw leaves the rest of the batch undispatched. Their buffers still
            // have to go back, or a handler that throws once a frame drains the pool.
            for (var rest = index + 1; rest < dispatching.Count; rest++) {
                if (dispatching[rest].Buffer is { } buffer) {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            dispatching.Clear();

            lock (gate) {
                polling = false;
            }
        }
    }

    /// <summary>Stops both halves and releases every payload still queued.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        StopClient();
        StopServer();

        lock (gate) {
            disposed = true;

            while (inbox.Count > 0) {
                if (inbox.Dequeue().Buffer is { } buffer) {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    void ResolvePendingConnect() {
        lock (gate) {
            if (!connectPending) {
                return;
            }

            connectPending = false;
        }

        // Find and accept outside our own lock: in a listen server the server being asked is this
        // transport, and TryAccept takes its gate.
        var server = network.Find(Address);

        if (server is not null && server.TryAccept(this, out var id)) {
            lock (gate) {
                upstream = server;
                self = id;
                ClientState = TransportState.Running;
                inbox.Enqueue(Queued.Connected(TransportRole.Client, id));
            }

            return;
        }

        lock (gate) {
            ClientState = TransportState.Stopped;
            inbox.Enqueue(
                Queued.Disconnected(TransportRole.Client, ConnectionId.None, DisconnectReason.ConnectionRefused)
            );
        }
    }

    bool TryAccept(LocalTransport client, out ConnectionId connection) {
        lock (gate) {
            if (ServerState != TransportState.Running || disposed) {
                connection = ConnectionId.None;

                return false;
            }

            connection = new(nextConnection++);
            clients[connection.Value] = client;
            inbox.Enqueue(Queued.Connected(TransportRole.Server, connection));

            return true;
        }
    }

    void ServerGone(DisconnectReason reason) {
        lock (gate) {
            if (ClientState == TransportState.Stopped) {
                return;
            }

            var id = self;
            upstream = null;
            self = ConnectionId.None;
            connectPending = false;
            ClientState = TransportState.Stopped;
            inbox.Enqueue(Queued.Disconnected(TransportRole.Client, id, reason));
        }
    }

    void ClientGone(ConnectionId connection, DisconnectReason reason) {
        lock (gate) {
            if (!clients.Remove(connection.Value)) {
                return;
            }

            inbox.Enqueue(Queued.Disconnected(TransportRole.Server, connection, reason));
        }
    }

    void Deliver(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
        var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);

        lock (gate) {
            if (disposed) {
                ArrayPool<byte>.Shared.Return(buffer);

                return;
            }

            inbox.Enqueue(Queued.Data(role, connection, channel, buffer, payload.Length));
        }
    }

    static void CheckPayloadSize(ReadOnlySpan<byte> payload) {
        if (payload.Length > MaxPayloadBytes) {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"A local transport payload may be at most {MaxPayloadBytes} bytes."
            );
        }
    }

    enum QueuedKind : byte {
        Connected,
        Disconnected,
        Data
    }

    readonly record struct Queued(
        QueuedKind Kind,
        TransportRole Role,
        ConnectionId Connection,
        Channel Channel,
        DisconnectReason Reason,
        byte[]? Buffer,
        int Length
    ) {
        public static Queued Connected(TransportRole role, ConnectionId connection) =>
            new(
                QueuedKind.Connected,
                role,
                connection,
                Vixen.Net.Channel.Reliable,
                DisconnectReason.Requested,
                null,
                0
            );

        public static Queued Disconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) =>
            new(QueuedKind.Disconnected, role, connection, Vixen.Net.Channel.Reliable, reason, null, 0);

        public static Queued Data(
            TransportRole role,
            ConnectionId connection,
            Channel channel,
            byte[] buffer,
            int length
        ) =>
            new(QueuedKind.Data, role, connection, channel, DisconnectReason.Requested, buffer, length);
    }
}
