// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using Vixen.Net.Messaging;
using Vixen.Net.Time;
using Vixen.Net.Transport;

namespace Vixen.Net.Sessions;

/// <summary>
///     A session: who is in it, what tick it is, and the handshake that decides both.
/// </summary>
/// <remarks>
///     <para>
///         This is the layer between the transport, which knows about connections and bytes, and
///         everything above, which wants to know about players and ticks. It owns exactly three
///         things — the handshake, the clock, and the player list — and hands every payload it does
///         not understand to an <see cref="ISessionMessageHandler" />.
///     </para>
///     <para>
///         <b>Nothing is dispatched before the handshake finishes.</b> A payload from a connection
///         that has not been accepted is dropped, not queued and not delivered late. Everything above
///         this layer may therefore assume that a payload it is handed came from a peer that agreed
///         on the protocol version and the content hash, and was let in by the authenticator.
///     </para>
///     <para>
///         <b>A player is not a connection.</b> When a connection drops, the player it carried stays
///         in the list with <see cref="NetworkPlayer.IsConnected" /> false for the length of
///         <see cref="SessionOptions.ReconnectWindow" />, holding their id and their slot. A client
///         that comes back with the token it was issued resumes as the same player. That is the
///         whole of reconnect support, and it is here rather than bolted on later because retrofitting
///         it means changing what every layer above means by "player".
///     </para>
///     <para>
///         <b>Host mode is not a special case.</b> <see cref="StartHost" /> starts both halves of one
///         transport, and the host's own client half does the same handshake through the loopback
///         that a remote client does over a socket. There is no offline path to rot.
///     </para>
///     <para>
///         Single-threaded, like everything else that runs in the frame: <see cref="Update" /> is
///         where all of it happens.
///     </para>
/// </remarks>
public sealed class NetworkSession : ITransportEvents, IDisposable {
    const int TokenBytes = 16;
    const int MaxIdentityBytes = 128;
    const int MaxReasonBytes = 256;

    readonly ITransport transport;
    readonly bool ownsTransport;
    readonly ISessionAuthenticator? authenticator;
    readonly byte[] scratch;

    readonly Dictionary<uint, NetworkPlayer> playersById = [];
    readonly Dictionary<uint, NetworkPlayer> playersByConnection = [];
    readonly Dictionary<string, PlayerId> reconnectTokens = new(StringComparer.Ordinal);
    readonly Dictionary<uint, Pending> pending = [];
    readonly List<NetworkPlayer> players = [];
    readonly List<uint> expired = [];
    readonly List<Pending> deciding = [];

    double now;
    uint nextPlayerId = 1;
    ISessionMessageHandler? handler;
    byte[] reconnectToken = [];

    // The client half's ping bookkeeping. The server keeps one of these per player.
    uint clientPingId;
    double clientPingSentAt;
    bool clientPingOutstanding;
    double clientNextPingAt;
    double clientLastHeardFrom;
    double handshakeSentAt;

    /// <summary>How the session behaves.</summary>
    public SessionOptions Options { get; }

    /// <summary>The transport underneath.</summary>
    public ITransport Transport => transport;

    /// <summary>The clock. On a client, the one being kept in step with the server's.</summary>
    public TickManager Clock { get; }

    /// <summary>The tick the session is on.</summary>
    public Tick Tick => Clock.Current;

    /// <summary>What role this session is playing.</summary>
    public SessionTopology Topology { get; private set; }

    /// <summary>Where the session is in its life.</summary>
    public SessionState State { get; private set; }

    /// <summary>Whether this session is the authority.</summary>
    public bool IsServer => Topology is SessionTopology.Server or SessionTopology.Host or SessionTopology.Offline;

    /// <summary>Whether this session has a player in the game.</summary>
    public bool IsClient => Topology is SessionTopology.Client or SessionTopology.Host or SessionTopology.Offline;

    /// <summary>Our own player, on anything that is not a dedicated server.</summary>
    public NetworkPlayer? LocalPlayer { get; private set; }

    /// <summary>Everybody in the session, connected or inside their reconnect window.</summary>
    public IReadOnlyList<NetworkPlayer> Players => players;

    /// <summary>
    ///     The token this client was issued. Keep a copy of it: presenting it to
    ///     <see cref="PresentReconnectToken" /> before connecting again is what makes the server give
    ///     you back the same <see cref="PlayerId" /> rather than a new one.
    /// </summary>
    public ReadOnlySpan<byte> ReconnectToken => reconnectToken;

    /// <summary>A player joined for the first time.</summary>
    public event Action<NetworkPlayer>? PlayerJoined;

    /// <summary>A player dropped into their reconnect window, or came back out of it.</summary>
    public event Action<NetworkPlayer>? PlayerConnectionChanged;

    /// <summary>A player left for good.</summary>
    public event Action<NetworkPlayer, PlayerLeaveReason>? PlayerLeft;

    /// <summary>This client was let in.</summary>
    public event Action<NetworkPlayer>? Connected;

    /// <summary>This client was refused, with the reason the server gave.</summary>
    public event Action<SessionRejectReason, string>? Rejected;

    /// <summary>This client's connection ended.</summary>
    public event Action<DisconnectReason>? Disconnected;

    /// <summary>Creates a session over a transport.</summary>
    /// <param name="transport">The transport to run on. Not started here.</param>
    /// <param name="options">How the session behaves.</param>
    /// <param name="authenticator">Who decides which clients are allowed in. Everybody, if null.</param>
    /// <param name="ownsTransport">Whether disposing this disposes the transport.</param>
    public NetworkSession(
        ITransport transport,
        SessionOptions? options = null,
        ISessionAuthenticator? authenticator = null,
        bool ownsTransport = false
    ) {
        this.transport = transport;
        this.authenticator = authenticator;
        this.ownsTransport = ownsTransport;

        Options = options ?? new SessionOptions();
        Clock = new(Options.TickRate);
        scratch = new byte[transport.Capabilities.MaxPayloadBytes];
    }

    /// <summary>Starts listening, without playing.</summary>
    public void StartServer() => Start(SessionTopology.Server);

    /// <summary>Connects to a server.</summary>
    public void StartClient() => Start(SessionTopology.Client);

    /// <summary>
    ///     Presents a token issued by an earlier session, so the server gives back the same player
    ///     rather than a new one. Call it before connecting.
    /// </summary>
    /// <param name="token">The token, as it was read from <see cref="ReconnectToken" />.</param>
    /// <exception cref="InvalidOperationException">The session has already been started.</exception>
    /// <remarks>
    ///     A token the server does not recognise — expired, already used, or from a different server
    ///     — is not an error and does not refuse the connection. It gets a new player id, which is
    ///     exactly what "your seat was given away" should feel like.
    /// </remarks>
    public void PresentReconnectToken(ReadOnlySpan<byte> token) {
        if (State != SessionState.Stopped) {
            throw new InvalidOperationException("A reconnect token is presented before connecting, not after.");
        }

        reconnectToken = token.ToArray();
    }

    /// <summary>Listens and plays: one process, both halves, a loopback between them.</summary>
    public void StartHost() => Start(SessionTopology.Host);

    /// <summary>
    ///     Single player. Mechanically <see cref="StartHost" />; the difference is what the game means
    ///     by it, and that the transport it was given is one nobody else can reach.
    /// </summary>
    public void StartOffline() => Start(SessionTopology.Offline);

    /// <summary>Stops everything and empties the session.</summary>
    public void Stop() {
        if (State == SessionState.Stopped) {
            return;
        }

        State = SessionState.Stopping;

        if (IsClient) {
            transport.StopClient();
        }

        if (IsServer) {
            transport.StopServer();
        }

        for (var i = players.Count - 1; i >= 0; i--) {
            var player = players[i];
            players.RemoveAt(i);
            playersById.Remove(player.Id.Value);
            PlayerLeft?.Invoke(player, PlayerLeaveReason.SessionStopped);
        }

        playersByConnection.Clear();
        reconnectTokens.Clear();
        pending.Clear();
        LocalPlayer = null;
        reconnectToken = [];
        clientPingOutstanding = false;
        Topology = SessionTopology.None;
        State = SessionState.Stopped;
    }

    /// <summary>
    ///     Runs the session for a frame: polls the transport, finishes handshakes, measures round
    ///     trips, retires players whose reconnect window closed, and advances the clock.
    /// </summary>
    /// <param name="elapsed">Time since the last call.</param>
    /// <param name="messages">Where payloads the session does not own are handed.</param>
    /// <returns>
    ///     How many fixed ticks the caller owes — the same number the engine's fixed-step accumulator
    ///     would give, because on a client it is the one being corrected towards the server's clock.
    /// </returns>
    public int Update(TimeSpan elapsed, ISessionMessageHandler? messages = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);

        if (State == SessionState.Stopped) {
            return 0;
        }

        now += elapsed.TotalSeconds;
        handler = messages;

        try {
            transport.Poll(elapsed, this);
        } finally {
            handler = null;
        }

        ResolvePendingConnections();
        RetireLostPlayers();
        SendPings();

        return Clock.Advance(elapsed);
    }

    /// <summary>Sends to the server, from a client.</summary>
    /// <param name="payload">The bytes.</param>
    /// <param name="channel">The delivery guarantee wanted.</param>
    /// <returns>Whether it was sent — false if this session has no client half in the game.</returns>
    public bool SendToServer(ReadOnlySpan<byte> payload, Channel channel) {
        if (!IsClient || LocalPlayer is null) {
            return false;
        }

        if (!TryFrame(SystemMessage.User, payload, out var framed)) {
            return false;
        }

        transport.SendToServer(framed, channel);

        return true;
    }

    /// <summary>Sends to one player, from a server.</summary>
    /// <param name="player">Who to send to.</param>
    /// <param name="payload">The bytes.</param>
    /// <param name="channel">The delivery guarantee wanted.</param>
    /// <returns>Whether it was sent — false if they are not here, or not connected right now.</returns>
    public bool SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) {
        if (!IsServer || !playersById.TryGetValue(player.Value, out var target) || !target.IsConnected) {
            return false;
        }

        if (!TryFrame(SystemMessage.User, payload, out var framed)) {
            return false;
        }

        transport.SendToClient(target.Connection, framed, channel);

        return true;
    }

    /// <summary>Sends to every connected player, from a server.</summary>
    /// <param name="payload">The bytes.</param>
    /// <param name="channel">The delivery guarantee wanted.</param>
    /// <returns>How many players it went to.</returns>
    public int SendToAll(ReadOnlySpan<byte> payload, Channel channel) {
        if (!IsServer || !TryFrame(SystemMessage.User, payload, out var framed)) {
            return 0;
        }

        var sent = 0;

        foreach (var player in players) {
            if (player.IsConnected) {
                transport.SendToClient(player.Connection, framed, channel);
                sent++;
            }
        }

        return sent;
    }

    /// <summary>Removes a player and closes their connection. No reconnect window: a kick is final.</summary>
    /// <param name="player">Who to remove.</param>
    /// <param name="reason">Why, which they are told.</param>
    /// <returns>Whether there was such a player.</returns>
    public bool Kick(PlayerId player, string reason = "") {
        if (!IsServer || !playersById.TryGetValue(player.Value, out var target)) {
            return false;
        }

        if (target.IsConnected) {
            SendReject(target.Connection, SessionRejectReason.None, reason);
            transport.Disconnect(target.Connection);
        }

        Remove(target, PlayerLeaveReason.Kicked);

        return true;
    }

    /// <summary>Finds a player.</summary>
    /// <param name="id">Who to look for.</param>
    /// <param name="player">Them, if they are here.</param>
    /// <returns>Whether they are here.</returns>
    public bool TryGetPlayer(PlayerId id, out NetworkPlayer? player) => playersById.TryGetValue(id.Value, out player);

    /// <summary>Stops the session, and the transport if it owns it.</summary>
    public void Dispose() {
        Stop();

        if (ownsTransport) {
            transport.Dispose();
        }
    }

    void ITransportEvents.OnConnected(TransportRole role, ConnectionId connection) {
        if (role == TransportRole.Server) {
            // Nothing is a player yet — a connection is a request to be let in, not an arrival.
            pending[connection.Value] = new(connection, now + Options.AuthenticationTimeout.TotalSeconds);

            return;
        }

        clientLastHeardFrom = now;
        handshakeSentAt = now;
        SendConnectRequest();
    }

    void ITransportEvents.OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) {
        if (role == TransportRole.Server) {
            pending.Remove(connection.Value);

            if (playersByConnection.Remove(connection.Value, out var player)) {
                LoseConnection(player, reason);
            }

            return;
        }

        if (State != SessionState.Stopped) {
            var local = LocalPlayer;
            LocalPlayer = null;
            clientPingOutstanding = false;

            if (Topology == SessionTopology.Client) {
                State = SessionState.Stopped;

                // A pure client's player list is exactly itself, and the next acceptance rebuilds it
                // — with whatever id that server hands out, which need not be the one before. Left
                // here, the old record is a player nothing will ever look up again, and a client
                // that reconnects a hundred times over a long session holds a hundred of them.
                //
                // Only for a pure client. On a host the same list is the server's, and clearing it
                // because the loopback client half dropped would remove everybody in the game.
                if (local is not null) {
                    players.Remove(local);
                    playersById.Remove(local.Id.Value);
                }
            }

            Disconnected?.Invoke(reason);
        }
    }

    void ITransportEvents.OnData(
        TransportRole role,
        ConnectionId connection,
        Channel channel,
        ReadOnlySpan<byte> payload
    ) {
        var reader = new PacketReader(payload);

        if (!reader.TryReadByte(out var raw)) {
            return;
        }

        var message = (SystemMessage)raw;

        if (role == TransportRole.Server) {
            OnServerData(connection, channel, message, ref reader);
        } else {
            OnClientData(channel, message, ref reader);
        }
    }

    void OnServerData(ConnectionId connection, Channel channel, SystemMessage message, ref PacketReader reader) {
        if (playersByConnection.TryGetValue(connection.Value, out var player)) {
            player.LastHeardFrom = now;

            switch (message) {
                case SystemMessage.Ping:
                    ReplyToPing(connection, ref reader);

                    break;

                case SystemMessage.Pong:
                    if (reader.TryReadUInt32(out var id) && player.PingOutstanding && id == player.PingId) {
                        player.PingOutstanding = false;
                        player.RoundTrip.Add(TimeSpan.FromSeconds(Math.Max(0, now - player.PingSentAt)));
                    }

                    break;

                case SystemMessage.User:
                    handler?.OnMessage(player.Id, channel, reader.RemainingBytes);

                    break;

                default:
                    // A second handshake on a live connection, or a message only a server sends.
                    // Neither is something a client of ours does.
                    break;
            }

            return;
        }

        if (message == SystemMessage.ConnectRequest && pending.TryGetValue(connection.Value, out var request)) {
            BeginAuthentication(connection, request, ref reader);
        }

        // Anything else from a connection that has not been let in is dropped without comment.
    }

    void OnClientData(Channel channel, SystemMessage message, ref PacketReader reader) {
        clientLastHeardFrom = now;

        switch (message) {
            case SystemMessage.ConnectAccepted:
                Accept(ref reader);

                break;

            case SystemMessage.ConnectRejected:
                if (reader.TryReadByte(out var raw) && reader.TryReadString(MaxReasonBytes, out var reason)) {
                    Rejected?.Invoke((SessionRejectReason)raw, reason);
                }

                transport.StopClient();

                break;

            case SystemMessage.Ping:
                ReplyToPing(ConnectionId.None, ref reader);

                break;

            case SystemMessage.Pong:
                if (reader.TryReadUInt32(out var id)
                    && reader.TryReadTick(out var serverTick)
                    && clientPingOutstanding
                    && id == clientPingId) {
                    clientPingOutstanding = false;
                    Clock.Synchronize(serverTick, TimeSpan.FromSeconds(Math.Max(0, now - clientPingSentAt)));
                }

                break;

            case SystemMessage.User:
                if (LocalPlayer is not null) {
                    handler?.OnMessage(PlayerId.None, channel, reader.RemainingBytes);
                }

                break;

            default:
                break;
        }
    }

    void Start(SessionTopology topology) {
        if (State != SessionState.Stopped) {
            throw new InvalidOperationException($"The session is already {State}.");
        }

        Topology = topology;
        State = SessionState.Starting;
        now = 0;
        clientNextPingAt = Options.PingInterval.TotalSeconds;
        Clock.Reset(default);

        if (IsServer) {
            transport.StartServer();
            State = SessionState.Running;
        }

        if (IsClient) {
            transport.StartClient();

            if (!IsServer) {
                State = SessionState.Starting;
            }
        }
    }

    void BeginAuthentication(ConnectionId connection, Pending request, ref PacketReader reader) {
        if (!reader.TryReadVariable(out var protocol)
            || !reader.TryReadUInt64(out var contentHash)
            || !reader.TryReadBlob(Options.MaxAuthenticationPayloadBytes, out var authPayload)
            || !reader.TryReadBlob(TokenBytes, out var token)) {
            RejectPending(connection, SessionRejectReason.BadHandshake, "Malformed handshake.");

            return;
        }

        if (protocol != Options.ProtocolVersion) {
            RejectPending(
                connection,
                SessionRejectReason.ProtocolMismatch,
                $"This server speaks protocol {Options.ProtocolVersion}, and you speak {protocol}."
            );

            return;
        }

        if (contentHash != Options.ContentHash) {
            RejectPending(
                connection,
                SessionRejectReason.ContentMismatch,
                "This server is running different content than you are."
            );

            return;
        }

        var resuming = PlayerId.None;

        if (!token.IsEmpty
            && reconnectTokens.TryGetValue(Convert.ToHexString(token), out var owner)
            && playersById.TryGetValue(owner.Value, out var away)
            && !away.IsConnected) {
            resuming = owner;
        }

        request = request with { Resuming = resuming, Payload = authPayload.ToArray(), Asked = true };
        pending[connection.Value] = request;

        Decide(connection, request);
    }

    void ResolvePendingConnections() {
        if (pending.Count == 0) {
            return;
        }

        expired.Clear();
        deciding.Clear();

        // Snapshotted rather than enumerated: deciding admits or rejects, and both edit the very
        // dictionary being walked.
        foreach (var request in pending.Values) {
            deciding.Add(request);
        }

        foreach (var request in deciding) {
            if (request.Asked && Decide(request.Connection, request)) {
                continue;
            }

            if (now > request.Deadline) {
                expired.Add(request.Connection.Value);
            }
        }

        foreach (var key in expired) {
            RejectPending(
                new(key),
                SessionRejectReason.AuthenticationTimedOut,
                "The handshake did not complete in time."
            );
        }
    }

    bool Decide(ConnectionId connection, Pending request) {
        var decision = AuthenticationDecision.Accept;

        if (authenticator is not null) {
            var ask = new AuthenticationRequest(connection, request.Payload, request.Resuming.IsValid);
            decision = authenticator.Authenticate(in ask);
        }

        switch (decision.Outcome) {
            case AuthenticationOutcome.Pending:
                return false;

            case AuthenticationOutcome.Reject:
                RejectPending(connection, SessionRejectReason.AuthenticationFailed, decision.Reason);

                return true;

            default:
                Admit(connection, request, decision.Identity);

                return true;
        }
    }

    void Admit(ConnectionId connection, Pending request, string identity) {
        pending.Remove(connection.Value);

        NetworkPlayer player;

        if (request.Resuming.IsValid && playersById.TryGetValue(request.Resuming.Value, out var returning)) {
            player = returning;
            player.Connection = connection;
            player.ReconnectCount++;
            player.Identity = identity.Length == 0 ? player.Identity : identity;
        } else {
            if (players.Count >= Options.MaxPlayers) {
                RejectPending(connection, SessionRejectReason.ServerFull, "This server is full.");

                return;
            }

            player = new(new(nextPlayerId++), identity) {
                Connection = connection,
                JoinedAt = Clock.Current
            };

            playersById[player.Id.Value] = player;
            players.Add(player);
        }

        playersByConnection[connection.Value] = player;
        player.LastHeardFrom = now;
        player.ReconnectDeadline = 0;
        player.PingOutstanding = false;
        player.NextPingAt = now + Options.PingInterval.TotalSeconds;

        IssueReconnectToken(player);
        SendAccepted(connection, player);

        if (request.Resuming.IsValid) {
            PlayerConnectionChanged?.Invoke(player);
        } else {
            PlayerJoined?.Invoke(player);
        }
    }

    void LoseConnection(NetworkPlayer player, DisconnectReason reason) {
        player.Connection = ConnectionId.None;
        player.PingOutstanding = false;

        if (Options.ReconnectWindow <= TimeSpan.Zero) {
            Remove(player, reason == DisconnectReason.Timeout ? PlayerLeaveReason.TimedOut : PlayerLeaveReason.Disconnected);

            return;
        }

        player.ReconnectDeadline = now + Options.ReconnectWindow.TotalSeconds;
        PlayerConnectionChanged?.Invoke(player);
    }

    void RetireLostPlayers() {
        if (!IsServer) {
            if (IsClient
                && LocalPlayer is not null
                && now - clientLastHeardFrom > Options.Timeout.TotalSeconds) {
                transport.StopClient();
            }

            return;
        }

        for (var i = players.Count - 1; i >= 0; i--) {
            var player = players[i];

            if (player.IsConnected) {
                if (now - player.LastHeardFrom > Options.Timeout.TotalSeconds) {
                    transport.Disconnect(player.Connection);
                    playersByConnection.Remove(player.Connection.Value);
                    player.Connection = ConnectionId.None;
                    LoseConnection(player, DisconnectReason.Timeout);
                }

                continue;
            }

            if (player.ReconnectDeadline > 0 && now > player.ReconnectDeadline) {
                Remove(player, PlayerLeaveReason.ReconnectWindowExpired);
            }
        }
    }

    void Remove(NetworkPlayer player, PlayerLeaveReason reason) {
        players.Remove(player);
        playersById.Remove(player.Id.Value);

        if (player.Connection.IsValid) {
            playersByConnection.Remove(player.Connection.Value);
        }

        if (player.ReconnectToken.Length != 0) {
            reconnectTokens.Remove(Convert.ToHexString(player.ReconnectToken));
            player.ReconnectToken = [];
        }

        player.Connection = ConnectionId.None;
        PlayerLeft?.Invoke(player, reason);
    }

    void SendPings() {
        if (IsServer) {
            foreach (var player in players) {
                if (!player.IsConnected || player.PingOutstanding || now < player.NextPingAt) {
                    continue;
                }

                player.PingId++;
                player.PingSentAt = now;
                player.PingOutstanding = true;
                player.NextPingAt = now + Options.PingInterval.TotalSeconds;
                SendPing(player.Connection, player.PingId);
            }
        }

        if (!IsClient || LocalPlayer is null || clientPingOutstanding || now < clientNextPingAt) {
            return;
        }

        clientPingId++;
        clientPingSentAt = now;
        clientPingOutstanding = true;
        clientNextPingAt = now + Options.PingInterval.TotalSeconds;
        SendPing(ConnectionId.None, clientPingId);
    }

    void Accept(ref PacketReader reader) {
        if (LocalPlayer is not null) {
            // A second acceptance on a connection that is already in the session. A server of ours
            // sends exactly one, so this is either a broken peer or one probing — and the cost of
            // believing it is a player record and two dictionary entries per packet, kept for ever,
            // for any id the sender cares to invent. The fuzzer measured that as fifty kilobytes
            // from a thirty-two byte packet, which is an amplifier a client hands out for free.
            //
            // The mirror of the rule OnServerData already keeps for a second handshake on a live
            // connection. Reconnecting still works: a disconnect clears LocalPlayer first.
            return;
        }

        if (!reader.TryReadVariable(out var playerId)
            || !reader.TryReadTick(out var serverTick)
            || !reader.TryReadBlob(TokenBytes, out var token)
            || !reader.TryReadString(MaxIdentityBytes, out var identity)) {
            return;
        }

        reconnectToken = token.ToArray();

        if (!playersById.TryGetValue(playerId, out var player)) {
            // A remote client: the server's player list is not ours to have, so the only player we
            // know about is us. A host already has the record its server half made.
            player = new(new(playerId), identity);
            playersById[playerId] = player;
            players.Add(player);
        }

        player.IsLocal = true;
        LocalPlayer = player;
        State = SessionState.Running;

        // The handshake is itself a round trip, and it is the only one measured before the first
        // ping — without it a client would run a whole second on a clock that assumed zero latency.
        Clock.Synchronize(serverTick, TimeSpan.FromSeconds(Math.Max(0, now - handshakeSentAt)));

        Connected?.Invoke(player);
    }

    void IssueReconnectToken(NetworkPlayer player) {
        if (player.ReconnectToken.Length != 0) {
            reconnectTokens.Remove(Convert.ToHexString(player.ReconnectToken));
        }

        // A new token every time they connect, so a token that leaked is worth one reconnection at
        // most and stops working the moment its owner uses it.
        var token = new byte[TokenBytes];
        RandomNumberGenerator.Fill(token);
        player.ReconnectToken = token;
        reconnectTokens[Convert.ToHexString(token)] = player.Id;
    }

    void SendConnectRequest() {
        var writer = new PacketWriter(scratch);
        writer.WriteByte((byte)SystemMessage.ConnectRequest);
        writer.WriteVariable(Options.ProtocolVersion);
        writer.WriteUInt64(Options.ContentHash);
        writer.WriteBlob(Options.AuthenticationPayload);
        writer.WriteBlob(reconnectToken);

        if (writer.TryFinish(out var packet)) {
            transport.SendToServer(packet, Channel.Reliable);
        }
    }

    void SendAccepted(ConnectionId connection, NetworkPlayer player) {
        var writer = new PacketWriter(scratch);
        writer.WriteByte((byte)SystemMessage.ConnectAccepted);
        writer.WriteVariable(player.Id.Value);
        writer.WriteTick(Clock.Current);
        writer.WriteBlob(player.ReconnectToken);
        writer.WriteString(player.Identity);

        if (writer.TryFinish(out var packet)) {
            transport.SendToClient(connection, packet, Channel.Reliable);
        }
    }

    void SendReject(ConnectionId connection, SessionRejectReason reason, string text) {
        var writer = new PacketWriter(scratch);
        writer.WriteByte((byte)SystemMessage.ConnectRejected);
        writer.WriteByte((byte)reason);
        writer.WriteString(text);

        if (writer.TryFinish(out var packet)) {
            transport.SendToClient(connection, packet, Channel.Reliable);
        }
    }

    void RejectPending(ConnectionId connection, SessionRejectReason reason, string text) {
        pending.Remove(connection.Value);
        SendReject(connection, reason, text);
        transport.Disconnect(connection);
    }

    void SendPing(ConnectionId connection, uint id) {
        var writer = new PacketWriter(scratch);
        writer.WriteByte((byte)SystemMessage.Ping);
        writer.WriteUInt32(id);
        writer.WriteTick(Clock.Current);

        if (!writer.TryFinish(out var packet)) {
            return;
        }

        if (connection.IsValid) {
            transport.SendToClient(connection, packet, Channel.Unreliable);
        } else {
            transport.SendToServer(packet, Channel.Unreliable);
        }
    }

    void ReplyToPing(ConnectionId connection, ref PacketReader reader) {
        if (!reader.TryReadUInt32(out var id)) {
            return;
        }

        var writer = new PacketWriter(scratch);
        writer.WriteByte((byte)SystemMessage.Pong);
        writer.WriteUInt32(id);
        writer.WriteTick(Clock.Current);

        if (!writer.TryFinish(out var packet)) {
            return;
        }

        if (connection.IsValid) {
            transport.SendToClient(connection, packet, Channel.Unreliable);
        } else {
            transport.SendToServer(packet, Channel.Unreliable);
        }
    }

    bool TryFrame(SystemMessage message, ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> framed) {
        var writer = new PacketWriter(scratch);
        writer.WriteByte((byte)message);
        writer.WriteRaw(payload);

        return writer.TryFinish(out framed);
    }

    readonly record struct Pending(ConnectionId Connection, double Deadline) {
        public PlayerId Resuming { get; init; }
        public byte[] Payload { get; init; } = [];
        public bool Asked { get; init; }
    }
}
