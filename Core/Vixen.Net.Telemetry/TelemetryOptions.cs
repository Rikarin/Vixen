// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Telemetry;

/// <summary>Where a server's metrics go, and what they say they came from.</summary>
/// <remarks>
///     <para>
///         <b>Every field has a sensible default and most deployments set none of them.</b> The
///         OpenTelemetry SDK reads <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, <c>OTEL_SERVICE_NAME</c> and
///         the rest of the standard environment on its own, and a container orchestrator already
///         sets those — so a server that calls <see cref="NetworkTelemetry.Start" /> with a bare
///         instance is configured by its deployment rather than by its build, which is the way round
///         that lets one image run in three environments.
///     </para>
///     <para>
///         What is here is for the cases the environment cannot express: a name a game gives itself,
///         an instance id that is the match rather than the pod, and the console fallback.
///     </para>
/// </remarks>
public sealed record TelemetryOptions {
    /// <summary>What this process calls itself. Overridden by <c>OTEL_SERVICE_NAME</c>.</summary>
    public string ServiceName { get; init; } = "vixen-server";

    /// <summary>Which build it is, so a rollout can tell two of them apart.</summary>
    public string? ServiceVersion { get; init; }

    /// <summary>
    ///     Which one of the fleet this is. A hostname is the usual answer; a match id is the more
    ///     useful one, when a process is one match.
    /// </summary>
    /// <remarks>
    ///     Defaulted to the machine name rather than left empty. An instance id that is absent makes
    ///     every server in a fleet the same time series, and the first question anybody asks a fleet
    ///     is which of them is the slow one.
    /// </remarks>
    public string? ServiceInstanceId { get; init; }

    /// <summary>
    ///     Where the collector is. Null takes <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, and then the
    ///     SDK's own default of <c>http://localhost:4317</c> — which is the sidecar.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>How often metrics are pushed.</summary>
    /// <remarks>
    ///     Fifteen seconds, which is the OpenTelemetry default and roughly what a dashboard can
    ///     use. Shorter is mostly a way to pay for storage: the numbers here are counters and
    ///     smoothed gauges, and neither says anything new twice a second.
    /// </remarks>
    public TimeSpan ExportInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Whether to publish CPU, GC, thread-pool and exception counts alongside.</summary>
    /// <remarks>
    ///     On by default. A server's own numbers say what happened and almost never why; the answer
    ///     is usually a collection, a starved thread pool, or an exception being thrown in a loop,
    ///     and none of those is visible from the networking metrics alone.
    /// </remarks>
    public bool IncludeRuntimeMetrics { get; init; } = true;

    /// <summary>Whether to also print every export to standard output.</summary>
    /// <remarks>
    ///     Off by default and worth having. The first question about a new deployment is whether it
    ///     is exporting anything at all, and a collector that is silently dropping the data looks
    ///     exactly like a server that is silently not sending it.
    /// </remarks>
    public bool AlsoWriteToConsole { get; init; }

    /// <summary>Extra resource attributes: region, cluster, game mode, whatever the fleet is cut by.</summary>
    public IReadOnlyDictionary<string, object>? Attributes { get; init; }
}
