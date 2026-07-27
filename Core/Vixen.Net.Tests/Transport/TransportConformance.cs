// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Tests.Transport;

/// <summary>
///     Everything <see cref="ITransport" /> promises, as tests rather than as prose.
/// </summary>
/// <remarks>
///     <para>
///         A transport is substitutable or it is nothing: the session, replication and RPC layers
///         are written once against this interface, and the difference between a listen server and a
///         relayed match is meant to be the constructor call. That is only true if every
///         implementation agrees on the things this file asserts — who is numbered, who is told
///         what when a connection ends, what a channel guarantees, when a payload stops being
///         readable.
///     </para>
///     <para>
///         So the suite is abstract and lives beside the interface rather than beside any one
///         implementation. <c>Vixen.Net.Transport.Udp.Tests</c>, and an addon transport in somebody
///         else's repository, inherit it and are held to the same contract as the in-process
///         transport that everything else is developed against.
///     </para>
/// </remarks>
public abstract class TransportConformance : IDisposable {
    /// <summary>How much time one <see cref="Pump" /> round tells a transport has passed.</summary>
    protected static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    readonly List<Peer> clients = [];

    /// <summary>The server under test. Created, not started — a test starts what it needs.</summary>
    protected ITransport Server { get; }

    /// <summary>What the server has reported.</summary>
    protected EventRecorder ServerEvents { get; } = new();

    /// <summary>Creates the server transport.</summary>
    protected TransportConformance() => Server = CreateServer();

    /// <summary>Makes a transport that can listen for the clients <see cref="CreateClient" /> makes.</summary>
    /// <returns>A transport that has not been started.</returns>
    protected abstract ITransport CreateServer();

    /// <summary>Makes a transport that can reach the server <see cref="CreateServer" /> made.</summary>
    /// <returns>A transport that has not been started.</returns>
    protected abstract ITransport CreateClient();

    /// <summary>Disposes every transport the test made.</summary>
    public void Dispose() {
        foreach (var client in clients) {
            client.Transport.Dispose();
        }

        Server.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Connect_IsReportedOnBothHalves() {
        Server.StartServer();
        var client = NewClient();
        client.Transport.StartClient();

        Pump();

        var onServer = ServerEvents.Connects(TransportRole.Server);
        var onClient = client.Events.Connects(TransportRole.Client);

        Assert.Single(onServer);
        Assert.Single(onClient);
        Assert.True(onServer[0].IsValid);

        // The client is told the number the server gave it, not a number of its own.
        Assert.Equal(onServer[0], onClient[0]);
        Assert.Equal(TransportState.Running, client.Transport.ClientState);
    }

    [Fact]
    public void Connect_WithNothingListening_IsRefused() {
        var client = NewClient();
        client.Transport.StartClient();

        Pump();

        var disconnects = client.Events.Disconnects(TransportRole.Client);

        Assert.Single(disconnects);
        Assert.Equal(DisconnectReason.ConnectionRefused, disconnects[0].Reason);
        Assert.Equal(ConnectionId.None, disconnects[0].Connection);
        Assert.Equal(TransportState.Stopped, client.Transport.ClientState);
        Assert.Empty(client.Events.Connects(TransportRole.Client));
    }

    [Fact]
    public void StartClient_WhileAlreadyRunning_Throws() {
        var client = ConnectClient();

        Assert.Throws<TransportException>(client.Transport.StartClient);
    }

    [Fact]
    public void StartServer_WhileAlreadyListening_Throws() {
        Server.StartServer();

        Assert.Throws<TransportException>(Server.StartServer);
    }

    [Theory]
    [InlineData(Channel.Reliable)]
    [InlineData(Channel.ReliableUnordered)]
    [InlineData(Channel.Unreliable)]
    [InlineData(Channel.Sequenced)]
    public void PayloadFromClient_ArrivesAtTheServer_OnTheChannelItWasSentOn(Channel channel) {
        var client = ConnectClient();

        client.Transport.SendToServer(Bytes("from the client"), channel);
        Pump();

        var received = ServerEvents.Payloads(TransportRole.Server);

        Assert.Single(received);
        Assert.Equal("from the client", received[0].Text);
        Assert.Equal(channel, received[0].Channel);
        Assert.Equal(client.Id, received[0].Connection);
    }

    [Theory]
    [InlineData(Channel.Reliable)]
    [InlineData(Channel.ReliableUnordered)]
    [InlineData(Channel.Unreliable)]
    [InlineData(Channel.Sequenced)]
    public void PayloadFromServer_ArrivesAtTheClient_OnTheChannelItWasSentOn(Channel channel) {
        var client = ConnectClient();

        Server.SendToClient(client.Id, Bytes("from the server"), channel);
        Pump();

        var received = client.Events.Payloads(TransportRole.Client);

        Assert.Single(received);
        Assert.Equal("from the server", received[0].Text);
        Assert.Equal(channel, received[0].Channel);
        Assert.Equal(client.Id, received[0].Connection);
    }

    [Fact]
    public void ReliableChannel_DeliversEverythingInOrder() {
        var client = ConnectClient();
        var expected = new List<string>();

        for (var i = 0; i < 64; i++) {
            var text = i.ToString(CultureInfo.InvariantCulture);
            expected.Add(text);
            client.Transport.SendToServer(Bytes(text), Channel.Reliable);
        }

        Pump();

        Assert.Equal(expected, ServerEvents.Texts(TransportRole.Server));
    }

    [Fact]
    public void AnEmptyPayload_IsStillAPayload() {
        var client = ConnectClient();

        client.Transport.SendToServer([], Channel.Reliable);
        Pump();

        var received = ServerEvents.Payloads(TransportRole.Server);

        Assert.Single(received);
        Assert.Empty(received[0].Payload);
    }

    [Fact]
    public void APayloadOfTheLargestSizeAllowed_IsCarried() {
        var client = ConnectClient();
        var payload = new byte[Server.Capabilities.MaxPayloadBytes];
        payload[^1] = 42;

        client.Transport.SendToServer(payload, Channel.Reliable);
        Pump();

        var received = ServerEvents.Payloads(TransportRole.Server);

        Assert.Single(received);
        Assert.Equal(payload.Length, received[0].Payload.Length);
        Assert.Equal(42, received[0].Payload[^1]);
    }

    [Fact]
    public void APayloadLargerThanAllowed_Throws() {
        var client = ConnectClient();
        var payload = new byte[Server.Capabilities.MaxPayloadBytes + 1];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => client.Transport.SendToServer(payload, Channel.Reliable)
        );
    }

    [Fact]
    public void APayloadIsCopiedWhenItIsSent_NotWhenItIsDelivered() {
        var client = ConnectClient();
        var buffer = Bytes("original");

        client.Transport.SendToServer(buffer, Channel.Reliable);
        buffer.AsSpan().Fill((byte)'X');
        Pump();

        Assert.Equal("original", ServerEvents.Payloads(TransportRole.Server)[0].Text);
    }

    [Fact]
    public void TwoClients_AreNumberedApart_AndDoNotReadEachOthersPayloads() {
        var first = ConnectClient();
        var second = ConnectClient();

        Assert.NotEqual(first.Id, second.Id);

        Server.SendToClient(first.Id, Bytes("for the first"), Channel.Reliable);
        Pump();

        Assert.Equal(["for the first"], first.Events.Texts(TransportRole.Client));
        Assert.Empty(second.Events.Payloads(TransportRole.Client));
    }

    [Fact]
    public void AClientLeaving_IsRequestedOnItsSideAndRemoteOnTheServers() {
        var client = ConnectClient();

        client.Transport.StopClient();
        Pump();

        Assert.Equal([(client.Id, DisconnectReason.Requested)], client.Events.Disconnects(TransportRole.Client));

        Assert.Equal(
            [(client.Id, DisconnectReason.RemoteRequested)],
            ServerEvents.Disconnects(TransportRole.Server)
        );

        Assert.Equal(TransportState.Stopped, client.Transport.ClientState);
    }

    [Fact]
    public void AClientTheServerCloses_IsToldItWasKicked() {
        var client = ConnectClient();

        Server.Disconnect(client.Id);
        Pump();

        Assert.Equal([(client.Id, DisconnectReason.Kicked)], client.Events.Disconnects(TransportRole.Client));
        Assert.Equal([(client.Id, DisconnectReason.Requested)], ServerEvents.Disconnects(TransportRole.Server));
        Assert.Equal(TransportState.Stopped, client.Transport.ClientState);
    }

    [Fact]
    public void AServerStopping_TakesEveryConnectionWithIt() {
        var first = ConnectClient();
        var second = ConnectClient();

        Server.StopServer();
        Pump();

        Assert.Equal([(first.Id, DisconnectReason.ServerStopped)], first.Events.Disconnects(TransportRole.Client));

        Assert.Equal([(second.Id, DisconnectReason.ServerStopped)], second.Events.Disconnects(TransportRole.Client));

        var onServer = ServerEvents.Disconnects(TransportRole.Server);

        Assert.Equal(2, onServer.Count);
        Assert.All(onServer, entry => Assert.Equal(DisconnectReason.ServerStopped, entry.Reason));
        Assert.Equal(TransportState.Stopped, Server.ServerState);
    }

    [Fact]
    public void SendingToAConnectionThatIsNotOurs_IsIgnoredRatherThanThrowing() {
        Server.StartServer();

        Server.SendToClient(new(4242), Bytes("nobody"), Channel.Reliable);
        Pump();

        Assert.Empty(ServerEvents.Events);
    }

    [Fact]
    public void SendingAfterTheConnectionEnded_IsIgnoredRatherThanThrowing() {
        var client = ConnectClient();

        client.Transport.StopClient();
        Pump();
        ServerEvents.Clear();
        client.Events.Clear();

        // The frame that sends does not know yet, and being right one frame later is not a bug.
        client.Transport.SendToServer(Bytes("too late"), Channel.Reliable);
        Server.SendToClient(client.Id, Bytes("also too late"), Channel.Reliable);
        Pump();

        Assert.Empty(ServerEvents.Payloads(TransportRole.Server));
        Assert.Empty(client.Events.Payloads(TransportRole.Client));
    }

    [Fact]
    public void PollingWhenNothingHappened_ReportsNothing() {
        var client = ConnectClient();

        Pump();

        Assert.Empty(ServerEvents.Events);
        Assert.Empty(client.Events.Events);
    }

    /// <summary>UTF-8, because a test that reads its own payloads is a test worth reading.</summary>
    /// <param name="text">The text to send.</param>
    /// <returns>Its UTF-8 bytes.</returns>
    protected static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Adds a client to the test, tracked for polling and disposal.</summary>
    /// <returns>The client, not yet started.</returns>
    protected Peer NewClient() {
        var peer = new Peer(CreateClient(), new());
        clients.Add(peer);

        return peer;
    }

    /// <summary>
    ///     Starts the server if it is not started, connects a new client to it, and forgets the
    ///     events that took, so a test asserts about what it does rather than about getting there.
    /// </summary>
    /// <returns>The connected client, with the id the server gave it.</returns>
    protected Peer ConnectClient() {
        if (Server.ServerState != TransportState.Running) {
            Server.StartServer();
        }

        var peer = NewClient();
        peer.Transport.StartClient();
        Pump();

        var connects = peer.Events.Connects(TransportRole.Client);

        Assert.Single(connects);
        peer.Id = connects[0];

        ServerEvents.Clear();

        foreach (var client in clients) {
            client.Events.Clear();
        }

        return peer;
    }

    /// <summary>
    ///     Polls the server and every client a few times, so that anything in flight anywhere gets
    ///     wherever it was going.
    /// </summary>
    /// <param name="rounds">
    ///     How many times round. The default is enough for a connect and a payload on top of it;
    ///     a test that injects latency says how long it is prepared to wait.
    /// </param>
    protected void Pump(int rounds = 4) {
        for (var round = 0; round < rounds; round++) {
            Server.Poll(Step, ServerEvents);

            foreach (var client in clients) {
                client.Transport.Poll(Step, client.Events);
            }
        }
    }

    /// <summary>A client transport, what it has reported, and the number the server gave it.</summary>
    /// <param name="Transport">The transport.</param>
    /// <param name="Events">What it has reported.</param>
    protected sealed record Peer(ITransport Transport, EventRecorder Events) {
        /// <summary>The connection id the server assigned, once it has connected.</summary>
        public ConnectionId Id { get; set; }
    }
}
