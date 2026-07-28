// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using Vixen.Net.Time;

namespace Vixen.Net.Transport.Udp;

/// <summary>
///     The transport with a socket in it: four channels, retransmission, reassembly and timeouts, over
///     datagrams that arrive when they feel like it and sometimes not at all.
/// </summary>
/// <remarks>
///     <para>
///         Everything the layers above rely on — that a <see cref="Channel.Reliable" /> payload
///         arrives once and in order, that a <see cref="Channel.Sequenced" /> one is never delivered
///         after a newer one, that a 64 KiB payload arrives whole — is built here out of a medium
///         that promises none of it. The conformance suite is what says it worked: this transport is
///         held to exactly the same tests as the in-process one, which is the point of having written
///         them against the interface.
///     </para>
///     <para>
///         <b>The socket is behind a seam.</b> Sequencing, retransmission and reassembly are the
///         parts that are subtle, and they are tested over an in-memory bus where time is a parameter
///         and every run is the same run. The socket adapter under it is thin enough to read.
///     </para>
///     <para>
///         <b>Time is a parameter here too.</b> Retransmission timeouts, keep-alives and connection
///         timeouts are all spent against the clock <see cref="Poll" /> advances, so a test that wants
///         to watch a connection time out does it in a loop rather than in ten seconds.
///     </para>
/// </remarks>
public sealed class UdpTransport : ITransport {
    static readonly Channel[] Channels = [Channel.Reliable, Channel.ReliableUnordered, Channel.Unreliable, Channel.Sequenced];

    readonly IDatagramSocketFactory factory;
    readonly UdpTransportOptions options;
    readonly Dictionary<EndPoint, UdpConnection> byEndPoint = [];
    readonly Dictionary<uint, UdpConnection> byId = [];
    readonly Queue<Queued> inbox = new();
    readonly List<Queued> dispatching = [];
    readonly List<(byte[] Buffer, int Length)> delivered = [];
    readonly List<Unacked> due = [];
    readonly List<UdpConnection> stale = [];
    readonly byte[] sendBuffer = new byte[UdpProtocol.MaxDatagramBytes];
    readonly byte[] receiveBuffer = new byte[UdpProtocol.MaxDatagramBytes];

    readonly byte[] cookieSecret = RandomNumberGenerator.GetBytes(8);

    IDatagramSocket? serverSocket;
    IDatagramSocket? clientSocket;
    UdpConnection? upstream;
    uint nextConnection = 1;
    uint clientSalt;
    uint clientChallenge;
    bool hasChallenge;
    double now;
    double connectDeadline;
    double nextConnectRetry;
    bool disposed;

    /// <inheritdoc />
    public TransportCapabilities Capabilities { get; } = new(UdpProtocol.MaxPayloadBytes, IsInProcess: false, IsLossy: true);

    /// <inheritdoc />
    public TransportState ServerState { get; private set; }

    /// <inheritdoc />
    public TransportState ClientState { get; private set; }

    /// <summary>How many clients the server half has.</summary>
    public int ConnectionCount => byId.Count;

    /// <summary>Where the server half is bound, once it is listening.</summary>
    public EndPoint? ListeningOn => serverSocket?.LocalEndPoint;

    /// <summary>Datagrams that were not a packet this transport understands.</summary>
    public long RejectedDatagramCount { get; private set; }

    /// <summary>Datagrams sent again because no acknowledgement came for them.</summary>
    /// <remarks>
    ///     The number a diagnostics panel draws, and the one that says whether a connection is
    ///     struggling. Zero on a good link; a rising fraction of the send count is loss.
    /// </remarks>
    public long RetransmitCount {
        get {
            var total = 0L;

            foreach (var connection in byId.Values) {
                total += connection.RetransmitCount;
            }

            return total + (upstream?.RetransmitCount ?? 0);
        }
    }

    /// <summary>Creates a transport.</summary>
    /// <param name="factory">Where its sockets come from.</param>
    /// <param name="options">How it behaves.</param>
    public UdpTransport(IDatagramSocketFactory factory, UdpTransportOptions? options = null) {
        ArgumentNullException.ThrowIfNull(factory);

        this.factory = factory;
        this.options = options ?? new UdpTransportOptions();
    }

    /// <inheritdoc />
    public void StartServer() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (ServerState != TransportState.Stopped) {
            throw new TransportException($"The server half is already {ServerState}.");
        }

        try {
            serverSocket = factory.Bind(options.ListenEndPoint);
        } catch (Exception exception) {
            throw new TransportException($"Could not listen on {options.ListenEndPoint}.", exception);
        }

        ServerState = TransportState.Running;
    }

    /// <inheritdoc />
    public void StopServer() {
        if (ServerState is TransportState.Stopped or TransportState.Stopping) {
            return;
        }

        ServerState = TransportState.Stopping;

        foreach (var connection in byId.Values.ToArray()) {
            Send(connection.EndPoint, UdpProtocol.WriteDisconnect(sendBuffer, connection.Id.Value, UdpDenyReason.Shutdown), serverSocket);
            Close(connection, TransportRole.Server, DisconnectReason.ServerStopped);
        }

        serverSocket?.Dispose();
        serverSocket = null;
        ServerState = TransportState.Stopped;
    }

    /// <inheritdoc />
    public void StartClient() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (ClientState != TransportState.Stopped) {
            throw new TransportException($"The client half is already {ClientState}.");
        }

        clientSocket = factory.Bind(new(options.RemoteEndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        clientSalt = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        hasChallenge = false;
        ClientState = TransportState.Starting;
        connectDeadline = now + options.ConnectTimeout.TotalSeconds;
        nextConnectRetry = now;
    }

    /// <inheritdoc />
    public void StopClient() {
        if (ClientState == TransportState.Stopped) {
            return;
        }

        var id = upstream?.Id ?? ConnectionId.None;

        if (upstream is not null) {
            Send(upstream.EndPoint, UdpProtocol.WriteDisconnect(sendBuffer, id.Value, UdpDenyReason.Shutdown), clientSocket);
            upstream.Clear();
            upstream = null;
        }

        clientSocket?.Dispose();
        clientSocket = null;
        ClientState = TransportState.Stopped;
        inbox.Enqueue(Queued.Disconnected(TransportRole.Client, id, DisconnectReason.Requested));
    }

    /// <inheritdoc />
    public void Disconnect(ConnectionId connection) {
        if (ServerState != TransportState.Running || !byId.TryGetValue(connection.Value, out var found)) {
            return;
        }

        Send(found.EndPoint, UdpProtocol.WriteDisconnect(sendBuffer, connection.Value, UdpDenyReason.Kicked), serverSocket);
        Close(found, TransportRole.Server, DisconnectReason.Requested);
    }

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) {
        CheckPayload(payload);

        if (ServerState != TransportState.Running || !byId.TryGetValue(connection.Value, out var found)) {
            return;
        }

        SendMessage(found, payload, channel, serverSocket);
    }

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) {
        CheckPayload(payload);

        if (ClientState != TransportState.Running || upstream is null) {
            return;
        }

        SendMessage(upstream, payload, channel, clientSocket);
    }

    /// <inheritdoc />
    public void Poll(TimeSpan elapsed, ITransportEvents events) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        now += elapsed.TotalSeconds;

        Receive(serverSocket, TransportRole.Server);
        Receive(clientSocket, TransportRole.Client);
        Handshake();
        Retransmit();
        SendAcks();
        KeepAlive();
        Timeouts();
        Dispatch(events);
    }

    /// <summary>Stops both halves and closes the sockets.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        StopClient();
        StopServer();
        disposed = true;

        while (inbox.Count > 0) {
            if (inbox.Dequeue().Buffer is { } buffer) {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    void SendMessage(UdpConnection connection, ReadOnlySpan<byte> payload, Channel channel, IDatagramSocket? socket) {
        var sender = connection.Sender(channel);
        var fragments = Math.Max(1, (payload.Length + UdpProtocol.MaxFragmentBytes - 1) / UdpProtocol.MaxFragmentBytes);
        var fragmentId = sender.NextFragmentId();

        for (var index = 0; index < fragments; index++) {
            var offset = index * UdpProtocol.MaxFragmentBytes;
            var length = Math.Min(UdpProtocol.MaxFragmentBytes, payload.Length - offset);
            var sequence = sender.NextSequence();

            var written = UdpProtocol.WriteMessage(
                sendBuffer,
                connection.Id.Value,
                channel,
                sequence,
                fragmentId,
                (byte)index,
                (byte)fragments,
                payload.Slice(offset, Math.Max(0, length))
            );

            Send(connection.EndPoint, written, socket);

            if (channel.IsReliable()) {
                sender.Remember(sequence, sendBuffer.AsSpan(0, written), now);
                sender.Trim(options.MaxUnacknowledged);
            }
        }

        connection.NextKeepAlive = now + options.KeepAliveInterval.TotalSeconds;
    }

    void Receive(IDatagramSocket? socket, TransportRole role) {
        if (socket is null) {
            return;
        }

        while (socket.TryReceiveFrom(receiveBuffer, out var from, out var length)) {
            Handle(receiveBuffer.AsSpan(0, length), from, role);
        }
    }

    void Handle(ReadOnlySpan<byte> datagram, EndPoint from, TransportRole role) {
        if (!UdpProtocol.TryReadKind(datagram, out var kind)) {
            RejectedDatagramCount++;

            return;
        }

        switch (kind) {
            case UdpPacketKind.ConnectRequest when role == TransportRole.Server:
                Challenge(datagram, from);

                return;

            case UdpPacketKind.ConnectResponse when role == TransportRole.Server:
                Accept(datagram, from);

                return;

            case UdpPacketKind.ConnectChallenge when role == TransportRole.Client:
                Challenged(datagram);

                return;

            case UdpPacketKind.ConnectAccept when role == TransportRole.Client:
                AcceptedByServer(datagram, from);

                return;

            case UdpPacketKind.ConnectDenied when role == TransportRole.Client:
                if (ClientState == TransportState.Starting) {
                    Refused(DisconnectReason.ConnectionRefused);
                }

                return;

            default:
                break;
        }

        var connection = Find(from, role);

        if (connection is null) {
            RejectedDatagramCount++;

            return;
        }

        connection.LastHeard = now;

        switch (kind) {
            case UdpPacketKind.Disconnect:
                Close(connection, role, WhyTheyLeft(datagram, role));

                break;

            case UdpPacketKind.KeepAlive:
                break;

            case UdpPacketKind.Message:
                Message(connection, datagram, role);

                break;

            case UdpPacketKind.Ack:
                Ack(connection, datagram);

                break;

            default:
                RejectedDatagramCount++;

                break;
        }
    }

    void Message(UdpConnection connection, ReadOnlySpan<byte> datagram, TransportRole role) {
        if (!UdpProtocol.TryReadByte(datagram, 5, out var rawChannel)
            || rawChannel > (byte)Channel.Sequenced
            || !UdpProtocol.TryReadUInt16(datagram, 6, out var sequence)
            || !UdpProtocol.TryReadUInt16(datagram, 8, out _)
            || !UdpProtocol.TryReadByte(datagram, 10, out var index)
            || !UdpProtocol.TryReadByte(datagram, 11, out var count)
            || count > UdpProtocol.MaxFragments
            || datagram.Length < UdpProtocol.MessageHeaderBytes) {
            RejectedDatagramCount++;

            return;
        }

        var channel = (Channel)rawChannel;
        delivered.Clear();

        connection.Receiver(channel)
            .Receive(sequence, index, count, datagram[UdpProtocol.MessageHeaderBytes..], delivered);

        foreach (var (buffer, length) in delivered) {
            inbox.Enqueue(Queued.Data(role, connection.Id, channel, buffer, length));
        }
    }

    void Ack(UdpConnection connection, ReadOnlySpan<byte> datagram) {
        if (!UdpProtocol.TryReadByte(datagram, 5, out var rawChannel)
            || rawChannel > (byte)Channel.Sequenced
            || !UdpProtocol.TryReadUInt16(datagram, 6, out var latest)
            || !UdpProtocol.TryReadUInt32(datagram, 8, out var history)) {
            RejectedDatagramCount++;

            return;
        }

        if (connection.Sender((Channel)rawChannel).Acknowledge(latest, history, now, out var sample) && sample >= 0) {
            connection.RoundTrip.Add(TimeSpan.FromSeconds(sample));
        }
    }

    void Challenge(ReadOnlySpan<byte> datagram, EndPoint from) {
        if (ServerState != TransportState.Running
            || datagram.Length < UdpProtocol.ConnectRequestBytes
            || !UdpProtocol.TryReadUInt32(datagram, 1, out var salt)) {
            RejectedDatagramCount++;

            return;
        }

        // No state is allocated here, on purpose. A datagram is trivially forged with somebody
        // else's address on it, and a server that allocated a connection for one could be filled up
        // by an attacker who never receives a single reply. So the answer is a number derived from
        // the address it claims to be at, and only a client that is actually there can echo it.
        Send(from, UdpProtocol.WriteConnectChallenge(sendBuffer, salt, Cookie(from, salt)), serverSocket);
    }

    void Challenged(ReadOnlySpan<byte> datagram) {
        if (ClientState != TransportState.Starting
            || !UdpProtocol.TryReadUInt32(datagram, 1, out var salt)
            || salt != clientSalt
            || !UdpProtocol.TryReadUInt32(datagram, 5, out var challenge)) {
            return;
        }

        clientChallenge = challenge;
        hasChallenge = true;
        nextConnectRetry = now;
    }

    /// <summary>
    ///     The number a client has to echo to prove it is at the address it says it is.
    /// </summary>
    /// <remarks>
    ///     Derived from a secret this transport made when it started, the address, and the client's
    ///     salt — so the server can check it without having kept anything, and an attacker who cannot
    ///     receive at the address it is forging cannot produce it. It is a cookie, not cryptography:
    ///     it stops blind spoofing from costing the server memory, and doc 16 is explicit that this
    ///     plan does not claim a bespoke crypto layer. DTLS is where confidentiality belongs.
    /// </remarks>
    uint Cookie(EndPoint from, uint salt) {
        var hash = 2166136261u;

        foreach (var value in cookieSecret) {
            hash = (hash ^ value) * 16777619u;
        }

        foreach (var character in from.ToString() ?? string.Empty) {
            hash = (hash ^ character) * 16777619u;
        }

        for (var shift = 0; shift < 32; shift += 8) {
            hash = (hash ^ ((salt >> shift) & 0xFF)) * 16777619u;
        }

        return hash;
    }

    void Accept(ReadOnlySpan<byte> datagram, EndPoint from) {
        if (!UdpProtocol.TryReadUInt32(datagram, 1, out var salt)
            || !UdpProtocol.TryReadUInt32(datagram, 5, out var challenge)) {
            RejectedDatagramCount++;

            return;
        }

        if (ServerState != TransportState.Running) {
            return;
        }

        if (challenge != Cookie(from, salt)) {
            RejectedDatagramCount++;

            return;
        }

        if (byEndPoint.TryGetValue(from, out var existing)) {
            // The accept was lost and the client is asking again. Answering with the same id is what
            // makes the handshake idempotent, and idempotent is what makes it survive a bad link.
            Send(from, UdpProtocol.WriteConnectAccept(sendBuffer, salt, existing.Id.Value), serverSocket);

            return;
        }

        if (byId.Count >= options.MaxConnections) {
            Send(from, UdpProtocol.WriteConnectDenied(sendBuffer, salt, UdpDenyReason.Full), serverSocket);

            return;
        }

        var connection = new UdpConnection(new(nextConnection++), from.Clone()) { LastHeard = now };
        byEndPoint[connection.EndPoint] = connection;
        byId[connection.Id.Value] = connection;

        Send(connection.EndPoint, UdpProtocol.WriteConnectAccept(sendBuffer, salt, connection.Id.Value), serverSocket);
        inbox.Enqueue(Queued.Connected(TransportRole.Server, connection.Id));
    }

    void AcceptedByServer(ReadOnlySpan<byte> datagram, EndPoint from) {
        if (ClientState != TransportState.Starting
            || !UdpProtocol.TryReadUInt32(datagram, 1, out var salt)
            || salt != clientSalt
            || !UdpProtocol.TryReadUInt32(datagram, 5, out var id)) {
            return;
        }

        upstream = new(new(id), from.Clone()) { LastHeard = now };
        ClientState = TransportState.Running;
        inbox.Enqueue(Queued.Connected(TransportRole.Client, upstream.Id));
    }

    void Handshake() {
        if (ClientState != TransportState.Starting) {
            return;
        }

        if (now >= connectDeadline) {
            Refused(DisconnectReason.ConnectionRefused);

            return;
        }

        if (now < nextConnectRetry) {
            return;
        }

        nextConnectRetry = now + options.ConnectRetryInterval.TotalSeconds;

        var written = hasChallenge
            ? UdpProtocol.WriteConnectResponse(sendBuffer, clientSalt, clientChallenge)
            : UdpProtocol.WriteConnectRequest(sendBuffer, clientSalt);

        Send(options.RemoteEndPoint, written, clientSocket);
    }

    void Refused(DisconnectReason reason) {
        ClientState = TransportState.Stopped;
        clientSocket?.Dispose();
        clientSocket = null;
        inbox.Enqueue(Queued.Disconnected(TransportRole.Client, ConnectionId.None, reason));
    }

    void Retransmit() {
        foreach (var connection in byId.Values) {
            Retransmit(connection, serverSocket);
        }

        if (upstream is not null) {
            Retransmit(upstream, clientSocket);
        }
    }

    void Retransmit(UdpConnection connection, IDatagramSocket? socket) {
        var timeout = RetransmitTimeout(connection);

        foreach (var channel in Channels) {
            if (!channel.IsReliable()) {
                continue;
            }

            due.Clear();
            connection.Sender(channel).CollectDue(now, timeout, due);

            foreach (var entry in due) {
                Send(connection.EndPoint, entry.Datagram.AsSpan(0, entry.Length), socket);
            }
        }
    }

    double RetransmitTimeout(UdpConnection connection) {
        if (!connection.RoundTrip.HasSamples) {
            return options.InitialRetransmitTimeout.TotalSeconds;
        }

        // RFC 6298's formula, with the estimator the session already uses: the average plus four
        // times the variance, which is the point past which a packet is far more likely lost than
        // late.
        var estimate = connection.RoundTrip.RoundTrip.TotalSeconds + (4 * connection.RoundTrip.Jitter.TotalSeconds);

        return Math.Clamp(estimate, options.MinRetransmitTimeout.TotalSeconds, options.MaxRetransmitTimeout.TotalSeconds);
    }

    void SendAcks() {
        foreach (var connection in byId.Values) {
            SendAcks(connection, serverSocket);
        }

        if (upstream is not null) {
            SendAcks(upstream, clientSocket);
        }
    }

    void SendAcks(UdpConnection connection, IDatagramSocket? socket) {
        foreach (var channel in Channels) {
            if (!channel.IsReliable()) {
                // Nothing retransmits an unreliable datagram, so nothing needs to hear that it
                // arrived. Acknowledging everything would double the packet count to say so.
                continue;
            }

            var receiver = connection.Receiver(channel);

            if (!receiver.AckPending || !receiver.HasReceived) {
                continue;
            }

            receiver.AckPending = false;

            Send(
                connection.EndPoint,
                UdpProtocol.WriteAck(sendBuffer, connection.Id.Value, channel, receiver.Latest, receiver.History),
                socket
            );
        }
    }

    void KeepAlive() {
        foreach (var connection in byId.Values) {
            KeepAlive(connection, serverSocket);
        }

        if (upstream is not null) {
            KeepAlive(upstream, clientSocket);
        }
    }

    void KeepAlive(UdpConnection connection, IDatagramSocket? socket) {
        if (now < connection.NextKeepAlive) {
            return;
        }

        connection.NextKeepAlive = now + options.KeepAliveInterval.TotalSeconds;
        Send(connection.EndPoint, UdpProtocol.WriteKeepAlive(sendBuffer, connection.Id.Value), socket);
    }

    void Timeouts() {
        var limit = options.Timeout.TotalSeconds;
        stale.Clear();

        foreach (var connection in byId.Values) {
            if (now - connection.LastHeard > limit) {
                stale.Add(connection);
            }
        }

        foreach (var connection in stale) {
            Close(connection, TransportRole.Server, DisconnectReason.Timeout);
        }

        if (upstream is not null && now - upstream.LastHeard > limit) {
            var id = upstream.Id;
            upstream.Clear();
            upstream = null;
            ClientState = TransportState.Stopped;
            clientSocket?.Dispose();
            clientSocket = null;
            inbox.Enqueue(Queued.Disconnected(TransportRole.Client, id, DisconnectReason.Timeout));
        }
    }

    void Close(UdpConnection connection, TransportRole role, DisconnectReason reason) {
        if (role == TransportRole.Server) {
            byEndPoint.Remove(connection.EndPoint);
            byId.Remove(connection.Id.Value);
            connection.Clear();
            inbox.Enqueue(Queued.Disconnected(TransportRole.Server, connection.Id, reason));

            return;
        }

        var id = connection.Id;
        connection.Clear();
        upstream = null;
        ClientState = TransportState.Stopped;
        clientSocket?.Dispose();
        clientSocket = null;
        inbox.Enqueue(Queued.Disconnected(TransportRole.Client, id, reason));
    }

    static DisconnectReason WhyTheyLeft(ReadOnlySpan<byte> datagram, TransportRole role) {
        // On the server it is always the client asking to leave — there is nothing else a client can
        // mean by it. On the client the reason byte is the difference between "the server shut down"
        // and "the server closed me specifically", which is exactly the distinction a game needs to
        // decide whether reconnecting is worth trying.
        if (role == TransportRole.Server || !UdpProtocol.TryReadByte(datagram, 5, out var reason)) {
            return DisconnectReason.RemoteRequested;
        }

        return (UdpDenyReason)reason switch {
            UdpDenyReason.Shutdown => DisconnectReason.ServerStopped,
            UdpDenyReason.Kicked => DisconnectReason.Kicked,
            UdpDenyReason.Timeout => DisconnectReason.Timeout,
            _ => DisconnectReason.RemoteRequested
        };
    }

    UdpConnection? Find(EndPoint from, TransportRole role) {
        if (role == TransportRole.Server) {
            return byEndPoint.GetValueOrDefault(from);
        }

        // On the client there is one connection, and a datagram from anywhere else is not it. The
        // endpoint check is most of what makes an off-path spoof need to guess the port as well as
        // the connection id.
        return upstream is not null && upstream.EndPoint.Equals(from) ? upstream : null;
    }

    void Dispatch(ITransportEvents events) {
        while (inbox.Count > 0) {
            dispatching.Add(inbox.Dequeue());
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

                    default:
                        var payload = queued.Buffer!;

                        try {
                            events.OnData(queued.Role, queued.Connection, queued.Channel, payload.AsSpan(0, queued.Length));
                        } finally {
                            ArrayPool<byte>.Shared.Return(payload);
                        }

                        break;
                }
            }
        } finally {
            for (var rest = index + 1; rest < dispatching.Count; rest++) {
                if (dispatching[rest].Buffer is { } buffer) {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            dispatching.Clear();
        }
    }

    void Send(EndPoint to, int length, IDatagramSocket? socket) => Send(to, sendBuffer.AsSpan(0, length), socket);

    static void Send(EndPoint to, ReadOnlySpan<byte> datagram, IDatagramSocket? socket) => socket?.SendTo(datagram, to);

    static void CheckPayload(ReadOnlySpan<byte> payload) {
        if (payload.Length > UdpProtocol.MaxPayloadBytes) {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"A payload may be at most {UdpProtocol.MaxPayloadBytes} bytes; above that it would not fit in "
                + $"{UdpProtocol.MaxFragments} fragments."
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
            new(QueuedKind.Connected, role, connection, Net.Channel.Reliable, DisconnectReason.Requested, null, 0);

        public static Queued Disconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) =>
            new(QueuedKind.Disconnected, role, connection, Net.Channel.Reliable, reason, null, 0);

        public static Queued Data(TransportRole role, ConnectionId connection, Channel channel, byte[] buffer, int length) =>
            new(QueuedKind.Data, role, connection, channel, DisconnectReason.Requested, buffer, length);
    }
}

/// <summary>One connection's state: its channels, its clock, and where it is.</summary>
sealed class UdpConnection {
    readonly ChannelSender[] senders = [new(), new(), new(), new()];
    readonly ChannelReceiver[] receivers;

    public ConnectionId Id { get; }

    public EndPoint EndPoint { get; }

    public RoundTripEstimator RoundTrip { get; } = new();

    public double LastHeard { get; set; }

    public double NextKeepAlive { get; set; }

    public UdpConnection(ConnectionId id, EndPoint endPoint) {
        Id = id;
        EndPoint = endPoint;

        receivers = [
            new(Channel.Reliable),
            new(Channel.ReliableUnordered),
            new(Channel.Unreliable),
            new(Channel.Sequenced)
        ];
    }

    public long RetransmitCount {
        get {
            var total = 0L;

            foreach (var sender in senders) {
                total += sender.RetransmitCount;
            }

            return total;
        }
    }

    public ChannelSender Sender(Channel channel) => senders[(int)channel];

    public ChannelReceiver Receiver(Channel channel) => receivers[(int)channel];

    public void Clear() {
        foreach (var sender in senders) {
            sender.Clear();
        }

        foreach (var receiver in receivers) {
            receiver.Clear();
        }
    }
}

/// <summary>Copies an endpoint, because a socket may hand back one it intends to reuse.</summary>
static class EndPointExtensions {
    public static EndPoint Clone(this EndPoint endPoint) =>
        endPoint is IPEndPoint ip ? new IPEndPoint(ip.Address, ip.Port) : endPoint;
}
