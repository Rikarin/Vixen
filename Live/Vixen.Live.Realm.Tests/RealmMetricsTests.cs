// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using Vixen.Net.Diagnostics;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Live.Realms.Tests;

/// <summary>That a running shard actually publishes what its meter promises.</summary>
/// <remarks>
///     <para>
///         <b>The state this closes.</b> <see cref="NetworkMetrics" /> registered fourteen
///         instruments, had a <c>Sample</c> method whose remarks say "call it from the server's
///         tick", and was constructed by nothing outside its own tests — <c>Session</c>,
///         <c>Transport</c>, <c>Ledger</c> and <c>Rpc</c> had no production caller anywhere in the
///         tree. Every gauge on every shard read zero, and a zero from an instrument nobody feeds is
///         indistinguishable from a zero measured off a healthy link. <c>RealmHeartbeat</c>'s own
///         remarks already asserted the opposite — "every one of these numbers is already an
///         instrument in <c>Vixen.Net.Telemetry</c>" — which is what made the gap invisible to a
///         reader.
///     </para>
///     <para>
///         ⚠ <b>Asserted through a <see cref="MeterListener" /> and not off the host's fields</b>,
///         which is the only version of this test that can fail for the right reason. A test reading
///         <c>Host.Metrics.Session</c> proves an assignment; what a collector sees is the
///         <em>reading</em>, and the reading only exists if something called <c>Sample</c> on the
///         realm's own thread once a tick. <c>MeterListener</c> is what the OpenTelemetry SDK is
///         underneath, so this is the same collection a shard's exporter performs.
///     </para>
///     <para>
///         ⚠ <b>Every collecting test supplies its own meter, tagged with a version nothing else
///         uses.</b> A <c>MeterListener</c> subscribes by meter <i>name</i>, every realm in this
///         assembly now publishes under <c>Vixen.Net</c>, and xUnit runs test classes in parallel —
///         so a collector that took every measurement it was offered would be reading some other
///         test's shard about a third of the time. That is the flake this file would otherwise be.
///     </para>
///     <para>
///         ⚠ <b>The link numbers are asserted over a wire with latency on it.</b> A realm on
///         <c>Perfect</c> reports a round trip of zero, which is exactly what an unfed gauge reports
///         — so the one profile that cannot tell the fix from the bug is the one every other test in
///         this project uses.
///     </para>
/// </remarks>
public class RealmMetricsTests {
    /// <summary>The two the realm owns, attached to the meter the realm publishes into.</summary>
    /// <remarks>
    ///     <para>
    ///         The transport comes off the session rather than being a second thing to wire, which is
    ///         <c>NetworkMetrics.Transport</c>'s own argument for taking a transport instead of four
    ///         delegates: <c>ITransport.Loss</c> is one read that already adds up both halves and
    ///         every channel. A realm on UDP therefore gets the loss counters with no further
    ///         wiring, and one on <c>Transport.Local</c> leaves them at zero because a transport that
    ///         cannot count datagrams has not told anybody it lost none.
    ///     </para>
    ///     <para>
    ///         The one test here that asserts against the meter the <em>realm</em> made, rather than
    ///         one handed in — which it can do safely because it reads the host and not a listener.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheRealmPointsTheMeterAtItsOwnSessionAndTheTransportUnderIt() {
        using var realm = new RealmFixture();

        Assert.Same(realm.Host.Session, realm.Host.Metrics.Session);
        Assert.Same(realm.Host.Session.Transport, realm.Host.Metrics.Transport);
    }

    /// <summary>Players, tick, round trip and jitter, off a shard somebody is playing on.</summary>
    [Fact]
    public void ARunningShardPublishesItsPopulationAndItsLink() {
        const string version = nameof(ARunningShardPublishesItsPopulationAndItsLink);

        using var metrics = new NetworkMetrics(version);
        using var realm = new RealmFixture(wire: NetworkSimulationProfile.Broadband, metrics: metrics);
        using var collector = new Collector(version);

        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Connect(realm.Ticket());

        // Long enough for the handshake to survive a delayed wire and for the ping interval — one
        // second, and a step is sixteen milliseconds — to have come round several times. Asserted on
        // the outcome and never on the count, for the reason AdmissionUnderLossTests gives.
        realm.Pump(400);

        collector.Collect();

        Assert.Equal(2, collector.Value("vixen.net.players"));
        Assert.Equal(realm.Host.Session.Tick.Value, collector.Value("vixen.net.tick"));

        // ⚠ The three that were the point of the exercise. Broadband is 35 ms each way and both ends
        // of the connection are wrapped, so a round trip is upwards of seventy milliseconds — a
        // shard reporting zero here is one whose meter nothing sampled.
        Assert.True(
            collector.Value("vixen.net.rtt.mean") > 0,
            "the mean round trip is zero, so nothing sampled the session into the meter"
        );

        Assert.True(collector.Value("vixen.net.rtt.worst") > 0, "the worst round trip is zero");
        Assert.True(collector.Value("vixen.net.jitter.worst") > 0, "the worst jitter is zero");

        // And the histogram beside them, which is the number RealmHeartbeat samples in parallel. The
        // two have to come from the same place, or a shard's health and its traces can disagree
        // about what a tick cost.
        Assert.True(collector.Value("vixen.net.tick.duration") > 0, "no tick was ever recorded");
    }

    /// <summary>The reading is current rather than cumulative: it follows the session down.</summary>
    /// <remarks>
    ///     ⚠ <b>A gauge that kept its last value would be the subtler half of the same bug.</b> One
    ///     sample taken at the right moment makes a dead shard look populated for ever, and a fleet
    ///     view built on that never notices a shard that stopped taking anybody.
    /// </remarks>
    [Fact]
    public void ThePopulationFallsWhenSomebodyLeaves() {
        const string version = nameof(ThePopulationFallsWhenSomebodyLeaves);

        using var metrics = new NetworkMetrics(version);
        using var realm = new RealmFixture(metrics: metrics);
        using var collector = new Collector(version);

        realm.MapIsUp();

        var client = realm.Connect(realm.Ticket());

        realm.Pump(16);
        collector.Collect();

        Assert.Equal(1, collector.Value("vixen.net.players"));

        client.Stop();
        realm.Pump(16);
        collector.Collect();

        Assert.Equal(0, collector.Value("vixen.net.players"));
    }

    /// <summary>A shard with an exporter publishes into the exporter's meter, not a second one.</summary>
    /// <remarks>
    ///     ⚠ <b>Two meters under one name is the failure this option exists to prevent.</b>
    ///     <c>NetworkTelemetry.Start</c> has to build its <see cref="NetworkMetrics" /> before the
    ///     provider, because an observable instrument registered afterwards is one the provider never
    ///     collects — so a realm that made one of its own would leave the exporter reading a meter
    ///     nobody samples while the samples went to a meter with no exporter on it. That failure is
    ///     silent and looks exactly like a shard that is not running.
    /// </remarks>
    [Fact]
    public void AMeterTheHostAlreadyHadIsTheOneTheRealmSamples() {
        const string version = nameof(AMeterTheHostAlreadyHadIsTheOneTheRealmSamples);

        using var mine = new NetworkMetrics(version);
        using var realm = new RealmFixture(metrics: mine);

        Assert.Same(mine, realm.Host.Metrics);
        Assert.Same(realm.Host.Session, mine.Session);

        using var collector = new Collector(version);

        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Pump(16);

        collector.Collect();

        Assert.Equal(1, collector.Value("vixen.net.players"));
    }

    /// <summary>
    ///     ⚠ Replication, RPC and the bandwidth ledger stay the game's, because the realm does not
    ///     own the game.
    /// </summary>
    /// <remarks>
    ///     <c>RealmHost</c>'s own remarks draw that line — "replication, RPC, interest and the world
    ///     belong to the realm's own session and its systems" — and a realm that attached them itself
    ///     would be reaching past it. What the realm guarantees is that all three are
    ///     <em>sampled</em> once a tick once a game has attached them, which is the half a game
    ///     cannot do for itself without a second call site on the frame path.
    /// </remarks>
    [Fact]
    public void AGameAttachesItsLedgerAndTheRealmSamplesIt() {
        const string version = nameof(AGameAttachesItsLedgerAndTheRealmSamplesIt);

        using var metrics = new NetworkMetrics(version);
        using var realm = new RealmFixture(metrics: metrics);
        using var collector = new Collector(version);

        var ledger = new BandwidthLedger();

        ledger.Record(new PlayerId(1), new NetworkId(1), "NetworkTransform", bits: 4_096, asDelta: true);
        realm.Host.Metrics.Ledger = ledger;

        realm.MapIsUp();
        realm.Pump(2);

        collector.Collect();

        // Bytes, because that is the unit the instrument is declared in and the ledger counts bits.
        Assert.Equal(512, collector.Value("vixen.net.bandwidth"));
    }

    /// <summary>Reads one <see cref="NetworkMetrics" />'s instruments the way a collector does.</summary>
    /// <remarks>
    ///     The same shape as <c>NetworkMetricsTests.Collector</c>, and deliberately a copy rather
    ///     than a shared helper: the two projects assert different things about the same meter, and a
    ///     collector shared between them would be a dependency from this project on
    ///     <c>Vixen.Net</c>'s test assembly. What is <em>not</em> a copy is the version filter — see
    ///     this class's remarks.
    /// </remarks>
    sealed class Collector : IDisposable {
        readonly MeterListener listener = new();
        readonly Dictionary<string, double> values = [];

        /// <summary>Subscribes to one meter.</summary>
        /// <param name="version">
        ///     The version the <see cref="NetworkMetrics" /> under test was constructed with, which
        ///     becomes its meter's. <c>NetworkMetrics</c> does not expose its <c>Meter</c> — nothing
        ///     outside it has any business recording into one — so the version is the handle a
        ///     listener has onto the instance, and the name alone would subscribe to every realm in
        ///     the assembly including ones another test class is running right now.
        /// </param>
        public Collector(string version) {
            listener.InstrumentPublished = (instrument, self) => {
                if (string.Equals(instrument.Meter.Name, NetworkMetrics.MeterName, StringComparison.Ordinal)
                    && string.Equals(instrument.Meter.Version, version, StringComparison.Ordinal)) {
                    self.EnableMeasurementEvents(instrument);
                }
            };

            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => values[instrument.Name] = value);
            listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => values[instrument.Name] = value);
            listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => values[instrument.Name] = value);
            listener.Start();
        }

        public void Collect() => listener.RecordObservableInstruments();

        public double Value(string name) => values.GetValueOrDefault(name);

        public void Dispose() => listener.Dispose();
    }
}
