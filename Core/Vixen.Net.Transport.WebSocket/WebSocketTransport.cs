// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Transport;

namespace Vixen.Net.Transport.WebSocket;

/// <summary>How the transport behaves.</summary>
public sealed record WebSocketTransportOptions {
    /// <summary>Where a server listens. A port of zero lets the operating system choose.</summary>
    public Uri ListenAddress { get; init; } = new("ws://127.0.0.1:0/");

    /// <summary>Where a client connects.</summary>
    public Uri RemoteAddress { get; init; } = new("ws://127.0.0.1:7778/");

    /// <summary>How many clients a server will hold.</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>How long without hearing anything before a peer is considered gone.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often to say something when there is nothing to say.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a client waits for the handshake before giving up.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A transport over WebSockets, for browsers and for anything behind a proxy.</summary>
/// <remarks>
///     <para>
///         <b>The medium already keeps most of the promises</b>, which makes this the shortest of the
///         three transports. A WebSocket is reliable, ordered, and message-framed, so there is no
///         reliability layer here, no sequence numbers, no fragmentation and no reassembly — the
///         things the UDP transport is mostly made of. What is left is a byte saying which channel a
///         payload was sent on, a frame kind, and the timeouts.
///     </para>
///     <para>
///         <b>The four channels collapse to one, and that is a real cost rather than a free win.</b>
///         Everything is delivered, in order, including the payloads whose channels say they need not
///         be — which satisfies the contract, since <c>Unreliable</c> and <c>Sequenced</c> say a
///         payload <i>may</i> be dropped rather than must. But the reason those channels exist is
///         head-of-line blocking: a snapshot that supersedes itself thirty times a second should not
///         wait behind a retransmission of one that is already stale, and over a single TCP stream it
///         does. A browser client has no alternative and this is the right transport for it; a
///         desktop client that could use UDP should.
///     </para>
///     <para>
///         The channel byte is still carried and still reported, so the layer above behaves
///         identically and a game moved between transports does not change. It is the delivery
///         guarantee that is stronger than asked for, not the vocabulary.
///     </para>
/// </remarks>
public sealed class WebSocketTransport : ITransport {
    /// <summary>The largest payload, matching the in-process transport so a game can move between them.</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    const byte KindPayload = 0;
    const byte KindBye = 1;
    const byte KindKeepAlive = 2;
    const byte KindHello = 3;
    const int HeaderBytes = 2;

    static readonly TransportCapabilities Capability = new(MaxPayloadBytes, IsInProcess: false, IsLossy: false);

    readonly IWebSocketFactory factory;
    readonly WebSocketTransportOptions options;
    readonly Dictionary<uint, Peer> clients = [];
    readonly List<uint> finished = [];

    // Stopping is not a poll, but a disconnection is an event, and events only come out of Poll.
    // What StopClient and StopServer have to say waits here until one happens.
    readonly Queue<(TransportRole Role, ConnectionId Id, DisconnectReason Reason)> pending = [];
    readonly byte[] receiveBuffer = new byte[MaxPayloadBytes + HeaderBytes];
    readonly byte[] sendBuffer = new byte[MaxPayloadBytes + HeaderBytes];

    IWebSocketListener? listener;
    Peer? server;
    double now;
    uint nextConnection = 1;
    bool clientConnected;
    double clientDeadline;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => Capability;

    /// <inheritdoc />
    public TransportState ServerState { get; private set; }

    /// <inheritdoc />
    public TransportState ClientState { get; private set; }

    /// <summary>Where the server half is actually listening, once it has started.</summary>
    public Uri? ListeningOn => listener?.Address;

    /// <summary>How many clients are connected.</summary>
    public int ConnectionCount => clients.Count;

    /// <summary>Creates a transport.</summary>
    /// <param name="factory">Where its sockets come from.</param>
    /// <param name="options">How it behaves.</param>
    public WebSocketTransport(IWebSocketFactory factory, WebSocketTransportOptions? options = null) {
        ArgumentNullException.ThrowIfNull(factory);

        this.factory = factory;
        this.options = options ?? new();
    }

    /// <inheritdoc />
    public void StartServer() {
        if (ServerState != TransportState.Stopped) {
            throw new TransportException("The server half is already listening.");
        }

        listener = factory.Listen(options.ListenAddress);
        ServerState = TransportState.Running;
    }

    /// <inheritdoc />
    public void StopServer() {
        if (ServerState == TransportState.Stopped) {
            return;
        }

        foreach (var peer in clients.Values) {
            // The byte in a Bye is the reason for whoever reads it, not for whoever sends it. That
            // is the whole of why a client leaving and a server seeing them leave report different
            // things without either end having to work out the other's point of view.
            Say(peer, KindBye, (byte)DisconnectReason.ServerStopped);
            peer.Channel.Close();
            peer.Channel.Dispose();
            pending.Enqueue((TransportRole.Server, peer.Id, DisconnectReason.ServerStopped));
        }

        clients.Clear();
        listener?.Dispose();
        listener = null;
        ServerState = TransportState.Stopped;
    }

    /// <inheritdoc />
    public void StartClient() {
        if (ClientState != TransportState.Stopped) {
            throw new TransportException("The client half is already running.");
        }

        server = new(new(0), factory.Connect(options.RemoteAddress), now);
        clientConnected = false;
        clientDeadline = now + options.ConnectTimeout.TotalSeconds;
        ClientState = TransportState.Running;
    }

    /// <inheritdoc />
    public void StopClient() {
        if (ClientState == TransportState.Stopped) {
            return;
        }

        if (server is not null) {
            Say(server, KindBye, (byte)DisconnectReason.RemoteRequested);
            server.Channel.Close();
            server.Channel.Dispose();

            if (clientConnected) {
                pending.Enqueue((TransportRole.Client, server.Id, DisconnectReason.Requested));
            }
        }

        server = null;
        clientConnected = false;
        ClientState = TransportState.Stopped;
    }

    /// <inheritdoc />
    public void Disconnect(ConnectionId connection) {
        if (clients.TryGetValue(connection.Value, out var peer)) {
            // They are told they were kicked; this end reports that it asked for it.
            Say(peer, KindBye, (byte)DisconnectReason.Kicked);
            peer.Closing = true;
            peer.Reason = DisconnectReason.Requested;
            peer.Channel.Close();

            return;
        }

        if (server is not null && clientConnected && connection == server.Id) {
            Say(server, KindBye, (byte)DisconnectReason.RemoteRequested);
            server.Closing = true;
            server.Reason = DisconnectReason.Requested;
            server.Channel.Close();
        }
    }

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) {
        Guard(payload);

        if (clients.TryGetValue(connection.Value, out var peer) && !peer.Closing) {
            Send(peer, channel, payload);
        }
    }

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) {
        Guard(payload);

        if (server is { Closing: false } peer && clientConnected) {
            Send(peer, channel, payload);
        }
    }

    /// <inheritdoc />
    public void Poll(TimeSpan elapsed, ITransportEvents events) {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        now += elapsed.TotalSeconds;

        while (pending.TryDequeue(out var leaving)) {
            events.OnDisconnected(leaving.Role, leaving.Id, leaving.Reason);
        }

        Accept(events);
        PollClients(events);
        PollServer(events);
    }

    /// <summary>Stops both halves and lets go of every socket.</summary>
    public void Dispose() {
        StopClient();
        StopServer();
    }

    void Accept(ITransportEvents events) {
        if (listener is null) {
            return;
        }

        listener.Pump();

        while (listener.TryAccept(out var channel) && channel is not null) {
            if (clients.Count >= options.MaxConnections) {
                // Refused by saying so and hanging up, rather than by never answering: a client that
                // is told it is not wanted can say so, and one that is ignored waits out its timeout.
                channel.Send([KindBye, (byte)DisconnectReason.ConnectionRefused]);
                channel.Close();
                channel.Dispose();

                continue;
            }

            var id = new ConnectionId(nextConnection++);
            var peer = new Peer(id, channel, now);
            clients[id.Value] = peer;

            // The number is the server's to give, and the client is told it rather than inventing
            // one — every layer above names a connection by it, so two ends disagreeing about it
            // would be two ends talking about different connections in the same words.
            channel.Send([KindHello, (byte)id.Value, (byte)(id.Value >> 8), (byte)(id.Value >> 16), (byte)(id.Value >> 24)]);
            events.OnConnected(TransportRole.Server, id);
        }
    }

    void PollClients(ITransportEvents events) {
        finished.Clear();

        foreach (var peer in clients.Values) {
            if (!Service(peer, TransportRole.Server, events)) {
                finished.Add(peer.Id.Value);
            }
        }

        foreach (var id in finished) {
            if (clients.Remove(id, out var peer)) {
                events.OnDisconnected(TransportRole.Server, peer.Id, peer.Reason);
                peer.Channel.Dispose();
            }
        }
    }

    void PollServer(ITransportEvents events) {
        if (server is null) {
            return;
        }

        server.Channel.Pump();

        if (!clientConnected) {
            if (server.Channel.State == WebSocketChannelState.Open && TryGreeting(server)) {
                clientConnected = true;
                server.LastHeard = now;
                events.OnConnected(TransportRole.Client, server.Id);
            } else if (server.Channel.State == WebSocketChannelState.Closed || now >= clientDeadline) {
                var peer = server;
                server = null;
                ClientState = TransportState.Stopped;
                peer.Channel.Dispose();
                events.OnDisconnected(TransportRole.Client, peer.Id, DisconnectReason.ConnectionRefused);

                return;
            }

            return;
        }

        if (!Service(server, TransportRole.Client, events)) {
            var peer = server;
            server = null;
            clientConnected = false;
            ClientState = TransportState.Stopped;
            events.OnDisconnected(TransportRole.Client, peer.Id, peer.Reason);
            peer.Channel.Dispose();
        }
    }

    /// <summary>Waits for the server to say which connection this is.</summary>
    /// <remarks>
    ///     A WebSocket being open is not yet a connection: the number every layer above uses to name
    ///     it has to come from the end that allocates it. Until that arrives there is a socket and no
    ///     connection, which is why nothing is reported and the connect timeout is still running.
    /// </remarks>
    bool TryGreeting(Peer peer) {
        while (peer.Channel.TryReceive(receiveBuffer, out var length)) {
            if (length >= 5 && receiveBuffer[0] == KindHello) {
                peer.Id = new(
                    (uint)(receiveBuffer[1] | (receiveBuffer[2] << 8) | (receiveBuffer[3] << 16) | (receiveBuffer[4] << 24))
                );

                return true;
            }
        }

        return false;
    }

    bool Service(Peer peer, TransportRole role, ITransportEvents events) {
        peer.Channel.Pump();

        while (peer.Channel.TryReceive(receiveBuffer, out var length)) {
            peer.LastHeard = now;

            if (length < 1) {
                continue;
            }

            switch (receiveBuffer[0]) {
                case KindPayload when length >= HeaderBytes:
                    events.OnData(
                        role,
                        peer.Id,
                        (Channel)receiveBuffer[1],
                        receiveBuffer.AsSpan(HeaderBytes, length - HeaderBytes)
                    );

                    break;

                case KindBye:
                    peer.Reason = WhyTheyLeft(length >= 2 ? receiveBuffer[1] : (byte)DisconnectReason.RemoteRequested);
                    peer.Closing = true;
                    peer.Channel.Close();

                    return false;

                default:
                    break;
            }
        }

        if (peer.Closing) {
            // We are the ones ending it, and everything they sent before we decided has been read.
            return false;
        }

        if (peer.Channel.State == WebSocketChannelState.Closed) {
            // A socket that ended without a reason is one that broke rather than one that left; the
            // reason a Bye already set is kept, because that one was said out loud.
            if (!peer.Closing) {
                peer.Reason = DisconnectReason.TransportError;
            }

            return false;
        }

        if (now - peer.LastHeard >= options.Timeout.TotalSeconds) {
            peer.Reason = DisconnectReason.Timeout;

            return false;
        }

        if (now >= peer.NextKeepAlive) {
            Say(peer, KindKeepAlive, 0);
            peer.NextKeepAlive = now + options.KeepAliveInterval.TotalSeconds;
        }

        return true;
    }

    void Send(Peer peer, Channel channel, ReadOnlySpan<byte> payload) {
        sendBuffer[0] = KindPayload;
        sendBuffer[1] = (byte)channel;
        payload.CopyTo(sendBuffer.AsSpan(HeaderBytes));
        peer.Channel.Send(sendBuffer.AsSpan(0, payload.Length + HeaderBytes));
        peer.NextKeepAlive = now + options.KeepAliveInterval.TotalSeconds;
    }

    static void Say(Peer peer, byte kind, byte detail) {
        if (peer.Channel.State == WebSocketChannelState.Open) {
            peer.Channel.Send([kind, detail]);
        }
    }

    static DisconnectReason WhyTheyLeft(byte raw) =>
        Enum.IsDefined((DisconnectReason)raw) ? (DisconnectReason)raw : DisconnectReason.RemoteRequested;

    static void Guard(ReadOnlySpan<byte> payload) {
        if (payload.Length > MaxPayloadBytes) {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"This transport carries at most {MaxPayloadBytes} bytes."
            );
        }
    }

    sealed class Peer(ConnectionId id, IWebSocketChannel channel, double at) {
        public ConnectionId Id { get; set; } = id;
        public IWebSocketChannel Channel { get; } = channel;
        public double LastHeard { get; set; } = at;
        public double NextKeepAlive { get; set; } = at;
        public bool Closing { get; set; }
        public DisconnectReason Reason { get; set; } = DisconnectReason.RemoteRequested;
    }
}
