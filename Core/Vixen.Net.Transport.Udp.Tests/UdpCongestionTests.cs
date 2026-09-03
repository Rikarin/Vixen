// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Text;
using Vixen.Net.Tests.Transport;
using Xunit;

namespace Vixen.Net.Transport.Udp.Tests;

/// <summary>The window that responds to loss, and what it does not break doing it.</summary>
/// <remarks>
///     <para>
///         <b>Nothing here asserts an elapsed time.</b> Every deadline in this transport is spent
///         against the clock <c>Poll</c> is handed, so "two seconds" below is a hundred and
///         twenty-five sixteen-millisecond steps and takes as long as a hundred and twenty-five
///         function calls take. What is asserted is a count of datagrams or of events — work, not
///         duration.
///     </para>
///     <para>
///         ⚠ <b>The windows here are set absurdly small on purpose.</b> The default is thirty-two,
///         which is a tick's worth of reliable traffic, so on realistic traffic the window never
///         binds and a test written against the defaults would pass whether the window existed or
///         not. Four is small enough that the pacing is the thing being measured.
///     </para>
/// </remarks>
public sealed class UdpCongestionTests : IDisposable {
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);
    static readonly IPEndPoint ListenAt = new(IPAddress.Loopback, 46100);

    readonly DatagramBus bus = new();
    readonly EventRecorder serverEvents = new();
    readonly EventRecorder clientEvents = new();

    UdpTransport server = null!;
    UdpTransport client = null!;

    public void Dispose() {
        client.Dispose();
        server.Dispose();
    }

    /// <summary>A burst wider than the window leaves in window-sized instalments.</summary>
    /// <remarks>
    ///     The deterministic counter is how many message datagrams reached the bus, taken from the
    ///     bus's own tap. Before the window existed every one of the twenty went out in the call that
    ///     wrote it, which is the behaviour this replaces.
    /// </remarks>
    [Fact]
    public void ABurstWiderThanTheWindowLeavesInInstalments() {
        Connect(new() { InitialWindow = 4, MinWindow = 4, MaxWindow = 4 });

        var messages = Count();

        for (var i = 0; i < 20; i++) {
            client.SendToServer(Bytes($"burst-{i.ToString(CultureInfo.InvariantCulture)}"), Channel.Reliable);
        }

        // Not one Poll has run, so nothing has been acknowledged and the window is exactly what it
        // started as. Four went out; sixteen are built, sequenced and waiting.
        Assert.Equal(4, messages());
    }

    /// <summary>And all of it arrives, in order, once the window has opened and closed enough times.</summary>
    /// <remarks>
    ///     The half that makes the pacing worth having. A window that dropped what it could not send
    ///     would satisfy the test above and fail this one.
    /// </remarks>
    [Fact]
    public void EverythingHeldBackStillArrivesAndInOrder() {
        Connect(new() { InitialWindow = 4, MinWindow = 4, MaxWindow = 4 });

        var expected = new List<string>();

        for (var i = 0; i < 20; i++) {
            var text = $"burst-{i.ToString(CultureInfo.InvariantCulture)}";
            expected.Add(text);
            client.SendToServer(Bytes(text), Channel.Reliable);
        }

        Pump(40);

        Assert.Equal(expected, serverEvents.Texts(TransportRole.Server));
        Assert.Equal(0, client.AbandonedCount);
    }

    /// <summary>An unreliable payload is never held back, whatever the window says.</summary>
    /// <remarks>
    ///     A snapshot that waited for a window is a snapshot the next one has already replaced. This
    ///     is the line between "paced" and "delayed", and it is the reason the window counts reliable
    ///     datagrams only.
    /// </remarks>
    [Fact]
    public void AnUnreliablePayloadIgnoresTheWindowEntirely() {
        Connect(new() { InitialWindow = 1, MinWindow = 1, MaxWindow = 1 });

        var messages = Count();

        for (var i = 0; i < 10; i++) {
            client.SendToServer(Bytes("snapshot"), Channel.Unreliable);
        }

        Assert.Equal(10, messages());
    }

    /// <summary>Loss narrows the window; quiet widens it again.</summary>
    [Fact]
    public void TheWindowNarrowsOnLossAndOpensAgainWithout() {
        Connect(new() { InitialWindow = 16, MinWindow = 2, MaxWindow = 64 });

        Assert.Equal(0, client.CongestionShrinkCount);

        // Every message datagram eaten, so every one of them comes due and the pass that finds them
        // is one loss event.
        bus.LossPattern = (_, datagram) => datagram.Span[0] == Message;

        for (var i = 0; i < 8; i++) {
            client.SendToServer(Bytes("into the void"), Channel.Reliable);
        }

        Pump(60);

        var shrunk = client.CongestionShrinkCount;

        Assert.True(shrunk > 0, "loss never reached the controller");

        // ⚠ And it is once per event, not once per datagram — the invariant that says so is that
        // there are strictly fewer halvings than retransmitted datagrams. All eight above were sent
        // together and come due together, so a retransmission pass produces eight datagrams and one
        // event; a controller that halved per datagram produces eight of each and would be at its
        // floor after the first outage of a match.
        //
        // ⚠ An earlier version of this line bounded the count at a constant instead, and a
        // per-datagram sabotage stayed green under it: over the polls below the outage is only two
        // or three passes, so eight-per-pass never reached the constant. A bound that has to be
        // guessed is a bound that can be guessed wrong; this one is a relation between two counters
        // the same run produced.
        Assert.True(
            shrunk < client.RetransmitCount,
            $"the window halved {shrunk.ToString(CultureInfo.InvariantCulture)} times against {client.RetransmitCount.ToString(CultureInfo.InvariantCulture)} retransmitted datagrams, which is per-datagram rather than per-event"
        );

        bus.LossPattern = null;
        Pump(120);

        // The connection is still up and still delivering, which is what recovery means here: the
        // window came off its floor rather than staying there for the rest of the match.
        client.SendToServer(Bytes("after"), Channel.Reliable);
        Pump(60);

        Assert.Contains("after", serverEvents.Texts(TransportRole.Server));
    }

    /// <summary>A retransmission that keeps failing waits longer each time.</summary>
    /// <remarks>
    ///     <para>
    ///         RFC 6298 § 5.5, which <c>RetransmitTimeout</c> cited and did not implement. With a
    ///         200 ms initial timeout and a 1 s ceiling the doubling gives attempts at 200 ms,
    ///         600 ms, 1 400 ms and 2 400 ms — three of them inside the two seconds pumped below.
    ///         At a fixed interval it would be ten.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a wall-clock budget.</b> Those milliseconds are the transport's own
    ///         parameter clock, advanced by exactly 16 ms per <c>Poll</c> whatever the machine is
    ///         doing, so the count below is a deterministic function of the number of calls.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARetransmissionThatKeepsFailingBacksOff() {
        Connect(
            new() {
                InitialRetransmitTimeout = TimeSpan.FromMilliseconds(200),
                MinRetransmitTimeout = TimeSpan.FromMilliseconds(200),
                MaxRetransmitTimeout = TimeSpan.FromSeconds(1)
            }
        );

        bus.LossPattern = (_, datagram) => datagram.Span[0] == Message;

        client.SendToServer(Bytes("never lands"), Channel.Reliable);

        // 125 × 16 ms = two seconds of the transport's clock.
        Pump(125);

        var attempts = client.RetransmitCount;

        Assert.True(attempts > 0, "it was never sent again at all");
        Assert.True(
            attempts <= 4,
            $"one datagram was retransmitted {attempts.ToString(CultureInfo.InvariantCulture)} times in two seconds, which is a fixed interval rather than a backoff"
        );
    }

    /// <summary>Giving up on a reliable datagram is counted.</summary>
    /// <remarks>
    ///     ⚠ <b>A new instrument rather than a regression test.</b> The memory cap has always
    ///     abandoned the oldest unacknowledged datagram — which is the one the peer's ordered
    ///     receiver is blocked on — and nothing counted it, so a connection whose reliability had
    ///     failed was indistinguishable from a healthy one. What is asserted is that the failure is
    ///     now visible, and that it does not happen when it should not.
    /// </remarks>
    [Fact]
    public void GivingUpOnAReliableDatagramIsVisible() {
        Connect(new() { MaxUnacknowledged = 8, InitialWindow = 4, MinWindow = 4, MaxWindow = 4 });

        bus.LossPattern = (_, datagram) => datagram.Span[0] == Message;

        for (var i = 0; i < 200; i++) {
            client.SendToServer(Bytes("doomed"), Channel.Reliable);
        }

        Assert.True(client.AbandonedCount > 0, "the cap threw messages away and said nothing");
    }

    /// <summary>Nothing is abandoned on a link that is merely slow.</summary>
    /// <remarks>The other half: a counter that is always rising says as little as one that never does.</remarks>
    [Fact]
    public void NothingIsGivenUpOnWhenTheLinkIsOnlyNarrow() {
        Connect(new() { MaxUnacknowledged = 1024, InitialWindow = 2, MinWindow = 2, MaxWindow = 2 });

        for (var i = 0; i < 40; i++) {
            client.SendToServer(Bytes("patient"), Channel.Reliable);
        }

        Pump(80);

        Assert.Equal(0, client.AbandonedCount);
        Assert.Equal(40, serverEvents.Texts(TransportRole.Server).Count);
    }

    const byte Message = 6;

    void Connect(UdpTransportOptions clientOptions) {
        server = new(bus, new() { ListenEndPoint = ListenAt, RemoteEndPoint = ListenAt });
        client = new(bus, clientOptions with { ListenEndPoint = new(IPAddress.Loopback, 0), RemoteEndPoint = ListenAt });

        server.StartServer();
        client.StartClient();
        Pump(3);

        Assert.Single(serverEvents.Connects(TransportRole.Server));
        serverEvents.Clear();
        clientEvents.Clear();
    }

    /// <summary>Taps the bus and counts message datagrams, without eating any.</summary>
    /// <remarks>
    ///     Acknowledgements and keep-alives go over the same bus, so counting datagrams would count
    ///     whatever the two halves happened to say to each other first. The kind is the first byte,
    ///     which the protocol fixes and never reuses.
    /// </remarks>
    Func<int> Count() {
        var seen = 0;

        bus.LossPattern = (_, datagram) => {
            if (datagram.Span[0] == Message) {
                seen++;
            }

            return false;
        };

        return () => seen;
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    void Pump(int rounds) {
        for (var round = 0; round < rounds; round++) {
            client.Poll(Step, clientEvents);
            server.Poll(Step, serverEvents);
        }
    }
}
