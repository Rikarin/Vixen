// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using Vixen.Net.Diagnostics;
using Vixen.Net.Tests.Sessions;
using Xunit;

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
