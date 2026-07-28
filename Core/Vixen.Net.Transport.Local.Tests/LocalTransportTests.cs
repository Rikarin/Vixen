// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Net.Tests.Transport;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Transport.Local.Tests;

/// <summary>
///     What is true of the in-process transport specifically, over and above the contract every
///     transport meets — which is asserted by <see cref="TransportConformance" /> and inherited from
///     it in <c>Vixen.Net.Tests</c>.
/// </summary>
public sealed class LocalTransportTests {
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    [Fact]
    public void AListenServerIsOneTransportConnectedToItself() {
        var network = new LocalNetwork();
        using var host = new LocalTransport(network);
        var events = new EventRecorder();

        host.StartServer();
        host.StartClient();
        Pump(host, events);

        var asServer = events.Connects(TransportRole.Server);
        var asClient = events.Connects(TransportRole.Client);

        Assert.Single(asServer);
        Assert.Single(asClient);
        Assert.Equal(asServer[0], asClient[0]);

        // And it can talk to itself, which is what makes host mode the same code path as a
        // dedicated server rather than a special case in every layer above.
        events.Clear();
        host.SendToServer(Bytes("up"), Channel.Reliable);
        Pump(host, events);
        host.SendToClient(asServer[0], Bytes("down"), Channel.Reliable);
        Pump(host, events);

        Assert.Equal(["up"], events.Texts(TransportRole.Server));
        Assert.Equal(["down"], events.Texts(TransportRole.Client));
    }

    [Fact]
    public void TwoServersCannotHoldOneAddress() {
        var network = new LocalNetwork();
        using var first = new LocalTransport(network);
        using var second = new LocalTransport(network);

        first.StartServer();

        Assert.Throws<TransportException>(second.StartServer);
        Assert.Equal(TransportState.Stopped, second.ServerState);
        Assert.Equal(1, network.ListenerCount);
    }

    [Fact]
    public void AServerThatStopsGivesItsAddressBack() {
        var network = new LocalNetwork();
        using var first = new LocalTransport(network);
        using var second = new LocalTransport(network);

        first.StartServer();
        first.StopServer();
        second.StartServer();

        Assert.True(network.IsListening(LocalNetwork.DefaultAddress));
        Assert.Equal(1, network.ListenerCount);
        Assert.Equal(TransportState.Running, second.ServerState);
    }

    [Fact]
    public void AddressesAreHowAClientPicksBetweenTwoServers() {
        var network = new LocalNetwork();
        using var lobby = new LocalTransport(network, "lobby");
        using var match = new LocalTransport(network, "match");
        using var client = new LocalTransport(network, "match");
        var lobbyEvents = new EventRecorder();
        var matchEvents = new EventRecorder();
        var clientEvents = new EventRecorder();

        lobby.StartServer();
        match.StartServer();
        client.StartClient();

        for (var i = 0; i < 3; i++) {
            client.Poll(Step, clientEvents);
            lobby.Poll(Step, lobbyEvents);
            match.Poll(Step, matchEvents);
        }

        Assert.Single(matchEvents.Connects(TransportRole.Server));
        Assert.Empty(lobbyEvents.Connects(TransportRole.Server));
    }

    [Fact]
    public void TwoNetworksAreTwoWorlds() {
        var here = new LocalNetwork();
        var elsewhere = new LocalNetwork();
        using var server = new LocalTransport(here);
        using var client = new LocalTransport(elsewhere);
        var serverEvents = new EventRecorder();
        var clientEvents = new EventRecorder();

        server.StartServer();
        client.StartClient();

        for (var i = 0; i < 3; i++) {
            client.Poll(Step, clientEvents);
            server.Poll(Step, serverEvents);
        }

        Assert.Equal(
            [(ConnectionId.None, DisconnectReason.ConnectionRefused)],
            clientEvents.Disconnects(TransportRole.Client)
        );

        Assert.Empty(serverEvents.Events);
    }

    [Fact]
    public void ConnectingToAServerThatHasStopped_IsRefused() {
        var network = new LocalNetwork();
        using var server = new LocalTransport(network);
        using var client = new LocalTransport(network);
        var clientEvents = new EventRecorder();

        server.StartServer();
        server.StopServer();
        client.StartClient();
        client.Poll(Step, clientEvents);

        Assert.Equal(
            [(ConnectionId.None, DisconnectReason.ConnectionRefused)],
            clientEvents.Disconnects(TransportRole.Client)
        );
    }

    [Fact]
    public void TheServerCountsItsConnections() {
        var network = new LocalNetwork();
        using var server = new LocalTransport(network);
        using var first = new LocalTransport(network);
        using var second = new LocalTransport(network);
        var events = new EventRecorder();

        server.StartServer();
        Assert.Equal(0, server.ConnectionCount);

        first.StartClient();
        second.StartClient();
        first.Poll(Step, events);
        second.Poll(Step, events);

        Assert.Equal(2, server.ConnectionCount);

        first.StopClient();

        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public void PollIsNotReentrant_AndSaysSoRatherThanCorruptingItself() {
        var network = new LocalNetwork();
        using var server = new LocalTransport(network);
        using var client = new LocalTransport(network);

        server.StartServer();
        client.StartClient();

        var reentrant = new ReentrantPoller(client);

        Assert.Throws<TransportException>(() => client.Poll(Step, reentrant));
        Assert.True(reentrant.Reentered);
    }

    [Fact]
    public void ADisposedTransportRefusesToBePolled() {
        var network = new LocalNetwork();
        var transport = new LocalTransport(network);

        transport.StartServer();
        transport.Dispose();

        Assert.Equal(0, network.ListenerCount);
        Assert.Equal(TransportState.Stopped, transport.ServerState);
        Assert.Throws<ObjectDisposedException>(() => transport.Poll(Step, new EventRecorder()));
    }

    [Fact]
    public void DisposingAClientLeavesTheServerHoldingNothing() {
        var network = new LocalNetwork();
        using var server = new LocalTransport(network);
        var client = new LocalTransport(network);
        var serverEvents = new EventRecorder();
        var clientEvents = new EventRecorder();

        server.StartServer();
        client.StartClient();
        client.Poll(Step, clientEvents);
        server.Poll(Step, serverEvents);
        serverEvents.Clear();

        client.Dispose();
        server.Poll(Step, serverEvents);

        Assert.Equal(0, server.ConnectionCount);

        var disconnects = serverEvents.Disconnects(TransportRole.Server);

        Assert.Single(disconnects);
        Assert.Equal(DisconnectReason.RemoteRequested, disconnects[0].Reason);
    }

    [Fact]
    public void APayloadSentToATransportThatWentAway_IsDropped() {
        var network = new LocalNetwork();
        using var server = new LocalTransport(network);
        var client = new LocalTransport(network);
        var serverEvents = new EventRecorder();
        var clientEvents = new EventRecorder();

        server.StartServer();
        client.StartClient();
        client.Poll(Step, clientEvents);
        server.Poll(Step, serverEvents);

        var connection = serverEvents.Connects(TransportRole.Server)[0];

        client.Dispose();
        server.SendToClient(connection, Bytes("into the void"), Channel.Reliable);
        server.Poll(Step, serverEvents);

        Assert.Empty(serverEvents.Payloads(TransportRole.Server));
    }

    static void Pump(LocalTransport transport, EventRecorder events, int rounds = 2) {
        for (var round = 0; round < rounds; round++) {
            transport.Poll(Step, events);
        }
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Polls the transport again from inside its own dispatch, which is not allowed.</summary>
    sealed class ReentrantPoller(LocalTransport transport) : ITransportEvents {
        public bool Reentered { get; private set; }

        public void OnConnected(TransportRole role, ConnectionId connection) {
            Reentered = true;
            transport.Poll(TimeSpan.Zero, this);
        }

        public void OnDisconnected(TransportRole role, ConnectionId connection, DisconnectReason reason) {
        }

        public void OnData(TransportRole role, ConnectionId connection, Channel channel, ReadOnlySpan<byte> payload) {
        }
    }
}
