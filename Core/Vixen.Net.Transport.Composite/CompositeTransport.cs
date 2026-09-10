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
///         <b>The client half is a single choice unless it is asked to be a race.</b> Composing
///         servers is the useful direction, and a client that knows what it is and which address it
///         was given wants exactly the one it was told to — which is what the ordinary constructor
///         gives it. <see cref="Racing" /> is the other case, and it is one server rather than two:
///         a composite listening on UDP and on WebSocket 443 is reachable two ways, and a client
///         racing them is a client that gets through a corporate firewall that drops UDP.
///     </para>
///     <para>
///         ⚠ <b>A race is resolved on the first transport-level connect, and that is not the same
///         question as the first completed handshake.</b> A handshake is <c>NetworkSession</c>'s —
///         its <c>ConnectRequest</c> and <c>ConnectAccepted</c> — and an <see cref="ITransport" />
///         sees connections, disconnections and opaque bytes. The stronger transport-observable
///         signal is <i>first inbound data</i>, which is what would catch a middlebox that accepts
///         the connection and drops the payload; it is a different signal rather than an expensive
///         version of this one, and it is not implemented here.
///     </para>
///     <para>
///         <b>Nothing above ever learns that a race happened.</b> Only the winner's connect is
///         reported, and a loser's connect, data and disconnect are swallowed with the candidate
///         stopped — so the layers above see exactly one connection with one id, and never the second
///         <c>OnConnected</c> that <c>NetworkSession</c>'s client arm is not built for. That is the
///         property that makes this implementable without a session-level notion of an attempt.
///     </para>
/// </remarks>
public sealed class CompositeTransport : ITransport {
    readonly ITransport[] inner;
    readonly int clientIndex;
    readonly bool racing;
    readonly TimeSpan stagger;
    readonly bool[] failed;
    readonly Dictionary<uint, Route> routes = [];
    readonly Dictionary<int, Dictionary<uint, ConnectionId>> outward = [];
    readonly Router router;

    uint nextConnection = 1;

    // The race, all of which is meaningless unless `racing`. `winner` is the index of the candidate
    // whose connect was reported upwards, and -1 until one is; `started` is how many candidates have
    // been asked to connect, which is every one of them at once unless there is a stagger.
    int winner = -1;
    int candidatesStarted;
    bool raceRunning;
    TimeSpan sinceLastStart;
    DisconnectReason? failureToReport;

    /// <summary>The transports underneath, in the order they were given.</summary>
    public IReadOnlyList<ITransport> Transports => inner;

    /// <summary>Which of them a client half runs on.</summary>
    /// <remarks>
    ///     ⚠ <b>On a racing composite this is the winner, and the first candidate until there is
    ///     one.</b> There is no honest answer before the race resolves — every candidate is still a
    ///     possibility — so a caller that needs to know whether it has resolved asks
    ///     <see cref="ClientState" />, which is <see cref="TransportState.Starting" /> for exactly
    ///     that long.
    /// </remarks>
    public ITransport ClientTransport => inner[racing ? Math.Max(winner, 0) : clientIndex];

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         The smallest payload any of them will carry, and the most pessimistic answer to both
    ///         other questions. A caller sizing a buffer from this has to be able to hand it to
    ///         whichever transport a given connection turns out to be on, and it does not get to know
    ///         which.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Including on a racing client, before and after the race.</b> A caller that sizes
    ///         a buffer while the race is unresolved gets the smallest <c>MaxPayloadBytes</c> of every
    ///         candidate — including the ones that lose — and that answer does not widen when a winner
    ///         emerges, because a buffer already handed out cannot be told to grow. Pessimistic is the
    ///         correct answer here for the same reason it is on a server, and it is written down here
    ///         rather than discovered.
    ///     </para>
    /// </remarks>
    public TransportCapabilities Capabilities { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sum of the ones that count, and absent when none of them does.</b> A
    ///         composite is typically a UDP transport for desktop players and a WebSocket one for
    ///         browsers, and only the first of those can see a datagram go missing. Reporting all
    ///         zeroes for the second would be this transport saying the browsers have a perfect
    ///         link; leaving it out says nothing about them, which is what is true.
    ///     </para>
    ///     <para>
    ///         So what comes back covers the players on the transports that count and not the rest,
    ///         and both halves of each ratio are drawn from the same population — which is the
    ///         property that makes the number mean anything at all.
    ///     </para>
    /// </remarks>
    public TransportLoss? Loss {
        get {
            var total = default(TransportLoss);
            var measured = false;

            foreach (var transport in inner) {
                if (transport.Loss is not { } loss) {
                    continue;
                }

                measured = true;

                total = new(
                    total.Sent + loss.Sent,
                    total.Retransmitted + loss.Retransmitted,
                    total.Expected + loss.Expected,
                    total.Missing + loss.Missing
                );
            }

            return measured ? total : null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Per-link this is not a sum at all, which is what makes it the easier half.</b>
    ///     <see cref="Loss" /> has to decide what to do about the transports that count nothing;
    ///     a single connection lives on exactly one of them, so this routes the question the same
    ///     way <see cref="SendToClient" /> routes a payload and answers with whatever that transport
    ///     says — including null, when the browser's connection is on the WebSocket half.
    /// </remarks>
    public TransportLoss? LossFor(ConnectionId connection) {
        if (!connection.IsValid) {
            // A race that has not resolved has no link to answer about: every candidate is still a
            // possibility, and adding their counters together would be a reading belonging to nobody.
            return ClientHalf is { } client ? client.LossFor(ConnectionId.None) : null;
        }

        return routes.TryGetValue(connection.Value, out var route)
            ? inner[route.Transport].LossFor(route.Inner)
            : null;
    }

    /// <inheritdoc />
    public TransportState ServerState { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="TransportState.Starting" /> for as long as a race is unresolved, which is what
    ///     it means: asked to connect, not there yet, and not yet known which route it will be on.
    /// </remarks>
    public TransportState ClientState {
        get {
            if (!racing) {
                return inner[clientIndex].ClientState;
            }

            if (winner >= 0) {
                return inner[winner].ClientState;
            }

            return raceRunning ? TransportState.Starting : TransportState.Stopped;
        }
    }

    /// <summary>The transport the client half is on, or null while a race has not resolved.</summary>
    ITransport? ClientHalf => racing ? winner >= 0 ? inner[winner] : null : inner[clientIndex];

    /// <summary>Combines transports.</summary>
    /// <param name="transports">
    ///     Them, in a fixed order. A server starts every one; a client starts one of them.
    /// </param>
    /// <param name="clientTransport">
    ///     Which one a client half uses, as an index. The first, by default.
    /// </param>
    /// <exception cref="ArgumentException">There are none.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The client index is not one of them.</exception>
    public CompositeTransport(IReadOnlyList<ITransport> transports, int clientTransport = 0)
        : this(transports, clientTransport, race: false, TimeSpan.Zero) { }

    /// <summary>
    ///     Combines transports and races the client half across every one of them: whichever connects
    ///     first is the one the layers above ever hear about.
    /// </summary>
    /// <param name="transports">Them, in the order they are tried.</param>
    /// <param name="stagger">
    ///     How long to wait before starting the next candidate. Zero — the default — starts all of
    ///     them at once, which costs a wasted connection attempt per connect and resolves as fast as
    ///     the fastest route; a positive value tries them in order and costs the fallback path that
    ///     much latency.
    /// </param>
    /// <returns>The composite, with nothing started.</returns>
    /// <exception cref="ArgumentException">There are none.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stagger" /> is negative.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The stagger is counted in the <c>elapsed</c> <see cref="Poll" /> is handed and not
    ///         from a clock</b>, like everything else in this layer whose behaviour depends on time.
    ///         A wall-clock budget calibrated on an idle machine is this repository's largest source
    ///         of flaky tests; counted this way a race is a pure function of the calls made to it, so
    ///         a test that wants to watch a 250 ms fallback does it in sixteen steps rather than in
    ///         250 ms and sees the same thing every run.
    ///     </para>
    ///     <para>
    ///         <b>A candidate that fails does not wait out its stagger.</b> The next one is started
    ///         the moment the failure is observed, so a route that is refused immediately costs the
    ///         race nothing at all.
    ///     </para>
    /// </remarks>
    public static CompositeTransport Racing(IReadOnlyList<ITransport> transports, TimeSpan stagger = default) {
        ArgumentOutOfRangeException.ThrowIfLessThan(stagger, TimeSpan.Zero);

        return new(transports, clientTransport: 0, race: true, stagger);
    }

    CompositeTransport(IReadOnlyList<ITransport> transports, int clientTransport, bool race, TimeSpan stagger) {
        ArgumentNullException.ThrowIfNull(transports);

        if (transports.Count == 0) {
            throw new ArgumentException("A composite of nothing carries nothing.", nameof(transports));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(clientTransport);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(clientTransport, transports.Count);

        inner = [.. transports];
        clientIndex = clientTransport;
        racing = race;
        this.stagger = stagger;
        failed = new bool[inner.Length];
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
    /// <remarks>
    ///     On a racing composite this starts the first candidate — every one of them, when the
    ///     stagger is zero — and the winner is decided on the first connect any of them reports.
    /// </remarks>
    public void StartClient() {
        if (!racing) {
            inner[clientIndex].StartClient();

            return;
        }

        if (raceRunning || winner >= 0) {
            throw new TransportException("The client half is already running.");
        }

        Array.Clear(failed);
        winner = -1;
        candidatesStarted = 0;
        sinceLastStart = TimeSpan.Zero;
        failureToReport = null;
        raceRunning = true;

        StartCandidates();
    }

    /// <inheritdoc />
    public void StopClient() {
        if (!racing) {
            inner[clientIndex].StopClient();

            return;
        }

        // Every candidate, not just the winner: the losers of a race that resolved are already
        // stopped, and stopping a stopped half does nothing, but a race abandoned halfway through
        // still has live attempts on it.
        for (var i = 0; i < candidatesStarted; i++) {
            inner[i].StopClient();
        }

        raceRunning = false;
        winner = -1;
        candidatesStarted = 0;
        failureToReport = null;
        Array.Clear(failed);
    }

    /// <inheritdoc />
    public void Disconnect(ConnectionId connection) {
        if (routes.TryGetValue(connection.Value, out var route)) {
            inner[route.Transport].Disconnect(route.Inner);

            return;
        }

        // Not one of ours to translate: on a client half the id is the server's, and the inner
        // transport is the one that knows it. A race that has not resolved has no such transport,
        // and disconnecting something that is not there is not an error.
        ClientHalf?.Disconnect(connection);
    }

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) {
        if (routes.TryGetValue(connection.Value, out var route)) {
            inner[route.Transport].SendToClient(route.Inner, payload, channel);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Dropped rather than queued while a race is unresolved, which is the same answer
    ///     <see cref="ITransport" /> already gives for sending before the client half is connected —
    ///     and nothing above sends before it is told it connected.
    /// </remarks>
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) =>
        ClientHalf?.SendToServer(payload, channel);

    /// <inheritdoc />
    public void Poll(TimeSpan elapsed, ITransportEvents events) {
        ArgumentNullException.ThrowIfNull(events);

        if (raceRunning && winner < 0) {
            // Before the inner transports are polled, so a candidate started by this frame's stagger
            // gets its first poll in this frame rather than the next one.
            sinceLastStart += elapsed;
            StartCandidates();
        }

        router.Events = events;

        try {
            for (var i = 0; i < inner.Length; i++) {
                router.Transport = i;
                inner[i].Poll(elapsed, router);
            }
        } finally {
            router.Events = null;
        }

        if (failureToReport is { } reason) {
            // The one event a race is allowed to report other than its winner's connect: every route
            // failed, so there is nothing left to fall back to and the layers above are told the
            // attempt ended. Reported after the loop rather than inline, because a transport reports
            // a refused connect on a later poll and never from inside StartClient.
            failureToReport = null;
            events.OnDisconnected(TransportRole.Client, ConnectionId.None, reason);
        }
    }

    /// <summary>Disposes every transport underneath.</summary>
    public void Dispose() {
        foreach (var transport in inner) {
            transport.Dispose();
        }
    }

    /// <summary>Starts every candidate whose turn has come, and the first one always has.</summary>
    void StartCandidates() {
        while (candidatesStarted < inner.Length) {
            if (candidatesStarted > 0) {
                if (sinceLastStart < stagger) {
                    break;
                }

                // The remainder rather than zero, so one long frame can start two candidates and a
                // stagger shorter than a frame is not silently rounded up to one per frame.
                sinceLastStart -= stagger;
            }

            var candidate = candidatesStarted++;

            try {
                inner[candidate].StartClient();
            } catch (TransportException) {
                // A route that cannot even be started has lost, and this is the cheapest way to
                // lose. Everything else about it is the same as a connect that was refused.
                Lost(candidate, DisconnectReason.TransportError);
            }
        }
    }

    /// <summary>One candidate's attempt failed. Try the next, or give up if there is none.</summary>
    void Lost(int candidate, DisconnectReason reason) {
        failed[candidate] = true;

        // Straight to the next one: a stagger exists to avoid a wasted attempt, not to make a route
        // that is already gone cost latency. StartCandidates is not called from here — the loop in
        // it is what starts the next one when a candidate fails to start at all, and a candidate
        // that fails later is started by the next poll, which is the poll it would first be driven
        // by anyway.
        sinceLastStart = stagger;

        if (candidatesStarted < inner.Length) {
            return;
        }

        for (var i = 0; i < inner.Length; i++) {
            if (!failed[i]) {
                return;
            }
        }

        raceRunning = false;
        failureToReport = reason;
    }

    /// <summary>The first candidate to connect wins, and every other one is torn down.</summary>
    void Won(int candidate, ConnectionId connection, ITransportEvents? events) {
        winner = candidate;
        raceRunning = false;
        failureToReport = null;

        for (var i = 0; i < candidatesStarted; i++) {
            if (i != candidate && !failed[i]) {
                // ⚠ A loser that connects *after* the winner is a real interleaving — the inner
                // transports are polled in order, so a later one can report a connect in the same
                // frame the race was already decided in. It is stopped here or when it arrives, and
                // either way nothing above is told about it.
                inner[i].StopClient();
            }
        }

        events?.OnConnected(TransportRole.Client, connection);
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
                if (owner.racing) {
                    if (owner.winner >= 0) {
                        // A loser that got there anyway. Nothing above has heard of it and nothing
                        // above ever will; it is stopped and forgotten.
                        owner.inner[Transport].StopClient();

                        return;
                    }

                    owner.Won(Transport, connection, Events);

                    return;
                }

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
                if (owner.racing && owner.winner != Transport) {
                    if (owner.winner < 0) {
                        // ⚠ Swallowed rather than forwarded, and that is the whole point: a pure
                        // client is driven to SessionState.Stopped by a disconnect, so a losing
                        // candidate's would end a session that has not started. Nothing above was
                        // told this attempt existed, so nothing above needs telling that it failed.
                        owner.Lost(Transport, reason);
                    }

                    return;
                }

                Events?.OnDisconnected(role, connection, reason);

                return;
            }

            var id = owner.TryOutward(Transport, connection, out var known) ? known : connection;
            owner.Forget(Transport, connection);
            Events?.OnDisconnected(role, id, reason);
        }

        public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
            if (role == TransportRole.Client) {
                if (owner.racing && owner.winner != Transport) {
                    // A loser's bytes. Handing them up would be a payload from a connection the
                    // layers above were never told about, on a link that is being torn down.
                    return;
                }

                Events?.OnData(role, connection, channel, payload);

                return;
            }

            var id = owner.TryOutward(Transport, connection, out var known) ? known : connection;
            Events?.OnData(role, id, channel, payload);
        }
    }
}
