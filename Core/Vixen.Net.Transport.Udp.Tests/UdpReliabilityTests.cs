// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Text;
using Vixen.Net.Tests.Transport;
using Xunit;

namespace Vixen.Net.Transport.Udp.Tests;

/// <summary>What the transport builds out of a medium that promises nothing.</summary>
public sealed class UdpReliabilityTests : IDisposable {
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);
    static readonly IPEndPoint ListenAt = new(IPAddress.Loopback, 46000);

    readonly DatagramBus bus = new();
    readonly UdpTransport server;
    readonly UdpTransport client;
    readonly EventRecorder serverEvents = new();
    readonly EventRecorder clientEvents = new();

    readonly ConnectionId connection;

    public UdpReliabilityTests() {
        server = new(bus, new() { ListenEndPoint = ListenAt, RemoteEndPoint = ListenAt });
        client = new(bus, new() { ListenEndPoint = new(IPAddress.Loopback, 0), RemoteEndPoint = ListenAt });

        server.StartServer();
        client.StartClient();
        Pump(3);

        connection = Assert.Single(serverEvents.Connects(TransportRole.Server));
        serverEvents.Clear();
        clientEvents.Clear();
    }

    public void Dispose() {
        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public void ALostReliablePayloadIsSentAgainAndArrives() {
        DropTheNext(1);

        client.SendToServer(Bytes("important"), Channel.Reliable);
        Pump(2);

        Assert.Empty(serverEvents.Texts(TransportRole.Server));

        // Past the retransmission timeout it goes again, and this time nothing eats it.
        Pump(20);

        Assert.Equal(["important"], serverEvents.Texts(TransportRole.Server));
        Assert.True(client.RetransmitCount > 0);
    }

    [Fact]
    public void ALostUnreliablePayloadIsSimplyGone() {
        DropTheNext(1);

        client.SendToServer(Bytes("whatever"), Channel.Unreliable);
        Pump(30);

        // Nothing retransmits it, and nothing should: by the time it could arrive it would be stale,
        // which is the entire reason the channel exists.
        Assert.Empty(serverEvents.Texts(TransportRole.Server));
        Assert.Equal(0, client.RetransmitCount);
    }

    [Fact]
    public void AReliableChannelHoldsWhatArrivedEarlyUntilTheGapIsFilled() {
        DropTheNext(1);

        for (var i = 0; i < 4; i++) {
            client.SendToServer(Bytes(i.ToString(CultureInfo.InvariantCulture)), Channel.Reliable);
        }

        Pump(2);

        // Three of the four are here and none of them may be delivered: the first is missing, and
        // "in order" means the ones behind it wait.
        Assert.Empty(serverEvents.Texts(TransportRole.Server));

        Pump(20);

        Assert.Equal(["0", "1", "2", "3"], serverEvents.Texts(TransportRole.Server));
    }

    [Fact]
    public void AnUnorderedReliableChannelDeliversWhatArrivedWithoutWaiting() {
        DropTheNext(1);

        for (var i = 0; i < 4; i++) {
            client.SendToServer(Bytes(i.ToString(CultureInfo.InvariantCulture)), Channel.ReliableUnordered);
        }

        Pump(2);

        // The point of the channel: one loss delays one message rather than the queue behind it.
        Assert.Equal(["1", "2", "3"], serverEvents.Texts(TransportRole.Server));

        Pump(20);

        Assert.Equal(["1", "2", "3", "0"], serverEvents.Texts(TransportRole.Server));
    }

    [Fact]
    public void ASequencedChannelDropsAMessageThatArrivesAfterANewerOne() {
        // Hold the next datagram back until two more have gone past it, so the old one arrives
        // behind the new ones rather than in front of them.
        var held = bus.SentCount + 1;
        bus.DelayPattern = number => number == held ? 2 : 0;

        client.SendToServer(Bytes("old"), Channel.Sequenced);
        client.SendToServer(Bytes("new"), Channel.Sequenced);
        client.SendToServer(Bytes("newer"), Channel.Sequenced);
        Pump(6);

        var arrived = serverEvents.Texts(TransportRole.Server);

        Assert.DoesNotContain("old", arrived);
        Assert.Contains("newer", arrived);
    }

    [Fact]
    public void ADuplicatedDatagramIsDeliveredOnce() {
        bus.DuplicatePattern = _ => true;

        client.SendToServer(Bytes("once"), Channel.Reliable);
        Pump(4);

        Assert.Equal(["once"], serverEvents.Texts(TransportRole.Server));
    }

    [Fact]
    public void ALargePayloadIsSplitUpAndPutBackTogether() {
        var payload = new byte[20_000];

        for (var i = 0; i < payload.Length; i++) {
            payload[i] = (byte)(i * 7);
        }

        client.SendToServer(payload, Channel.Reliable);
        Pump(4);

        var received = Assert.Single(serverEvents.Payloads(TransportRole.Server));

        Assert.Equal(payload, received.Payload);
    }

    [Fact]
    public void AFragmentLostOnTheWayIsRetransmittedAndTheMessageStillArrives() {
        var payload = new byte[20_000];
        payload[19_999] = 42;

        // The fourth datagram of the set, which is somewhere in the middle of it.
        var seen = 0L;
        bus.LossPattern = (_, _) => ++seen == 4;

        client.SendToServer(payload, Channel.Reliable);
        Pump(2);

        Assert.Empty(serverEvents.Payloads(TransportRole.Server));

        bus.LossPattern = null;
        Pump(20);

        var received = Assert.Single(serverEvents.Payloads(TransportRole.Server));

        Assert.Equal(20_000, received.Payload.Length);
        Assert.Equal(42, received.Payload[19_999]);
    }

    [Fact]
    public void AConnectionThatStopsAnsweringTimesOut() {
        // The client's process is still there; it has simply stopped running its frame.
        for (var i = 0; i < 700; i++) {
            server.Poll(Step, serverEvents);
        }

        var disconnects = serverEvents.Disconnects(TransportRole.Server);

        Assert.Single(disconnects);
        Assert.Equal(DisconnectReason.Timeout, disconnects[0].Reason);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public void AQuietConnectionIsKeptAliveByTheDatagramsThatSayNothing() {
        // Twelve seconds of neither side sending anything, against a ten-second timeout.
        Pump(750);

        Assert.Empty(serverEvents.Disconnects(TransportRole.Server));
        Assert.Empty(clientEvents.Disconnects(TransportRole.Client));
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public void JunkIsRejectedRatherThanBelieved() {
        var rogue = bus.Bind(new(IPAddress.Loopback, 0));
        var random = new Random(7);
        var datagram = new byte[64];

        for (var i = 0; i < 200; i++) {
            random.NextBytes(datagram);
            rogue.SendTo(datagram, ListenAt);
        }

        Pump(4);

        // Two hundred forged datagrams, and not one of them cost the server a connection. A
        // connect request is answered with a challenge and nothing is allocated until the challenge
        // comes back from the address it was sent to.
        Assert.Equal(1, server.ConnectionCount);
        Assert.Empty(serverEvents.Payloads(TransportRole.Server));
        Assert.True(server.RejectedDatagramCount > 0);
    }

    [Fact]
    public void TheRoundTripIsMeasuredFromTheAcknowledgements() {
        for (var i = 0; i < 10; i++) {
            client.SendToServer(Bytes("ping"), Channel.Reliable);
            Pump(2);
        }

        // Nothing to assert about the number over an instant bus except that measuring it did not
        // break anything — the estimator has its own tests. What matters here is that acknowledgements
        // arrived at all, which is what stops the retransmission.
        Assert.Equal(0, client.RetransmitCount);
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    void DropTheNext(int count) {
        var dropped = 0;
        bus.LossPattern = (_, _) => dropped++ < count;
    }

    void Pump(int rounds) {
        for (var round = 0; round < rounds; round++) {
            client.Poll(Step, clientEvents);
            server.Poll(Step, serverEvents);
        }
    }
}
