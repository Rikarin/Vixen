// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Vixen.Net.Diagnostics;
using Xunit;

namespace Vixen.Net.Telemetry.Tests;

/// <summary>The committed Grafana dashboard, against the instruments it claims to draw.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A dashboard is the observability artefact most likely to be quietly wrong, because
///         nothing fails when it is.</b> A renamed metric leaves a panel drawing "No data", which is
///         exactly what a healthy quiet server looks like — so the panel that would have told you
///         about an outage is the panel that stops working first, and it does it silently. This is
///         the gate that makes that a build failure.
///     </para>
///     <para>
///         <b>Both directions, because each catches a different mistake.</b> Every instrument must
///         appear somewhere in the dashboard, or a number nobody drew is a number nobody will look
///         at; and every <c>vixen_net_*</c> series the dashboard queries must be an instrument that
///         exists, or a typo is a permanently empty panel.
///     </para>
///     <para>
///         <b>The names are derived, not listed.</b> Listing them would be a second copy of the
///         mapping, which would agree with this file and with nothing else. The expected Prometheus
///         name is computed from the live meter — its instrument name, its unit and whether it is
///         monotonic — which is the OpenTelemetry Prometheus exporter's own normalisation, and is
///         where a mistake would actually be.
///     </para>
/// </remarks>
public sealed class DashboardCoverageTests {
    /// <summary>The suffixes a Prometheus series carries beyond the name a query names.</summary>
    /// <remarks>
    ///     A histogram becomes three series and a query names <c>_bucket</c>; the dashboard writes
    ///     that, and this is what lets the derived name match it.
    /// </remarks>
    static readonly string[] QuerySuffixes = ["", "_bucket", "_sum", "_count"];

    [Fact]
    public void EveryInstrumentIsDrawnAndEverySeriesDrawnExists() {
        var dashboard = ReadDashboard();
        var published = Published();

        Assert.NotEmpty(published);

        var missing = new List<string>();

        foreach (var name in published) {
            if (!Array.Exists(QuerySuffixes, suffix => dashboard.Contains(name + suffix, StringComparison.Ordinal))) {
                missing.Add(name);
            }
        }

        Assert.True(missing.Count == 0, $"Instruments nothing draws: {string.Join(", ", missing)}.");

        var unknown = new List<string>();

        foreach (Match match in Regex.Matches(dashboard, @"\bvixen_net_[a-z0-9_]+\b")) {
            var series = match.Value;
            var name = Array.Find(published, candidate => Matches(series, candidate));

            if (name is null) {
                unknown.Add(series);
            }
        }

        Assert.True(unknown.Count == 0, $"Series the dashboard draws that no instrument publishes: {string.Join(", ", unknown)}.");
    }

    static bool Matches(string series, string name) =>
        Array.Exists(QuerySuffixes, suffix => string.Equals(series, name + suffix, StringComparison.Ordinal));

    /// <summary>Every instrument the meter registers, under the name Prometheus would give it.</summary>
    static string[] Published() {
        var names = new List<string>();
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, _) => {
            if (instrument.Meter.Name != NetworkMetrics.MeterName) {
                return;
            }

            var name = Normalize(instrument);

            // ⚠ Deduplicated, because this listener hears every meter of that name in the process
            // and xunit runs test classes in parallel — a `NetworkTelemetry.Start` next door builds a
            // `NetworkMetrics` of its own, and its instruments are published to this listener too.
            // Harmless for the assertions, since it is the same type publishing the same names, but
            // it doubles every entry in a failure message.
            if (!names.Contains(name)) {
                names.Add(name);
            }
        };

        listener.Start();

        // ⚠ Constructed *after* the listener starts. `InstrumentPublished` fires as instruments are
        // created, so a meter built first is a meter whose instruments this never sees — and an empty
        // list would satisfy the "everything is drawn" half of the assertion perfectly. That is why
        // the emptiness check above is the first line of the test rather than an afterthought.
        using var metrics = new NetworkMetrics("0.0.1");

        listener.Dispose();

        return [.. names];
    }

    /// <summary>The OpenTelemetry Prometheus name for one instrument.</summary>
    /// <remarks>
    ///     Dots to underscores; the unit as a suffix, spelt out — <c>s</c> is seconds and <c>By</c> is
    ///     bytes; and <c>_total</c> on a monotonic counter and nothing else. A gauge that happens to
    ///     be a running total is still a gauge and gets no suffix, which is the distinction a
    ///     hand-written list gets wrong.
    /// </remarks>
    static string Normalize(Instrument instrument) {
        var name = instrument.Name.Replace('.', '_');

        name += instrument.Unit switch {
            "s" => "_seconds",
            "By" => "_bytes",
            _ => ""
        };

        // The closed generic type is what says monotonic, and `IsObservable` is not: an
        // ObservableGauge and an ObservableCounter are both observable and only one of them is a
        // counter. An up-down counter is not monotonic either, so it is excluded by name rather than
        // caught by the prefix.
        var kind = instrument.GetType().Name;
        var monotonic = (kind.StartsWith("Counter`", StringComparison.Ordinal)
                || kind.StartsWith("ObservableCounter`", StringComparison.Ordinal))
            && !kind.Contains("UpDown", StringComparison.Ordinal);

        return monotonic ? name + "_total" : name;
    }

    static string ReadDashboard() {
        var path = Path.Combine(AppContext.BaseDirectory, "dashboards", "vixen-net.json");

        Assert.True(File.Exists(path), $"The dashboard was not copied to the test output: {path}.");

        return File.ReadAllText(path);
    }
}
