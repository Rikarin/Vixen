// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     The log as an <see cref="EventSource" />, so that <c>dotnet-trace</c>, PerfView, ETW and
///     LTTng can record it alongside the runtime's own events.
/// </summary>
/// <remarks>
///     <para>
///         What this buys that the other sinks do not: <b>one timeline</b>. A GC pause, a JIT
///         event, a thread-pool starvation warning and the engine's "device lost" line collected by
///         the same tool, ordered against each other by the same clock. Correlating a log file with
///         a separate trace by wall-clock timestamps is a job nobody does twice.
///     </para>
///     <para>
///         <c>dotnet-trace collect --providers Vixen-Diagnostics-Log</c>, and the level passed to
///         the collector filters at the source — a provider enabled at <c>Warning</c> never
///         formats the informational lines, which is the difference between tracing a running game
///         and changing what it measures.
///     </para>
///     <para>
///         One event per level rather than one event with a level argument, because that is what
///         makes the collector's own verbosity filter work: <see cref="EventLevel" /> is a property
///         of the event, not of its payload.
///     </para>
///     <para>
///         Ahead-of-time builds may have <c>EventSourceSupport</c> switched off, in which case the
///         writes compile to nothing and this sink costs its filter check. That is the intended
///         behaviour for a shipping build, and the reason the sink checks
///         <see cref="EventSource.IsEnabled()" /> rather than assuming anybody is listening.
///     </para>
/// </remarks>
public sealed class EventSourceSink : LogRecordSink {
    /// <summary>The provider name to give <c>dotnet-trace</c> or PerfView.</summary>
    public const string ProviderName = LogEventSource.SourceName;

    /// <summary>Creates a sink writing to the <c>Vixen-Diagnostics-Log</c> provider.</summary>
    /// <param name="minimumLevel">The level below which nothing is written.</param>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    /// <remarks>
    ///     The default is <see cref="LogLevel.Trace" />, which is not the default anywhere else in
    ///     this assembly and is deliberate: the collector decides what it wants when it enables the
    ///     provider, and a sink that had already dropped the debug lines would make
    ///     <c>--providers Vixen-Diagnostics-Log:::EventLevel=Verbose</c> a lie.
    /// </remarks>
    public EventSourceSink(LogLevel minimumLevel = LogLevel.Trace, LogFilter? filter = null) : base(filter) {
        if (filter is null) {
            MinimumLevel = minimumLevel;
        }
    }

    /// <inheritdoc />
    protected override void Write(LogRecord record) {
        var source = LogEventSource.Log;

        if (!source.IsEnabled()) {
            return;
        }

        var message = record.SuppressedCount > 0
            ? $"{record.Message} (repeated {record.SuppressedCount} times)"
            : record.Message;

        if (record.Exception is not null) {
            message = $"{message}{Environment.NewLine}{record.Exception}";
        }

        switch (record.Level) {
            case LogLevel.Trace:
                source.Trace(record.EventId.Id, record.Category, message);

                break;

            case LogLevel.Debug:
                source.Debug(record.EventId.Id, record.Category, message);

                break;

            case LogLevel.Information:
                source.Information(record.EventId.Id, record.Category, message);

                break;

            case LogLevel.Warning:
                source.Warning(record.EventId.Id, record.Category, message);

                break;

            case LogLevel.Error:
                source.Error(record.EventId.Id, record.Category, message);

                break;

            case LogLevel.Critical:
                source.Critical(record.EventId.Id, record.Category, message);

                break;

            default:
                break;
        }
    }
}

/// <summary>
///     The provider itself. Internal because the sink is the way to write to it and an
///     <see cref="EventSource" /> anybody can call is an event id anybody can invent.
/// </summary>
/// <remarks>
///     <c>WriteEventCore</c> rather than the <c>params object[]</c> overload of
///     <c>WriteEvent</c>: the latter boxes every argument and allocates an array per line, and its
///     reflection over the payload is exactly the shape ahead-of-time compilation cannot see
///     through.
/// </remarks>
[EventSource(Name = SourceName)]
sealed class LogEventSource : EventSource {
    public const string SourceName = "Vixen-Diagnostics-Log";

    public static readonly LogEventSource Log = new();

    LogEventSource() { }

    [Event(1, Level = EventLevel.Verbose, Message = "{2}")]
    public void Trace(int eventId, string category, string message) => WriteLine(1, eventId, category, message);

    [Event(2, Level = EventLevel.Verbose, Message = "{2}")]
    public void Debug(int eventId, string category, string message) => WriteLine(2, eventId, category, message);

    [Event(3, Level = EventLevel.Informational, Message = "{2}")]
    public void Information(int eventId, string category, string message) => WriteLine(3, eventId, category, message);

    [Event(4, Level = EventLevel.Warning, Message = "{2}")]
    public void Warning(int eventId, string category, string message) => WriteLine(4, eventId, category, message);

    [Event(5, Level = EventLevel.Error, Message = "{2}")]
    public void Error(int eventId, string category, string message) => WriteLine(5, eventId, category, message);

    [Event(6, Level = EventLevel.Critical, Message = "{2}")]
    public void Critical(int eventId, string category, string message) => WriteLine(6, eventId, category, message);

    [NonEvent]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification =
            "WriteEventCore is annotated because it will serialise an arbitrary object graph if given "
            + "one. This payload is three fields — an int and two strings — laid out by hand, which is "
            + "the case the annotation's own message names as safe to suppress. Nothing here is "
            + "reflected over, so there is nothing for the trimmer to remove."
    )]
    unsafe void WriteLine(int traceEventId, int eventId, string category, string message) {
        fixed (char* categoryPointer = category)
        fixed (char* messagePointer = message) {
            var payload = stackalloc EventData[3];
            payload[0].DataPointer = (nint)(&eventId);
            payload[0].Size = sizeof(int);
            payload[1].DataPointer = (nint)categoryPointer;
            payload[1].Size = (category.Length + 1) * sizeof(char);
            payload[2].DataPointer = (nint)messagePointer;
            payload[2].Size = (message.Length + 1) * sizeof(char);

            WriteEventCore(traceEventId, 3, payload);
        }
    }
}
