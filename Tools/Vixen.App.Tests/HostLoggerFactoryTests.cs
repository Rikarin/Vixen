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
}
