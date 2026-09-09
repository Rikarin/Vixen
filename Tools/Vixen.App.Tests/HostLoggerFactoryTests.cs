// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     What the host's logger factory does after it has been closed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracle here cannot be "does <c>CreateLogger</c> throw?"</b> — it never did, and
///         that is the whole defect. <c>Dispose</c> disposed the providers and cleared the list, so
///         a subsequent <c>CreateLogger</c> handed back a fan-out over nothing: an
///         <see cref="ILogger" /> whose <c>IsEnabled</c> was false, whose <c>Log</c> iterated an
///         empty array, and which therefore accepted every record and wrote it nowhere. A disposed
///         factory was not dead, it was deaf. So the oracle is a channel a test can read back, and
///         the assertion is that the record's own words are in it.
///     </para>
///     <para>
///         ⚠ <b>The cached logger is the half that matters more</b>, and it is the half a
///         <c>CreateLogger</c>-only test misses. <c>VixenApplication</c> takes its logger in its
///         constructor and holds it for the process's life, so nothing on the shutdown path calls
///         <c>CreateLogger</c> again at all — the record that used to vanish was written through a
///         logger made long before.
///     </para>
/// </remarks>
public sealed class HostLoggerFactoryTests {
    /// <summary>The one message shape these tests write. CA1848 is an error in this tree.</summary>
    static readonly Action<ILogger, string, Exception?> Say =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1), "{Phase}");

    /// <summary>The same at Error, so the exception object has somewhere to go.</summary>
    static readonly Action<ILogger, string, Exception?> Blame =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2), "{Phase}");

    /// <summary>A record written before the close still goes to the sink, and nowhere else.</summary>
    [Fact]
    public void AnOpenFactoryWritesToItsProvidersAndNotToTheLastResort() {
        var provider = new RecordingProvider();
        var lastResort = new StringWriter(CultureInfo.InvariantCulture);
        using var factory = new HostLoggerFactory(provider) { LastResort = lastResort };

        Say(factory.CreateLogger("Vixen.Teardown"), "the wheels are still on", null);

        Assert.Equal(["Warning: the wheels are still on"], provider.Records);
        Assert.Equal(string.Empty, lastResort.ToString());
    }

    /// <summary>
    ///     A logger taken before the close, and used after it, says so instead of swallowing.
    /// </summary>
    [Fact]
    public void ALoggerCachedBeforeTheCloseDivertsRatherThanDiscards() {
        var provider = new RecordingProvider();
        var lastResort = new StringWriter(CultureInfo.InvariantCulture);
        var factory = new HostLoggerFactory(provider) { LastResort = lastResort };
        var cached = factory.CreateLogger("Vixen.Teardown");

        factory.Dispose();

        // ⚠ Asserted explicitly, because the source-generated logging methods gate on it: a closed
        // factory answering false here would skip the call below entirely and restore the silence
        // without failing anything else in this file.
        Assert.True(
            cached.IsEnabled(LogLevel.Trace),
            "a closed factory that reports nothing is enabled is one that formats nothing, and a "
            + "record it never formats is a record it never diverts."
        );

        Blame(cached, "shutting down", new InvalidOperationException("the wheels came off"));

        var written = lastResort.ToString();

        Assert.Contains("shutting down", written, StringComparison.Ordinal);
        Assert.Contains("Vixen.Teardown", written, StringComparison.Ordinal);
        Assert.Contains("the wheels came off", written, StringComparison.Ordinal);

        // The provider is closed; the record must not have been pushed into it after its Dispose.
        Assert.Empty(provider.Records);
    }

    /// <summary>The same for a logger asked for after the close, which is the filed shape.</summary>
    [Fact]
    public void ALoggerCreatedAfterTheCloseDivertsRatherThanDiscards() {
        var lastResort = new StringWriter(CultureInfo.InvariantCulture);
        var factory = new HostLoggerFactory(new RecordingProvider()) { LastResort = lastResort };

        factory.Dispose();

        Say(factory.CreateLogger("Vixen.Late"), "asked for a logger after the host had gone", null);

        Assert.Contains(
            "asked for a logger after the host had gone",
            lastResort.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     Adding a provider to a closed factory is the one case with no teardown excuse: it would
    ///     be leaked, never disposed and never written to, so it throws rather than diverting.
    /// </summary>
    [Fact]
    public void AddingAProviderAfterTheCloseThrows() {
        var factory = new HostLoggerFactory(new RecordingProvider());

        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.AddProvider(new RecordingProvider()));
    }

    /// <summary>
    ///     A provider added after the fact reaches the loggers that already exist, not only the ones
    ///     made from then on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The logger is taken <i>before</i> the <c>AddProvider</c> on purpose, and that is the
    ///     only arrangement that asks the question.</b> Every test in this file used to create its
    ///     loggers after construction, so the factory snapshotting its provider list into each
    ///     logger it handed out was invisible — and it is exactly the shape the two mobile samples
    ///     used, which is why their <c>PlatformSink</c> received nothing the host wrote at boot
    ///     (#1197).
    /// </remarks>
    [Fact]
    public void AProviderAddedLateReachesALoggerThatAlreadyExists() {
        var first = new RecordingProvider();
        using var factory = new HostLoggerFactory(first);
        var cached = factory.CreateLogger("Vixen.Boot");

        var late = new RecordingProvider();
        factory.AddProvider(late);

        Say(cached, "after the sink arrived", null);

        Assert.Equal(["Warning: after the sink arrived"], late.Records);
        Assert.Equal(["Warning: after the sink arrived"], first.Records);
    }

    /// <summary>
    ///     And the records written before it arrived are gone, which is why the host has a seam that
    ///     runs first rather than only this.
    /// </summary>
    [Fact]
    public void AProviderAddedLateDoesNotReceiveWhatWasWrittenBeforeIt() {
        using var factory = new HostLoggerFactory(new RecordingProvider());
        var cached = factory.CreateLogger("Vixen.Boot");

        Say(cached, "the whole boot", null);

        var late = new RecordingProvider();
        factory.AddProvider(late);

        // A log has no rewind. AddProvider reaching existing loggers is necessary and not
        // sufficient: what a mobile bring-up needs is the sink installed before the host logs, which
        // is AppBuilder.WithLoggerProvider.
        Assert.Empty(late.Records);
    }

    /// <summary>
    ///     The same category twice is the same logger — which is what bounds the cache that
    ///     <c>AddProvider</c> walks, and what <c>Microsoft.Extensions.Logging</c>'s own
    ///     factory does.
    /// </summary>
    [Fact]
    public void TheSameCategoryTwiceIsTheSameLogger() {
        using var factory = new HostLoggerFactory(new RecordingProvider());

        Assert.Same(factory.CreateLogger("Vixen.Boot"), factory.CreateLogger("Vixen.Boot"));
        Assert.NotSame(factory.CreateLogger("Vixen.Boot"), factory.CreateLogger("Vixen.Other"));
    }

    /// <summary>
    ///     A sink installed on the builder has the host's own boot in it; the same sink installed
    ///     through <c>WithServices</c> does not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The oracle is an order, not a count and not a duration</b>: how many records the
    ///     early sink already held at the moment the service callbacks ran. That number is zero for
    ///     any arrangement where the sink arrives after the host has logged, whatever the machine
    ///     was doing, and it is what the two mobile samples were getting (#1197).
    /// </remarks>
    [Fact]
    public void ASinkInstalledOnTheBuilderHasTheHostBootInIt() {
        var early = new RecordingProvider();
        var late = new RecordingProvider();
        var earlyWhenTheCallbacksRan = -1;

        using var application = VixenApp
            .Create(["--vixen-headless", "--vixen-workers", "1", "--vixen-frame-limit", "0"])
            .WithLoggerProvider(early)
            .WithServices(services => {
                earlyWhenTheCallbacksRan = early.Records.Count;
                services.LoggerFactory.AddProvider(late);
            })
            .Build(new SilentGame());

        Assert.True(
            earlyWhenTheCallbacksRan > 0,
            "a sink installed through WithLoggerProvider held nothing by the time the service "
            + "callbacks ran, which means the host logged nothing during Build and this test cannot "
            + "tell the two seams apart."
        );

        // And the other half, so the assertion above is not true by construction: this is what the
        // samples used to do, and it is empty.
        Assert.Empty(late.Records);
    }

    /// <summary>Closing twice disposes each provider once.</summary>
    [Fact]
    public void DisposeIsIdempotent() {
        var provider = new RecordingProvider();
        var factory = new HostLoggerFactory(provider);

        factory.Dispose();
        factory.Dispose();

        Assert.Equal(1, provider.Disposals);
    }

    /// <summary>A provider that keeps what it was given, so a dropped record is visible.</summary>
    sealed class RecordingProvider : ILoggerProvider {
        public List<string> Records { get; } = [];

        public int Disposals { get; private set; }

        public ILogger CreateLogger(string categoryName) => new Sink(Records);

        public void Dispose() => Disposals++;

        sealed class Sink(List<string> records) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) =>
                records.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    /// <summary>A game that does nothing, so the only records are the host's own.</summary>
    sealed class SilentGame : Game;
}
