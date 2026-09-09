// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Vixen.Net.Tests.Transport;
using Xunit;

namespace Vixen.Net.Transport.Udp.Tests;

/// <summary>
///     <c>LossFor</c>: the same four totals for one link rather than for the whole process, which is
///     the shape <c>Loss</c>'s own remarks said a per-connection question would need.
/// </summary>
/// <remarks>
///     ⚠ <b>Two clients, because with one the two answers agree and the test proves nothing.</b> A
///     transport that ignored its argument and returned <c>Loss</c> would pass every single-connection
///     assertion here — and the defect it would be hiding is the one that matters, since the whole
///     point of asking per link is a server with more than one player.
/// </remarks>
public sealed class UdpPerLinkLossTests : IDisposable {
    const byte Message = 6;

    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);
    static readonly IPEndPoint ListenAt = new(IPAddress.Loopback, 46100);

    readonly DatagramBus bus = new();
    readonly UdpTransport server;
    readonly UdpTransport lossy;
    readonly UdpTransport clean;
    readonly EventRecorder serverEvents = new();
    readonly EventRecorder lossyEvents = new();
    readonly EventRecorder cleanEvents = new();

    readonly ConnectionId lossyLink;
    readonly ConnectionId cleanLink;

    public UdpPerLinkLossTests() {
        server = new(bus, new() { ListenEndPoint = ListenAt, RemoteEndPoint = ListenAt });
        lossy = new(bus, new() { ListenEndPoint = new(IPAddress.Loopback, 0), RemoteEndPoint = ListenAt });
        clean = new(bus, new() { ListenEndPoint = new(IPAddress.Loopback, 0), RemoteEndPoint = ListenAt });

        server.StartServer();
        lossy.StartClient();
        Pump(3);

        lossyLink = Assert.Single(serverEvents.Connects(TransportRole.Server));
        serverEvents.Clear();

        clean.StartClient();
        Pump(3);

        cleanLink = Assert.Single(serverEvents.Connects(TransportRole.Server));
        serverEvents.Clear();

        Assert.NotEqual(lossyLink, cleanLink);
    }

    public void Dispose() {
        clean.Dispose();
        lossy.Dispose();
        server.Dispose();
    }

    /// <summary>
    ///     The link that lost datagrams says so; the link beside it, on the same transport and the
    ///     same bus, says nothing of the sort.
    /// </summary>
    /// <remarks>
    ///     ⚠ The clean link's <c>Missing</c> being zero is the assertion that cannot be satisfied by
    ///     the process totals, which are three. Everything else here would pass against them.
    /// </remarks>
    [Fact]
    public void OneLinksLossIsNotTheOtherLinksAndIsNotTheProcessTotal() {
        var seen = 0;
        bus.LossPattern = (_, datagram) => datagram.Span[0] == Message && ++seen is 5 or 40 or 41;

        Burst(lossy);
        Pump(4);

        bus.LossPattern = null;

        Burst(clean);
        Pump(4);

        var whole = Assert.NotNull(server.Loss);
        var bad = Assert.NotNull(server.LossFor(lossyLink));
        var good = Assert.NotNull(server.LossFor(cleanLink));

        // A hundred each, the newest thirty-three of each still under judgement.
        Assert.Equal(100 - 33, bad.Expected);
        Assert.Equal(3, bad.Missing);

        Assert.Equal(100 - 33, good.Expected);
        Assert.Equal(0, good.Missing);

        // And the process total is the sum, which is neither of them.
        Assert.Equal(bad.Expected + good.Expected, whole.Expected);
        Assert.Equal(3, whole.Missing);
    }

    /// <summary>
    ///     ⚠ <b>A link that has ended has no reading, where the process total keeps what it counted.</b>
    ///     The two rules are opposites on purpose: a cumulative counter that falls reads as a process
    ///     restart, and a per-link reading of a link nobody has is a number about nothing.
    /// </summary>
    [Fact]
    public void AConnectionThatHasGoneHasNoPerLinkReadingAlthoughTheProcessKeepsIts() {
        var seen = 0;
        bus.LossPattern = (_, datagram) => datagram.Span[0] == Message && ++seen is 5 or 40 or 41;

        Burst(lossy);
        Pump(4);

        bus.LossPattern = null;

        Assert.Equal(3, Assert.NotNull(server.LossFor(lossyLink)).Missing);

        lossy.StopClient();
        Pump(3);

        Assert.Null(server.LossFor(lossyLink));
        Assert.Equal(3, Assert.NotNull(server.Loss).Missing);
    }

    /// <summary>A client half asks about its one link with <see cref="ConnectionId.None" />.</summary>
    [Fact]
    public void TheClientHalfIsAskedAboutByNone() {
        Burst(lossy);
        Pump(4);

        // Nothing came the other way, so this is the client's own outbound bookkeeping — and it is a
        // reading rather than a null, which is what says the client half is wired at all.
        var link = Assert.NotNull(lossy.LossFor(ConnectionId.None));

        Assert.Equal(Assert.NotNull(lossy.Loss), link);

        // A connection id a server invented means nothing to a client, and inventing an answer for it
        // would be worse than saying nothing.
        Assert.Null(lossy.LossFor(lossyLink));
    }

    static void Burst(UdpTransport from) {
        for (var i = 0; i < 100; i++) {
            from.SendToServer(Encoding.UTF8.GetBytes("tick"), Channel.Unreliable);
        }
    }

    void Pump(int rounds) {
        for (var round = 0; round < rounds; round++) {
            lossy.Poll(Step, lossyEvents);
            clean.Poll(Step, cleanEvents);
            server.Poll(Step, serverEvents);
        }
    }
}
