// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Net.Diagnostics;
using Xunit;

namespace Vixen.Net.Telemetry.Tests;

/// <summary>The exporter: that it starts, that it does not take the server down with it.</summary>
/// <remarks>
///     <para>
///         <b>These tests do not assert that metrics arrive somewhere</b>, and it is worth saying why
///         rather than leaving it looking like an oversight. Doing that means a collector, which means
///         a socket and a process in the test run, and what it would prove is that the OpenTelemetry
///         SDK exports metrics — which is the SDK's own claim and is tested in the SDK's own
///         repository. What is ours to get wrong is the wiring: the meter name, the resource, and the
///         lifetime.
///     </para>
///     <para>
///         <b>The failure mode this does test is the one that matters on a dedicated server.</b> A
///         collector that is not there is the normal state of affairs at three in the morning, and the
///         only acceptable behaviour is that the game keeps running. Every test here points the
///         exporter at a port with nothing behind it.
///     </para>
/// </remarks>
public sealed class NetworkTelemetryTests {
    /// <summary>A port nothing is listening on, which is the interesting deployment.</summary>
    static readonly Uri Nowhere = new("http://127.0.0.1:1");

    static TelemetryOptions Options => new() {
        ServiceName = "vixen-test",
        ServiceVersion = "0.0.1",
        ServiceInstanceId = "match-1",
        Endpoint = Nowhere,
        ExportInterval = TimeSpan.FromMilliseconds(50),

        // Off, because the runtime instrumentation registers process-wide callbacks and two test
        // classes starting it at once is a fight over nothing this test is about.
        IncludeRuntimeMetrics = false,

        // ⚠ Off for the same class of reason, and it is load-bearing for the two tests below rather
        // than tidiness. A tracer provider subscribes to an `ActivitySource` *by name*, and that
        // subscription is process-wide — so a test asserting that traces are off cannot be right
        // while another test in the same process has them on. xunit runs the tests of one class in
        // sequence, which makes that assertion sound here and nowhere else.
        IncludeTraces = false
    };

    /// <summary>Traces on means the engine's source has a listener, which is the whole wiring.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Asserted through a fresh <c>ActivitySource</c> of the same name rather than
    ///         through the engine's own object</b>, because that is what the SDK actually matches on:
    ///         <c>AddSource("Vixen.Net")</c> subscribes to the name, so any source called that gets
    ///         the listener. Which also means this test cannot pass by accident of the engine having
    ///         been touched — nothing in this assembly can start a session.
    ///     </para>
    ///     <para>
    ///         The negative half is the one that makes it a test rather than a tautology: with
    ///         <c>IncludeTraces</c> off, the same source has no listener, so the assertion is capable
    ///         of both answers.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TracesAreWiredToTheEnginesSourceAndCanBeTurnedOff() {
        using (var source = new ActivitySource(NetworkActivity.SourceName)) {
            Assert.False(source.HasListeners(), "Something had already subscribed before this test ran.");

            using (NetworkTelemetry.Start(Options with { IncludeTraces = true })) {
                Assert.True(source.HasListeners());
            }

            // Disposed with the provider, so a server that stops telemetry stops paying for spans.
            Assert.False(source.HasListeners());

            using (NetworkTelemetry.Start(Options)) {
                Assert.False(source.HasListeners());
            }
        }
    }

    /// <summary>A collector that is not there does not take the spans down with it either.</summary>
    [Fact]
    public void ACollectorThatIsNotThere_DoesNotTakeTheSpansWithIt() {
        using var telemetry = NetworkTelemetry.Start(Options with { IncludeTraces = true });
        using var source = new ActivitySource(NetworkActivity.SourceName);

        for (var handshake = 0; handshake < 20; handshake++) {
            using var activity = source.StartActivity(NetworkActivity.HandshakeName, ActivityKind.Server);
            activity?.SetTag("vixen.net.connection", handshake);
        }

        // Same contract as the metrics half: whether it got out is the network's business, that it
        // answers inside the timeout rather than hanging the shutdown is ours.
        telemetry.Flush(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void ACollectorThatIsNotThere_DoesNotTakeTheServerWithIt() {
        using var telemetry = NetworkTelemetry.Start(Options);

        // A few frames of a server that has nothing to say and a collector that is not listening.
        for (var tick = 0; tick < 20; tick++) {
            telemetry.Metrics.Sample();
            telemetry.Metrics.RecordTick(TimeSpan.FromMilliseconds(2));
            telemetry.Metrics.RecordSnapshot(bytes: 256);
        }

        // Whether it got anything out is the network's business and is allowed to be false. That it
        // answers inside the timeout rather than hanging the shutdown is ours.
        telemetry.Flush(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void ItOwnsTheInstrumentsItStarted() {
        using var telemetry = NetworkTelemetry.Start(Options);

        Assert.NotNull(telemetry.Metrics);

        // The provider is built around this meter, so it has to be the one the server writes to.
        // Constructing a second NetworkMetrics and sampling that instead is the mistake this makes
        // hard to make: there is nowhere else to get one from.
        telemetry.Metrics.Sample();
    }

    [Fact]
    public void StoppingTwiceIsNotAnError() {
        var telemetry = NetworkTelemetry.Start(Options);

        telemetry.Dispose();
        telemetry.Dispose();
    }

    /// <summary>Defaults alone are enough to start, which is what a container does.</summary>
    /// <remarks>
    ///     No endpoint, so the SDK takes <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and then its own default
    ///     of the local sidecar. That is the configuration a deployment supplies rather than a build,
    ///     and it has to be the one that needs no code.
    /// </remarks>
    [Fact]
    public void ItStartsWithNothingConfigured() {
        using var telemetry = NetworkTelemetry.Start(
            new() { ExportInterval = TimeSpan.FromSeconds(30), IncludeRuntimeMetrics = false }
        );

        telemetry.Metrics.Sample();
    }
}
