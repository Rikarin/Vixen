// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using Vixen.Ecs;
using Vixen.Net.Diagnostics;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Net.Tests.Sessions;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;
using Xunit;
using LinkCountingTransport = Vixen.Net.Tests.Sessions.CountingTransport;

namespace Vixen.Net.Tests.Diagnostics;

/// <summary>The metrics: that they are published, that they are current, and that they are tagged.</summary>
/// <remarks>
///     Read through a <see cref="MeterListener" />, which is the BCL's own collector and is exactly
///     what the OpenTelemetry SDK is underneath. Testing through it rather than through the SDK keeps
///     these tests in <c>Vixen.Net.Tests</c>, where they belong: the instrumentation is the thing
///     with no dependencies, and a test of it that needed the exporter would be claiming otherwise.
/// </remarks>
public sealed class NetworkMetricsTests {
    [Fact]
    public void EveryInstrumentIsPublishedUnderTheOneMeterName() {
        using var metrics = new NetworkMetrics("1.2.3");
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        Assert.Contains("vixen.net.players", collector.Names);
        Assert.Contains("vixen.net.tick", collector.Names);
        Assert.Contains("vixen.net.rtt.mean", collector.Names);
        Assert.Contains("vixen.net.bandwidth", collector.Names);
        Assert.Contains("vixen.net.snapshot.records", collector.Names);
        Assert.Contains("vixen.net.rpc.calls", collector.Names);
        Assert.Contains("vixen.net.datagrams.sent", collector.Names);
        Assert.Contains("vixen.net.datagrams.retransmitted", collector.Names);
        Assert.Contains("vixen.net.datagrams.expected", collector.Names);
        Assert.Contains("vixen.net.datagrams.lost", collector.Names);
        Assert.Contains("vixen.net.datagrams.peer_expected", collector.Names);
        Assert.Contains("vixen.net.datagrams.peer_lost", collector.Names);
    }

    /// <summary>The four loss totals are the transport's, unchanged and undivided.</summary>
    /// <remarks>
    ///     ⚠ <b>Four counters and no share, which is this file's own rule rather than an omission.</b>
    ///     Three per cent of what went out was resent and one per cent of what was sent to this
    ///     server never arrived; both of those are a division the collector does, and a ratio
    ///     published here could not be re-aggregated across a fleet.
    /// </remarks>
    [Fact]
    public void TheLossTotalsAreWhateverTheTransportCounted() {
        using var metrics = new NetworkMetrics {
            Transport = new CountingTransport(new(Sent: 400, Retransmitted: 12, Expected: 900, Missing: 9))
        };

        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        Assert.Equal(400, collector.Value("vixen.net.datagrams.sent"));
        Assert.Equal(12, collector.Value("vixen.net.datagrams.retransmitted"));
        Assert.Equal(900, collector.Value("vixen.net.datagrams.expected"));
        Assert.Equal(9, collector.Value("vixen.net.datagrams.lost"));
    }

    /// <summary>
    ///     ⚠ A transport that measures nothing leaves the counters at zero, and a dashboard has to
    ///     read them beside the send count to tell that apart from a clean link. It is the one place
    ///     the meter cannot say "not measured" — an instrument registered conditionally would make
    ///     the scrape schema depend on which transport a server happened to be running.
    /// </summary>
    [Fact]
    public void ATransportThatCountsNothingLeavesTheLossTotalsAtZero() {
        // Typed as the interface, because the default member is on the interface: a transport that
        // has nothing to say about loss does not restate the property, and that is the point of it.
        using ITransport transport = new LocalTransport(new());

        Assert.Null(transport.Loss);

        using var metrics = new NetworkMetrics { Transport = transport };
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        Assert.Equal(0, collector.Value("vixen.net.datagrams.sent"));
        Assert.Equal(0, collector.Value("vixen.net.datagrams.lost"));
    }

    /// <summary>What the meter reports is what the last sample found.</summary>
    /// <remarks>
    ///     The whole design in one test: the collection reads a struct the frame filled in, not the
    ///     session. Nothing here would look different if it read the session directly — what would
    ///     differ is the day a collection lands while a player is joining, which is not a thing a
    ///     test can be written for and is why the indirection is there.
    /// </remarks>
    [Fact]
    public void ThePlayersAndTheTickAreWhatTheSessionSaid() {
        using var harness = new SessionHarness();
        var server = harness.StartServer();
        harness.StartClient();
        harness.StartClient();
        harness.Pump();

        using var metrics = new NetworkMetrics();
        metrics.Session = server;

        using var collector = new Collector();

        // Nothing sampled yet, so nothing to report — a gauge that guessed would be worse than one
        // that read zero.
        collector.Collect();
        Assert.Equal(0, collector.Value("vixen.net.players"));

        metrics.Sample();
        collector.Collect();

        Assert.Equal(2, collector.Value("vixen.net.players"));
        Assert.Equal(0, collector.Value("vixen.net.players.awaiting_reconnect"));
        Assert.Equal(server.Tick.Value, collector.Value("vixen.net.tick"));
    }

    [Fact]
    public void APlayerInsideTheirReconnectWindow_IsReportedSeparately() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(new() { ReconnectWindow = TimeSpan.FromSeconds(30) });
        var client = harness.StartClient();
        harness.Pump();

        client.Stop();
        harness.Pump();

        using var metrics = new NetworkMetrics { Session = server };
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        // Still a player, still holding their seat, and not somebody the server can send to. A
        // count that folded the two together would make a fleet losing connections look healthy.
        Assert.Equal(0, collector.Value("vixen.net.players"));
        Assert.Equal(1, collector.Value("vixen.net.players.awaiting_reconnect"));
    }

    [Fact]
    public void BandwidthComesFromTheLedgerInBytes() {
        var ledger = new BandwidthLedger();
        ledger.RecordCall(new(1), "Thing.Method()", bits: 800);

        using var metrics = new NetworkMetrics { Ledger = ledger };
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        // Counted in bits and reported in bytes, the same conversion the ledger's own report makes.
        Assert.Equal(100, collector.Value("vixen.net.bandwidth"));
    }

    /// <summary>Records carry the tag that says which half of the delta story they are.</summary>
    /// <remarks>
    ///     One instrument with a tag rather than two instruments, because the number anybody looks
    ///     at is the ratio — a fleet whose whole-record share is climbing is a fleet losing packets,
    ///     and two separate series make that a division somebody has to remember to do.
    /// </remarks>
    [Fact]
    public void RecordsAreTaggedByWhetherTheyWentAsADifference() {
        using var metrics = new NetworkMetrics();
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        Assert.Contains("kind=delta", collector.Tags("vixen.net.snapshot.records"));
        Assert.Contains("kind=whole", collector.Tags("vixen.net.snapshot.records"));
    }

    [Fact]
    public void CallsAreTaggedByWhatHappenedToThem() {
        using var metrics = new NetworkMetrics();
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        var outcomes = collector.Tags("vixen.net.rpc.calls");

        Assert.Contains("outcome=accepted", outcomes);
        Assert.Contains("outcome=rate_limited", outcomes);
        Assert.Contains("outcome=bad_arguments", outcomes);
        Assert.Contains("outcome=unknown_method", outcomes);
    }

    [Fact]
    public void ATickIsRecordedInSecondsIntoAHistogram() {
        using var metrics = new NetworkMetrics();
        using var collector = new Collector();

        metrics.RecordTick(TimeSpan.FromMilliseconds(2.5));
        metrics.RecordSnapshot(bytes: 480);

        Assert.Equal(0.0025, collector.Value("vixen.net.tick.duration"), 6);
        Assert.Equal(480, collector.Value("vixen.net.snapshot.size"));
    }

    /// <summary>The client's three, which are the numbers no server-side one can answer.</summary>
    /// <remarks>
    ///     ⚠ <b>The rejection is produced rather than asserted about.</b> A test that set the fields
    ///     by hand would be checking that a struct copies, which is not the claim — the claim is that
    ///     a snapshot a client could not decode reaches a collector as a number somebody can alert
    ///     on. So this feeds a real <c>ReplicationClient</c> a type id nothing registered, which is
    ///     the shape of the failure that matters: two peers disagreeing about a wire format.
    /// </remarks>
    [Fact]
    public void TheClientPublishesWhatWentWrongForThePlayer() {
        using var world = new World("metrics-client");
        var client = new ReplicationClient(new ReplicationRegistry());
        var writer = new BitWriter(new byte[128]);

        writer.WriteUInt32(7);
        writer.WriteBool(false);
        writer.WriteBool(true);
        writer.WriteVariable(1);
        writer.WriteVariable(0xDEAD);
        writer.WriteUInt32(0);

        Assert.True(writer.TryFinish(out var snapshot));
        Assert.False(client.TryApply(world, snapshot));

        using var metrics = new NetworkMetrics { Client = client };
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        Assert.Contains("vixen.net.client.entities", collector.Names);
        Assert.Contains("vixen.net.client.snapshots.stale", collector.Names);
        Assert.Equal(1, collector.Value("vixen.net.client.snapshots.rejected"));
        Assert.Equal(0, collector.Value("vixen.net.client.entities"));
    }

    /// <summary>What the peers say they missed of what this end sent, as two counters.</summary>
    /// <remarks>
    ///     ⚠ <b>Over a real wire, because the report is a message and not a property.</b> A test that
    ///     assigned <c>NetworkPlayer.ObservedOutbound</c> would prove the meter reads a field; what
    ///     is worth proving is that the number a peer's transport counted reaches the meter, and the
    ///     assertion above it — that the property is non-null before the meter is asked — is what
    ///     keeps a passing zero from looking like agreement.
    /// </remarks>
    [Fact]
    public void WhatThePeersSayTheyMissedIsPublishedAsTwoCounters() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(Fast);

        var counting = new LinkCountingTransport(harness.RawTransport());
        counting.Report(ConnectionId.None, new(Sent: 40, Retransmitted: 3, Expected: 100, Missing: 7));

        var client = harness.Add(counting, "client", Fast);
        client.StartClient();
        harness.Pump(24);

        var player = Assert.Single(server.Players);
        Assert.NotNull(player.ObservedOutbound);

        using var metrics = new NetworkMetrics { Session = server };
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        // The client's inbound pair and neither of its outbound ones — 40 and 3 are the client's own
        // bookkeeping about what it sent, and say nothing about what this server's packets did.
        Assert.Equal(100, collector.Value("vixen.net.datagrams.peer_expected"));
        Assert.Equal(7, collector.Value("vixen.net.datagrams.peer_lost"));
    }

    /// <summary>A client publishes its own report, which is on no player record anywhere.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a meter walking <c>Session.Players</c> cannot see.</b> A server writes what
    ///     a peer said onto that peer's <c>NetworkPlayer</c>; a client writes it onto the session,
    ///     because the peer is the server and a client's player list is a roster rather than a set of
    ///     links. Read only through the players, this counter would be flat at zero on every client
    ///     in the fleet and nothing would say so.
    /// </remarks>
    [Fact]
    public void AClientPublishesWhatTheServerSaidItMissedOfWhatTheClientSent() {
        using var harness = new SessionHarness();

        var counting = new LinkCountingTransport(harness.RawTransport());
        var server = harness.Add(counting, "server", Fast);
        server.StartServer();

        var client = harness.StartClient(Fast);
        harness.Pump(24);

        counting.Report(Assert.Single(server.Players).Connection, new(0, 0, Expected: 300, Missing: 29));
        harness.Pump(24);

        Assert.NotNull(client.ObservedOutbound);

        using var metrics = new NetworkMetrics { Session = client };
        using var collector = new Collector();

        metrics.Sample();
        collector.Collect();

        Assert.Equal(300, collector.Value("vixen.net.datagrams.peer_expected"));
        Assert.Equal(29, collector.Value("vixen.net.datagrams.peer_lost"));
    }

    /// <summary>
    ///     ⚠ A player dropping inside their reconnect window must not make the counters fall, and the
    ///     naive implementation does exactly that.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>NetworkSession.LoseConnection</c> clears <c>ObservedOutbound</c> the moment the
    ///         connection ends — the totals described that link — while leaving the player in the
    ///         session for the whole reconnect window. So a meter that summed the live players would
    ///         publish a hundred, then zero, on a server that has lost nothing at all: an
    ///         <c>ObservableCounter</c> going down is what a collector reads as a process restart,
    ///         and the rate it computes across that step is a negative one it throws away.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The clear is asserted before the meter is, and that is the instrument check.</b>
    ///         Were the property still set, this test would pass against the very implementation it
    ///         exists to refuse.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APlayerDroppingDoesNotMakeTheOutboundCountersFall() {
        var options = new SessionOptions {
            PingInterval = TimeSpan.FromMilliseconds(32), ReconnectWindow = TimeSpan.FromSeconds(30)
        };

        using var harness = new SessionHarness();
        var server = harness.StartServer(options);

        var counting = new LinkCountingTransport(harness.RawTransport());
        counting.Report(ConnectionId.None, new(0, 0, Expected: 100, Missing: 7));

        var client = harness.Add(counting, "client", options);
        client.StartClient();
        harness.Pump(24);

        using var metrics = new NetworkMetrics { Session = server };

        using (var before = new Collector()) {
            metrics.Sample();
            before.Collect();

            Assert.Equal(100, before.Value("vixen.net.datagrams.peer_expected"));
            Assert.Equal(7, before.Value("vixen.net.datagrams.peer_lost"));
        }

        client.Stop();
        harness.Pump(8);

        var player = Assert.Single(server.Players);
        Assert.False(player.IsConnected);
        Assert.Null(player.ObservedOutbound);

        metrics.Sample();

        using var after = new Collector();
        after.Collect();

        Assert.Equal(100, after.Value("vixen.net.datagrams.peer_expected"));
        Assert.Equal(7, after.Value("vixen.net.datagrams.peer_lost"));
    }

    /// <summary>
    ///     And a player removed outright — no reconnect window at all — is the same requirement one
    ///     step further, where the record the naive sum walked is not there to be walked.
    /// </summary>
    [Fact]
    public void APlayerLeavingForGoodDoesNotMakeTheOutboundCountersFall() {
        var options = new SessionOptions {
            PingInterval = TimeSpan.FromMilliseconds(32), ReconnectWindow = TimeSpan.Zero
        };

        using var harness = new SessionHarness();
        var server = harness.StartServer(options);

        var counting = new LinkCountingTransport(harness.RawTransport());
        counting.Report(ConnectionId.None, new(0, 0, Expected: 100, Missing: 7));

        var client = harness.Add(counting, "client", options);
        client.StartClient();
        harness.Pump(24);

        using var metrics = new NetworkMetrics { Session = server };
        metrics.Sample();

        Assert.NotEmpty(server.Players);

        client.Stop();
        harness.Pump(8);

        Assert.Empty(server.Players);

        metrics.Sample();

        using var after = new Collector();
        after.Collect();

        Assert.Equal(100, after.Value("vixen.net.datagrams.peer_expected"));
        Assert.Equal(7, after.Value("vixen.net.datagrams.peer_lost"));
    }

    /// <summary>
    ///     ⚠ A report that arrives after a newer one must not walk a live link's totals backwards,
    ///     because the message travels unreliable and is therefore free to be reordered.
    /// </summary>
    /// <remarks>
    ///     The link is still up throughout — no drop, no clear — so the only thing that could lower
    ///     the published counter is the meter believing the last report it saw rather than the
    ///     largest. A link's totals are cumulative for its life, so the largest is the true one.
    /// </remarks>
    [Fact]
    public void AReorderedReportDoesNotWalkALiveLinkBackwards() {
        using var harness = new SessionHarness();
        var server = harness.StartServer(Fast);

        var counting = new LinkCountingTransport(harness.RawTransport());
        counting.Report(ConnectionId.None, new(0, 0, Expected: 400, Missing: 40));

        var client = harness.Add(counting, "client", Fast);
        client.StartClient();
        harness.Pump(24);

        using var metrics = new NetworkMetrics { Session = server };
        metrics.Sample();

        // The same link, saying something it already said a while ago.
        counting.Report(ConnectionId.None, new(0, 0, Expected: 100, Missing: 7));
        harness.Pump(24);

        Assert.Equal(new(100, 7), Assert.NotNull(Assert.Single(server.Players).ObservedOutbound));

        metrics.Sample();

        using var after = new Collector();
        after.Collect();

        Assert.Equal(400, after.Value("vixen.net.datagrams.peer_expected"));
        Assert.Equal(40, after.Value("vixen.net.datagrams.peer_lost"));
    }

    /// <summary>Short enough that a handful of harness steps crosses the ping cadence.</summary>
    static SessionOptions Fast => new() { PingInterval = TimeSpan.FromMilliseconds(32) };

    /// <summary>A transport that does nothing but count, which is all the meter asks of one.</summary>
    sealed class CountingTransport(TransportLoss counted) : ITransport {
        public TransportCapabilities Capabilities { get; } = new(1200, IsInProcess: false, IsLossy: true);

        public TransportLoss? Loss => counted;

        public TransportState ServerState => TransportState.Stopped;

        public TransportState ClientState => TransportState.Stopped;

        public void StartServer() { }

        public void StopServer() { }

        public void StartClient() { }

        public void StopClient() { }

        public void Disconnect(ConnectionId connection) { }

        public void SendToClient(ConnectionId connection, ReadOnlySpan<byte> payload, Channel channel) { }

        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) { }

        public void Poll(TimeSpan elapsed, ITransportEvents events) { }

        public void Dispose() { }
    }

    /// <summary>Reads Vixen.Net's meter the way a collector does.</summary>
    sealed class Collector : IDisposable {
        readonly MeterListener listener = new();
        readonly Dictionary<string, double> values = [];
        readonly Dictionary<string, List<string>> tags = [];

        public Collector() {
            listener.InstrumentPublished = (instrument, self) => {
                if (string.Equals(instrument.Meter.Name, NetworkMetrics.MeterName, StringComparison.Ordinal)) {
                    self.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>((instrument, value, labels, _) => Take(instrument, value, labels));
            listener.SetMeasurementEventCallback<int>((instrument, value, labels, _) => Take(instrument, value, labels));
            listener.SetMeasurementEventCallback<double>((instrument, value, labels, _) => Take(instrument, value, labels));
            listener.Start();
        }

        public IReadOnlyCollection<string> Names => values.Keys;

        public void Collect() => listener.RecordObservableInstruments();

        public double Value(string name) => values.GetValueOrDefault(name);

        public IReadOnlyList<string> Tags(string name) => tags.GetValueOrDefault(name) ?? [];

        public void Dispose() => listener.Dispose();

        void Take(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> labels) {
            // Summed rather than replaced, because a tagged instrument reports once per tag set and
            // the total across them is the number the assertions above want.
            values[instrument.Name] = values.GetValueOrDefault(instrument.Name) + value;

            if (labels.IsEmpty) {
                return;
            }

            if (!tags.TryGetValue(instrument.Name, out var seen)) {
                seen = [];
                tags[instrument.Name] = seen;
            }

            foreach (var label in labels) {
                seen.Add($"{label.Key}={label.Value}");
            }
        }
    }
}
