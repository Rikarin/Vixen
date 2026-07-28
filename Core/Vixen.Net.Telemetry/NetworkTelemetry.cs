// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Vixen.Net.Diagnostics;

namespace Vixen.Net.Telemetry;

/// <summary>The metrics endpoint a dedicated server is expected to have.</summary>
/// <remarks>
///     <para>
///         <b>It pushes rather than being scraped, and that is the decision this type embodies.</b>
///         A game server is not a web service with a stable address: it is one of a fleet, started
///         and stopped per match, on a port an orchestrator chose, often behind NAT and frequently
///         shorter-lived than a scrape interval. Everything Prometheus's pull model is good at
///         depends on the target being findable and long-lived, and a match server is neither. OTLP
///         to a collector inverts that — the server needs to know one address, and the collector is
///         the thing with a stable name that a Prometheus can then scrape if that is what the
///         organisation runs. This is also the shape a sidecar or a DaemonSet already expects.
///     </para>
///     <para>
///         <b>It is a wrapper around four lines of SDK setup, and that is deliberate.</b> Its value
///         is not the abstraction — it is that the meter name, the resource attributes and the
///         cardinality decisions are made once, correctly, where they can be reviewed, rather than
///         copied into every server head that ever gets written. A game that wants the SDK directly
///         should use the SDK directly; <c>Vixen.Net</c>'s instruments are plain
///         <c>System.Diagnostics.Metrics</c> and need nothing from this package to be read.
///     </para>
///     <para>
///         Nothing here is on the frame's path. The exporter runs on its own timer and reads what
///         <see cref="NetworkMetrics.Sample" /> last published, which is why that method exists —
///         see its remarks for why the game pushes rather than the collector pulling.
///     </para>
/// </remarks>
public sealed class NetworkTelemetry : IDisposable {
    readonly MeterProvider provider;

    NetworkTelemetry(MeterProvider provider, NetworkMetrics metrics) {
        this.provider = provider;
        Metrics = metrics;
    }

    /// <summary>The instruments. Attach the session and the servers to it, and sample it a tick.</summary>
    public NetworkMetrics Metrics { get; }

    /// <summary>Starts exporting.</summary>
    /// <param name="options">Where to, and as whom. Defaults throughout if null.</param>
    /// <returns>The pipeline, which stops when it is disposed.</returns>
    /// <remarks>
    ///     Owns the <see cref="NetworkMetrics" /> it creates, because the meter has to exist before
    ///     the provider is built — an <c>ObservableGauge</c> registered after the provider has
    ///     started is one the provider never collects, and that failure is silent and looks exactly
    ///     like a metric nobody is producing.
    /// </remarks>
    public static NetworkTelemetry Start(TelemetryOptions? options = null) {
        var settings = options ?? new TelemetryOptions();
        var metrics = new NetworkMetrics(settings.ServiceVersion);

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                settings.ServiceName,
                serviceVersion: settings.ServiceVersion,
                serviceInstanceId: settings.ServiceInstanceId ?? Environment.MachineName
            );

        if (settings.Attributes is { Count: > 0 }) {
            resource.AddAttributes(settings.Attributes);
        }

        var builder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(NetworkMetrics.MeterName);

        if (settings.IncludeRuntimeMetrics) {
            builder.AddRuntimeInstrumentation();
        }

        builder.AddOtlpExporter(
            (exporter, reader) => {
                if (settings.Endpoint is not null) {
                    exporter.Endpoint = settings.Endpoint;
                }

                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                    (int)settings.ExportInterval.TotalMilliseconds;
            }
        );

        if (settings.AlsoWriteToConsole) {
            builder.AddConsoleExporter(
                (_, reader) => reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                    (int)settings.ExportInterval.TotalMilliseconds
            );
        }

        return new(builder.Build(), metrics);
    }

    /// <summary>Flushes anything not yet exported.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>Whether it got everything out.</returns>
    /// <remarks>
    ///     Worth calling before a match server exits. The last export interval is the one that
    ///     covers how the match ended, which is the interval somebody is going to go looking for.
    /// </remarks>
    public bool Flush(TimeSpan timeout) => provider.ForceFlush((int)timeout.TotalMilliseconds);

    /// <summary>Stops exporting and closes the meter.</summary>
    public void Dispose() {
        provider.Dispose();
        Metrics.Dispose();
    }
}
