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

    /// <summary>
    ///     Inbound loss is observed, and this is the test that says what "observed" buys: the
    ///     sequences that fell out of the acknowledgement window, and the ones among them that never
    ///     came.
    /// </summary>
    /// <remarks>
    ///     ⚠ The numbers are exact rather than approximate, because an approximate assertion here
    ///     would pass against an instrument that was merely counting something. A hundred datagrams
    ///     go, three of them are eaten, and the newest thirty-three are still under judgement — so
    ///     sixty-seven have been judged and three of those are the ones that were eaten.
    /// </remarks>
    [Fact]
    public void InboundLossIsCountedWhenTheGapFallsOutOfTheAckWindow() {
        DropMessages(5, 40, 41);

        for (var i = 0; i < 100; i++) {
            client.SendToServer(Bytes("tick"), Channel.Unreliable);
        }

        Pump(4);

        var loss = Loss(server);

        Assert.Equal(100 - 33, loss.Expected);
        Assert.Equal(3, loss.Missing);

        // And nothing about the inbound half leaked into the outbound one: an unreliable datagram is
        // never remembered, so there is nothing there to retransmit and nothing to divide by.
        Assert.Equal(0, loss.Sent);
        Assert.Equal(0, loss.Retransmitted);
    }

    /// <summary>
    ///     A burst exactly as wide as the acknowledgement window, which is the arithmetic's one sharp
    ///     edge.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Thirty-one lost and not thirty-two.</b> A jump of exactly thirty-two moves the whole
    ///     of the history out and leaves the sequence that <i>was</i> newest in the top slot — and a
    ///     shift of thirty-two is a shift of zero on this machine, so folding that case in with the
    ///     wider jumps drops that bit and reports an arrival as a loss. The first datagram of this
    ///     test is that bit.
    /// </remarks>
    [Fact]
    public void ABurstExactlyAsWideAsTheWindowLosesEveryDatagramInItAndNoOthers() {
        // The first goes, the next thirty-one are eaten, and the one after that is thirty-two
        // sequences on from the first.
        DropMessages([.. Enumerable.Range(2, 31)]);

        for (var i = 0; i < 100; i++) {
            client.SendToServer(Bytes("tick"), Channel.Unreliable);
        }

        Pump(4);

        var loss = Loss(server);

        Assert.Equal(100 - 33, loss.Expected);
        Assert.Equal(31, loss.Missing);
    }

    /// <summary>
    ///     ⚠ A datagram that is late is not a datagram that is lost, and the window is the whole
    ///     difference. Counted at the gap rather than at the judgement, this would report a loss and
    ///     never take it back.
    /// </summary>
    [Fact]
    public void ADatagramThatArrivesOutOfOrderIsNotCountedAsLost() {
        // Held back until two later ones have gone past it, so it arrives behind them.
        var held = bus.SentCount + 1;
        bus.DelayPattern = number => number == held ? 2 : 0;

        for (var i = 0; i < 63; i++) {
            client.SendToServer(Bytes("tick"), Channel.Unreliable);
        }

        Pump(4);

        var loss = Loss(server);

        Assert.Equal(63 - 33, loss.Expected);
        Assert.Equal(0, loss.Missing);
    }

    /// <summary>
    ///     What the send counter is <i>of</i>: reliable datagrams, once each, however many times they
    ///     then have to go again.
    /// </summary>
    [Fact]
    public void TheSendCounterCountsReliableDatagramsAndCountsAResendAsNeither() {
        for (var i = 0; i < 5; i++) {
            client.SendToServer(Bytes("snapshot"), Channel.Unreliable);
        }

        Pump(2);

        // Unreliable traffic is not in the denominator. Nothing can retransmit it, so counting it
        // would move the share whenever a game changed how many snapshots it sent.
        Assert.Equal(0, Loss(client).Sent);

        DropTheNext(1);
        client.SendToServer(Bytes("important"), Channel.Reliable);
        Pump(2);

        Assert.Equal(1, Loss(client).Sent);
        Assert.Equal(0, Loss(client).Retransmitted);

        // Past the retransmission timeout: the same datagram goes again, and a datagram that goes
        // again is not a datagram that was sent again for the first time.
        Pump(20);

        Assert.Equal(1, Loss(client).Sent);
        Assert.True(Loss(client).Retransmitted > 0);
    }

    /// <summary>
    ///     ⚠ A cumulative counter that falls is one a collector reads as a process restart, and
    ///     summing live connections alone is a counter that falls whenever somebody leaves.
    /// </summary>
    [Fact]
    public void WhatAConnectionCountedOutlivesTheConnection() {
        DropTheNext(1);
        client.SendToServer(Bytes("important"), Channel.Reliable);
        Pump(30);

        var before = Loss(client);

        Assert.True(before.Retransmitted > 0);

        client.StopClient();
        Pump(2);

        Assert.Equal(before, Loss(client));
        Assert.Equal(before.Retransmitted, client.RetransmitCount);
    }

    static TransportLoss Loss(UdpTransport transport) {
        Assert.True(transport.Loss.HasValue, "the transport counted nothing at all");

        return transport.Loss!.Value;
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Eats the message datagrams at these positions, counting from one.</summary>
    /// <remarks>
    ///     ⚠ <b>Message datagrams and not datagrams.</b> Acknowledgements and keep-alives go through
    ///     the bus as well, so a pattern that counted every datagram would eat a different one
    ///     depending on what the two halves had said to each other first. The kind is the first byte
    ///     of the wire format, which the protocol fixes and never reuses.
    /// </remarks>
    void DropMessages(params int[] which) {
        const byte message = 6;
        var seen = 0;

        bus.LossPattern = (_, datagram) => datagram.Span[0] == message && which.Contains(++seen);
    }

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
