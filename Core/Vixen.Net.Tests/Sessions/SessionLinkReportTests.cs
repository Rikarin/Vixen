// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;
using Xunit;

namespace Vixen.Net.Tests.Sessions;

/// <summary>
///     The peer's inbound counters coming back over the wire, which is the only measurement of a
///     sender's own loss there is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every one of these asserts a number and not a nullness</b>, because the failure this
///         is guarding against is not "no report" — it is a report taken over the wrong link.
///         <c>ITransport.Loss</c> is the whole process's totals, so a session that sent that instead
///         of <c>LossFor(connection)</c> would still deliver a report, still leave the property
///         non-null, and still be wrong for every server with more than one player.
///         <see cref="AServerTellsEachPlayerWhatThatOneLinkMissed" /> is the one that can tell.
///     </para>
///     <para>
///         Ordering, not elapsed time: the cadence is <see cref="SessionOptions.PingInterval" /> and
///         the harness's step is a fixed 16 ms fed to <c>Update</c>, so "enough rounds" is a count of
///         calls and the same count on any machine.
///     </para>
/// </remarks>
public sealed class SessionLinkReportTests {
    /// <summary>Short enough that a handful of harness steps crosses it.</summary>
    static SessionOptions Fast => new() { PingInterval = TimeSpan.FromMilliseconds(32) };

    [Fact]
    public void AClientTellsTheServerWhatItMissedOfWhatWasSentToIt() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(Fast);

        var counting = new CountingTransport(harness.RawTransport());
        counting.Report(ConnectionId.None, new(Sent: 40, Retransmitted: 3, Expected: 100, Missing: 7));

        var client = harness.Add(counting, "client", Fast);
        client.StartClient();

        harness.Pump(24);

        var player = Assert.Single(server.Players);
        var report = Assert.NotNull(player.ObservedOutbound);

        // The client's inbound pair, verbatim, and neither of its outbound ones: what the server sent
        // and resent is the server's own bookkeeping and says nothing about what arrived.
        Assert.Equal(100, report.Expected);
        Assert.Equal(7, report.Missing);
    }

    [Fact]
    public void AServerTellsEachPlayerWhatThatOneLinkMissed() {
        using var harness = new SessionHarness();

        var counting = new CountingTransport(harness.RawTransport());
        var server = harness.Add(counting, "server", Fast);
        server.StartServer();

        var first = harness.StartClient(Fast);
        var second = harness.StartClient(Fast);

        harness.Pump(24);

        Assert.Equal(2, server.Players.Count);

        // Scripted only now that the connections exist, and deliberately different: a session that
        // asked its transport for `Loss` rather than `LossFor` would hand both clients one number,
        // and one number cannot be both of these.
        counting.Report(server.Players[0].Connection, new(0, 0, Expected: 200, Missing: 11));
        counting.Report(server.Players[1].Connection, new(0, 0, Expected: 300, Missing: 29));

        harness.Pump(24);

        var toFirst = Assert.NotNull(first.ObservedOutbound);
        var toSecond = Assert.NotNull(second.ObservedOutbound);

        Assert.Equal(new(200, 11), toFirst);
        Assert.Equal(new(300, 29), toSecond);
    }

    [Fact]
    public void ATransportThatCountsNothingSendsNoReportAtAll() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(Fast);
        var client = harness.StartClient(Fast);

        harness.Pump(24);

        var player = Assert.Single(server.Players);

        // ⚠ The half that keeps this from passing vacuously. `LocalTransport` reports no loss, so the
        // absence below is only evidence if the cadence that would have carried a report actually
        // ran — and a measured round trip is the observable that says a ping went and came back.
        // ⚠ The client's own round trip is on its clock and not on its LocalPlayer record — the
        // client's Pong handler feeds Clock.Synchronize, and the player list a client keeps is a
        // roster rather than a set of measurements. Both are asserted because the two ends run the
        // cadence independently, and a null that only proved the server's half had run would be
        // half an instrument.
        Assert.True(player.RoundTrip.SampleCount > 0, "the server's ping never completed");
        Assert.True(client.Clock.RoundTrip.SampleCount > 0, "the client's ping never completed");

        Assert.Null(player.ObservedOutbound);
        Assert.Null(client.ObservedOutbound);
    }

    [Fact]
    public void AReportClaimingItMissedMoreThanItExpectedIsDiscarded() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(Fast);

        var counting = new CountingTransport(harness.RawTransport());

        // Not a link — a claim. A peer is entitled to be wrong and a fuzzer is entitled to be
        // hostile, and a ratio above one is a number somebody would act on.
        counting.Report(ConnectionId.None, new(0, 0, Expected: 10, Missing: 99));

        var client = harness.Add(counting, "client", Fast);
        client.StartClient();

        harness.Pump(24);

        var player = Assert.Single(server.Players);

        Assert.True(player.RoundTrip.SampleCount > 0, "no ping completed, so the absence proves nothing");
        Assert.Null(player.ObservedOutbound);
    }

    [Fact]
    public void APlayerThatDropsLosesTheReportItGave() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(Fast);

        var counting = new CountingTransport(harness.RawTransport());
        counting.Report(ConnectionId.None, new(0, 0, Expected: 100, Missing: 7));

        var client = harness.Add(counting, "client", Fast);
        client.StartClient();

        harness.Pump(24);

        var player = Assert.Single(server.Players);
        Assert.NotNull(player.ObservedOutbound);

        client.Stop();
        harness.Pump(8);

        // The player is still in the session — a reconnect window is running — and the reading is
        // gone, because it described a connection that has ended.
        Assert.False(player.IsConnected);
        Assert.Null(player.ObservedOutbound);
    }

    [Fact]
    public void TheReportIsAboutTheLinkAndNotAboutTheProcess() {
        using var harness = new SessionHarness();

        var counting = new CountingTransport(harness.RawTransport());
        var server = harness.Add(counting, "server", Fast);
        server.StartServer();

        var client = harness.StartClient(Fast);

        harness.Pump(24);

        var connection = Assert.Single(server.Players).Connection;

        counting.Report(connection, new(0, 0, Expected: 50, Missing: 5));

        // What the whole process missed, which on a server with one busy player and one silent one is
        // not what either of them missed. Nothing may read this.
        counting.Whole = new(0, 0, Expected: 9_999, Missing: 4_321);

        harness.Pump(24);

        Assert.Equal(new(50, 5), Assert.NotNull(client.ObservedOutbound));
    }
}

/// <summary>
///     A transport that counts what a test tells it to, per link, so a session's report can be read
///     against a number nobody guessed.
/// </summary>
/// <remarks>
///     ⚠ <b>A decorator over a real <c>LocalTransport</c> rather than a stub</b>, because the packet
///     has to actually travel: a double that answered <c>LossFor</c> and delivered nothing would let
///     a session that never sent the message pass. Everything but the two loss members is the inner
///     transport's, unchanged.
/// </remarks>
/// <param name="inner">The transport that does the work.</param>
sealed class CountingTransport(LocalTransport inner) : ITransport {
    readonly Dictionary<uint, TransportLoss> links = [];

    /// <summary>What the process is pretending to have counted across every link at once.</summary>
    public TransportLoss? Whole { get; set; }

    /// <inheritdoc />
    public TransportCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public TransportLoss? Loss => Whole;

    /// <inheritdoc />
    public TransportLoss? LossFor(ConnectionId connection) =>
        links.TryGetValue(connection.Value, out var loss) ? loss : null;

    /// <inheritdoc />
    public TransportState ServerState => inner.ServerState;

    /// <inheritdoc />
    public TransportState ClientState => inner.ClientState;

    /// <summary>Says what one link has counted from now on.</summary>
    /// <param name="connection">The link, or <see cref="ConnectionId.None" /> for the client half's.</param>
    /// <param name="loss">What it counted.</param>
    public void Report(ConnectionId connection, TransportLoss loss) => links[connection.Value] = loss;

    /// <inheritdoc />
    public void StartServer() => inner.StartServer();

    /// <inheritdoc />
    public void StopServer() => inner.StopServer();

    /// <inheritdoc />
    public void StartClient() => inner.StartClient();

    /// <inheritdoc />
    public void StopClient() => inner.StopClient();

    /// <inheritdoc />
    public void Disconnect(ConnectionId connection) => inner.Disconnect(connection);

    /// <inheritdoc />
    public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) =>
        inner.SendToClient(connection, payload, channel);

    /// <inheritdoc />
    public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) => inner.SendToServer(payload, channel);

    /// <inheritdoc />
    public void Poll(TimeSpan elapsed, ITransportEvents events) => inner.Poll(elapsed, events);

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();
}
