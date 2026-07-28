// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Transport;

namespace Vixen.Net.Transport.Composite;

/// <summary>Several transports, listening at once, behind one.</summary>
/// <remarks>
///     <para>
///         <b>What it is for is one server accepting more than one kind of client.</b> A desktop build
///         should be on UDP and a browser build cannot be; a game that wants both has otherwise to run
///         two servers with two worlds, or pick one and make somebody suffer for it. Here the session,
///         replication and RPC layers see a single transport and never learn that half their players
///         arrived over TCP.
///     </para>
///     <para>
///         <b>Connection ids are rewritten, and that is the whole of the difficulty.</b> Each inner
///         transport numbers its own connections from one, so two of them will hand out the same
///         number for different players within the first second. This one hands out ids of its own and
///         keeps a map both ways, so the number a game sees is unique across every transport it is
///         listening on — which is what everything above assumes and what nothing above checks.
///     </para>
///     <para>
///         <b>The client half is a single choice, not a race.</b> Composing servers is the useful
///         direction: a client knows what it is and which address it was given. Starting every inner
///         client at once and keeping whichever answered first is a different feature — transport
///         fallback — and it belongs with the relay work rather than being smuggled in here, so a
///         composite used as a client drives exactly the one it was told to.
///     </para>
/// </remarks>
public sealed class CompositeTransport : ITransport {
    readonly ITransport[] inner;
    readonly int clientIndex;
    readonly Dictionary<uint, Route> routes = [];
    readonly Dictionary<int, Dictionary<uint, ConnectionId>> outward = [];
    readonly Router router;

    uint nextConnection = 1;

    /// <summary>The transports underneath, in the order they were given.</summary>
    public IReadOnlyList<ITransport> Transports => inner;

    /// <summary>Which of them a client half runs on.</summary>
    public ITransport ClientTransport => inner[clientIndex];

    /// <inheritdoc />
    /// <remarks>
    ///     The smallest payload any of them will carry, and the most pessimistic answer to both other
    ///     questions. A caller sizing a buffer from this has to be able to hand it to whichever
    ///     transport a given connection turns out to be on, and it does not get to know which.
    /// </remarks>
    public TransportCapabilities Capabilities { get; }

    /// <inheritdoc />
    public TransportState ServerState { get; private set; }

    /// <inheritdoc />
    public TransportState ClientState => inner[clientIndex].ClientState;

    /// <summary>Combines transports.</summary>
    /// <param name="transports">
    ///     Them, in a fixed order. A server starts every one; a client starts one of them.
    /// </param>
    /// <param name="clientTransport">
    ///     Which one a client half uses, as an index. The first, by default.
    /// </param>
    /// <exception cref="ArgumentException">There are none.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The client index is not one of them.</exception>
    public CompositeTransport(IReadOnlyList<ITransport> transports, int clientTransport = 0) {
        ArgumentNullException.ThrowIfNull(transports);

        if (transports.Count == 0) {
            throw new ArgumentException("A composite of nothing carries nothing.", nameof(transports));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(clientTransport);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(clientTransport, transports.Count);

        inner = [.. transports];
        clientIndex = clientTransport;
        router = new(this);

        var smallest = int.MaxValue;
        var inProcess = true;
        var lossy = false;

        foreach (var transport in inner) {
            smallest = Math.Min(smallest, transport.Capabilities.MaxPayloadBytes);
            inProcess &= transport.Capabilities.IsInProcess;
            lossy |= transport.Capabilities.IsLossy;
        }

        Capabilities = new(smallest, inProcess, lossy);

        for (var i = 0; i < inner.Length; i++) {
            outward[i] = [];
        }
    }

    /// <inheritdoc />
    public void StartServer() {
        if (ServerState != TransportState.Stopped) {
            throw new TransportException("The server half is already listening.");
        }

        var started = 0;

        try {
            for (; started < inner.Length; started++) {
                inner[started].StartServer();
            }
        } catch {
            // All or none. A composite half-listening is a game that accepts some of its players and
            // silently refuses the rest, which is worse than not starting.
            for (var i = 0; i < started; i++) {
                inner[i].StopServer();
            }

            throw;
        }

        ServerState = TransportState.Running;
    }

    /// <inheritdoc />
    public void StopServer() {
        if (ServerState == TransportState.Stopped) {
            return;
        }

        foreach (var transport in inner) {
            transport.StopServer();
        }

        ServerState = TransportState.Stopped;
    }

    /// <inheritdoc />
    public void StartClient() => inner[clientIndex].StartClient();

    /// <inheritdoc />
    public void StopClient() => inner[clientIndex].StopClient();

    /// <inheritdoc />
    public void Disconnect(ConnectionId connection) {
        if (routes.TryGetValue(connection.Value, out var route)) {
            inner[route.Transport].Disconnect(route.Inner);

            return;
        }

        // Not one of ours to translate: on a client half the id is the server's, and the inner
        // transport is the one that knows it.
        inner[clientIndex].Disconnect(connection);
    }

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) {
        if (routes.TryGetValue(connection.Value, out var route)) {
            inner[route.Transport].SendToClient(route.Inner, payload, channel);
        }
    }

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) =>
        inner[clientIndex].SendToServer(payload, channel);

    /// <inheritdoc />
    public void Poll(TimeSpan elapsed, ITransportEvents events) {
        ArgumentNullException.ThrowIfNull(events);

        router.Events = events;

        try {
            for (var i = 0; i < inner.Length; i++) {
                router.Transport = i;
                inner[i].Poll(elapsed, router);
            }
        } finally {
            router.Events = null;
        }
    }

    /// <summary>Disposes every transport underneath.</summary>
    public void Dispose() {
        foreach (var transport in inner) {
            transport.Dispose();
        }
    }

    ConnectionId Outward(int transport, ConnectionId innerId) {
        var map = outward[transport];

        if (map.TryGetValue(innerId.Value, out var existing)) {
            return existing;
        }

        var id = new ConnectionId(nextConnection++);
        map[innerId.Value] = id;
        routes[id.Value] = new(transport, innerId);

        return id;
    }

    bool TryOutward(int transport, ConnectionId innerId, out ConnectionId id) =>
        outward[transport].TryGetValue(innerId.Value, out id);

    void Forget(int transport, ConnectionId innerId) {
        if (outward[transport].Remove(innerId.Value, out var id)) {
            routes.Remove(id.Value);
        }
    }

    readonly record struct Route(int Transport, ConnectionId Inner);

    /// <summary>Translates one inner transport's events into the composite's numbering.</summary>
    /// <remarks>
    ///     One instance, re-pointed per transport per poll, because allocating an adapter for each
    ///     inner transport on every frame is a per-frame allocation in the one place the engine's
    ///     budget says there are none.
    /// </remarks>
    sealed class Router(CompositeTransport owner) : ITransportEvents {
        public ITransportEvents? Events { get; set; }
        public int Transport { get; set; }

        public void OnConnected(TransportRole role, ConnectionId connection) {
            if (role == TransportRole.Client) {
                // A client half's connection is the server's number, and there is only one inner
                // transport in play, so there is nothing to renumber and renumbering would break
                // the contract that says both ends agree about it.
                Events?.OnConnected(role, connection);

                return;
            }

            Events?.OnConnected(role, owner.Outward(Transport, connection));
        }

        public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) {
            if (role == TransportRole.Client) {
                Events?.OnDisconnected(role, connection, reason);

                return;
            }

            var id = owner.TryOutward(Transport, connection, out var known) ? known : connection;
            owner.Forget(Transport, connection);
            Events?.OnDisconnected(role, id, reason);
        }

        public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
            if (role == TransportRole.Client) {
                Events?.OnData(role, connection, channel, payload);

                return;
            }

            var id = owner.TryOutward(Transport, connection, out var known) ? known : connection;
            Events?.OnData(role, id, channel, payload);
        }
    }
}
