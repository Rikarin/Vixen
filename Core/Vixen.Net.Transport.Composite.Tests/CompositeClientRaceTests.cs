// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Sessions;
using Vixen.Net.Tests.Transport;
using Vixen.Net.Transport.Local;
using Xunit;

namespace Vixen.Net.Transport.Composite.Tests;

/// <summary>Racing the inner clients: several routes started, one connection above them.</summary>
/// <remarks>
///     <para>
///         <b>The claim every test here holds is that nothing above learns a race happened.</b> One
///         connect, with one id, and a loser's connect, bytes and disconnect swallowed — which is what
///         makes the feature implementable at all, because <c>NetworkSession</c>'s client arm is built
///         for exactly one connect per lifetime and a pure client treats a disconnect as the end of
///         the session.
///     </para>
///     <para>
///         ⚠ <b>Nothing here waits for anything.</b> A stagger is counted in the <c>elapsed</c> each
///         <c>Poll</c> is handed, so every assertion below is about a number of polls and none of them
///         is about a duration — the difference between a test that is deterministic on a loaded
///         machine and this repository's largest source of flakes.
///     </para>
/// </remarks>
public sealed class CompositeClientRaceTests : IDisposable {
    const string Live = "live";
    const string AlsoLive = "also-live";
    const string Dead = "dead";

    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    readonly LocalNetwork network = new();
    readonly LocalTransport server;
    readonly EventRecorder events = new();

    public CompositeClientRaceTests() {
        server = new(network, Live);
        server.StartServer();
    }

    public void Dispose() => server.Dispose();

    /// <summary>A route that is refused loses, and the one that answers is the only one reported.</summary>
    /// <remarks>
    ///     The whole feature in one test: the dead route is tried first — a firewall that drops UDP —
    ///     and the layers above see a single connection and are never told about the failure.
    /// </remarks>
    [Fact]
    public void TheOnlyConnectAnythingAboveSeesIsTheWinners() {
        using var client = CompositeTransport.Racing([Route(Dead), Route(Live)]);

        client.StartClient();
        Pump(client);

        Assert.Single(events.Connects(TransportRole.Client));
        Assert.Empty(events.Disconnects(TransportRole.Client));
        Assert.Equal(TransportState.Running, client.ClientState);
    }

    /// <summary>The loser's refusal is not a disconnect above it, which would end the session.</summary>
    [Fact]
    public void ARaceThatOneRouteSurvivesReportsNoDisconnect() {
        using var client = CompositeTransport.Racing([Route(Dead), Route(Live)]);

        client.StartClient();
        Pump(client);

        // Refused, in the transport underneath — so the event existed and was deliberately not
        // forwarded, rather than never having happened.
        Assert.Equal(TransportState.Stopped, client.Transports[0].ClientState);
        Assert.Empty(events.Disconnects(TransportRole.Client));
    }

    /// <summary>Every route failing is the one failure a race does report, and it reports it once.</summary>
    [Fact]
    public void WhenEveryRouteFailsTheClientIsToldOnce() {
        using var client = CompositeTransport.Racing([Route(Dead), Route(Dead)]);

        client.StartClient();
        Pump(client);

        var disconnects = events.Disconnects(TransportRole.Client);

        Assert.Equal(DisconnectReason.ConnectionRefused, Assert.Single(disconnects).Reason);
        Assert.Empty(events.Connects(TransportRole.Client));
        Assert.Equal(TransportState.Stopped, client.ClientState);
    }

    /// <summary>A stagger is counted in polls, and the second route is not started before its turn.</summary>
    /// <remarks>
    ///     ⚠ The first candidate here never answers and never fails — the case a stagger exists for.
    ///     Fifteen steps of 16 ms is 240 ms and the sixteenth is 256 ms, so the assertion is about
    ///     which poll it is rather than about how long the test took.
    /// </remarks>
    [Fact]
    public void AStaggerIsCountedInPollsAndNotOnAClock() {
        var silent = new SilentTransport();
        using var client = CompositeTransport.Racing([silent, Route(Live)], TimeSpan.FromMilliseconds(250));

        client.StartClient();

        Assert.Equal(TransportState.Starting, silent.ClientState);

        for (var poll = 0; poll < 15; poll++) {
            client.Poll(Step, events);
        }

        Assert.Equal(TransportState.Starting, client.ClientState);
        Assert.Equal(TransportState.Stopped, client.Transports[1].ClientState);

        client.Poll(Step, events);

        Assert.Equal(TransportState.Running, client.ClientState);
        Assert.Single(events.Connects(TransportRole.Client));

        // ⚠ And the candidate that never answered is torn down by the win rather than left holding a
        // socket for the rest of the session. A loser that *connects* is stopped when its connect
        // arrives; one that is still trying is stopped by nothing else at all.
        Assert.Equal(TransportState.Stopped, silent.ClientState);
    }

    /// <summary>A route that fails hands over immediately rather than waiting out its stagger.</summary>
    /// <remarks>
    ///     A stagger exists to avoid a wasted connection attempt, not to make a route that is already
    ///     gone cost latency. The stagger here is thirty times the whole pump, so a race that waited
    ///     for it would connect to nothing.
    /// </remarks>
    [Fact]
    public void AFailedRouteDoesNotWaitOutItsStagger() {
        using var client = CompositeTransport.Racing([Route(Dead), Route(Live)], TimeSpan.FromSeconds(10));

        client.StartClient();
        Pump(client, rounds: 4);

        Assert.Single(events.Connects(TransportRole.Client));
    }

    /// <summary>A loser that connects anyway is stopped, and nothing above hears of it.</summary>
    /// <remarks>
    ///     ⚠ The interleaving that is easy to miss: both routes lead to a server that accepts, and the
    ///     inner transports are polled in order, so the second one reports its connect in the same
    ///     frame the race was already decided in.
    /// </remarks>
    [Fact]
    public void ALoserThatConnectsAnywayIsStoppedAndSilent() {
        using var second = new LocalTransport(network, AlsoLive);
        second.StartServer();

        using var client = CompositeTransport.Racing([Route(Live), Route(AlsoLive)]);

        client.StartClient();
        Pump(client);

        Assert.Single(events.Connects(TransportRole.Client));
        Assert.Empty(events.Disconnects(TransportRole.Client));
        Assert.Equal(TransportState.Stopped, client.Transports[1].ClientState);
        Assert.Same(client.Transports[0], client.ClientTransport);
    }

    /// <summary>The winner's own disconnect is forwarded, or a race would swallow everything.</summary>
    /// <remarks>
    ///     The instrument check for the three tests above: they assert that a disconnect did
    ///     <i>not</i> arrive, and a composite that forwarded no client disconnect at all would pass
    ///     every one of them.
    /// </remarks>
    [Fact]
    public void TheWinnersOwnDisconnectIsStillReported() {
        using var client = CompositeTransport.Racing([Route(Dead), Route(Live)]);

        client.StartClient();
        Pump(client);

        Assert.Single(events.Connects(TransportRole.Client));

        server.StopServer();
        Pump(client);

        Assert.Single(events.Disconnects(TransportRole.Client));
    }

    /// <summary>Capabilities are the smallest of every candidate, including before a winner exists.</summary>
    /// <remarks>
    ///     A caller sizing a buffer while the race is unresolved has to be able to hand it to whichever
    ///     route wins, so the answer cannot widen later — and this is the sentence #518 says is owed.
    /// </remarks>
    [Fact]
    public void CapabilitiesArePessimisticBeforeTheRaceResolves() {
        var small = new SilentTransport { Payload = 512 };
        using var client = CompositeTransport.Racing([small, Route(Live)], TimeSpan.FromSeconds(10));

        client.StartClient();

        Assert.Equal(TransportState.Starting, client.ClientState);
        Assert.Equal(512, client.Capabilities.MaxPayloadBytes);

        Pump(client);

        Assert.Equal(512, client.Capabilities.MaxPayloadBytes);
    }

    /// <summary>Starting a race that is already running is the same refusal any transport gives.</summary>
    [Fact]
    public void StartingARaceThatIsAlreadyRunningIsRefused() {
        using var client = CompositeTransport.Racing([Route(Dead), Route(Live)]);

        client.StartClient();

        Assert.Throws<TransportException>(client.StartClient);
    }

    /// <summary>A stagger that runs backwards is refused where it is given, not where it is used.</summary>
    [Fact]
    public void ANegativeStaggerIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CompositeTransport.Racing([new SilentTransport()], TimeSpan.FromMilliseconds(-1))
        );

    /// <summary>A composite that was not asked to race still drives exactly the one it was told to.</summary>
    [Fact]
    public void ACompositeThatWasNotAskedToRaceDrivesTheOneItWasTold() {
        using var client = new CompositeTransport([Route(Live), Route(Dead)], clientTransport: 1);

        client.StartClient();
        Pump(client);

        // The one it was told, which is the dead one — a single choice is a choice the caller made.
        Assert.Equal(
            DisconnectReason.ConnectionRefused,
            Assert.Single(events.Disconnects(TransportRole.Client)).Reason
        );
        Assert.Equal(TransportState.Stopped, client.Transports[0].ClientState);
    }

    /// <summary>A session over a racing composite joins once, and is never told a route died.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion #518 asks for, and the one that needs the layer above to exist.</b> A
    ///     pure client is driven to <see cref="SessionState.Stopped" /> by a disconnect and its client
    ///     arm starts a fresh handshake on every connect, so a composite that forwarded either half of
    ///     a losing attempt would show up here as a session that stopped or as a second handshake.
    /// </remarks>
    [Fact]
    public void ASessionOverARacingCompositeJoinsExactlyOnce() {
        using var hostTransport = new LocalTransport(network, AlsoLive);
        using var host = new NetworkSession(hostTransport, ownsTransport: false);
        host.StartServer();

        using var client = new NetworkSession(
            CompositeTransport.Racing([Route(Dead), Route(AlsoLive)]),
            ownsTransport: true
        );

        var connects = 0;
        var disconnects = 0;

        client.Connected += _ => connects++;
        client.Disconnected += _ => disconnects++;
        client.StartClient();

        for (var round = 0; round < 8; round++) {
            client.Update(Step);
            host.Update(Step);
        }

        Assert.Equal(1, connects);
        Assert.Equal(0, disconnects);
        Assert.NotNull(client.LocalPlayer);
        Assert.Equal(SessionState.Running, client.State);
        Assert.Single(host.Players);
    }

    LocalTransport Route(string address) => new(network, address);

    void Pump(CompositeTransport client, int rounds = 8) {
        for (var round = 0; round < rounds; round++) {
            client.Poll(Step, events);
            server.Poll(Step, new EventRecorder());
        }
    }

    /// <summary>A transport that is asked to connect and never answers, which is what a stagger is for.</summary>
    /// <remarks>
    ///     Deliberately not a <c>LocalTransport</c> pointed at nothing: that one is refused on its
    ///     first poll, which is the <i>other</i> failure. A route that hangs is the one a race cannot
    ///     resolve by waiting for an answer.
    /// </remarks>
    sealed class SilentTransport : ITransport {
        public int Payload { get; init; } = 1200;

        public TransportCapabilities Capabilities => new(Payload, IsInProcess: true, IsLossy: false);
        public TransportState ServerState { get; private set; }
        public TransportState ClientState { get; private set; }

        public void StartServer() => ServerState = TransportState.Running;
        public void StopServer() => ServerState = TransportState.Stopped;
        public void StartClient() => ClientState = TransportState.Starting;
        public void StopClient() => ClientState = TransportState.Stopped;
        public void Disconnect(ConnectionId connection) { }
        public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) { }
        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) { }
        public void Poll(TimeSpan elapsed, ITransportEvents events) { }
        public void Dispose() { }
    }
}
