// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     The log on disk: JSON lines, rolling by day and by size, written on a background thread by
///     ZLogger.
/// </summary>
/// <remarks>
///     <para>
///         The file is what a player attaches to a bug report and what a dedicated server keeps
///         between restarts, so it is the one sink whose output is read by machines as often as by
///         people. JSON lines rather than formatted text for exactly that reason: <c>jq</c>, an
///         ingestion pipeline and a support engineer can all read it, and the structured fields the
///         <c>[LoggerMessage]</c> call site declared are still fields rather than having been
///         flattened into a sentence.
///     </para>
///     <para>
///         <b>Why ZLogger and not thirty more lines of our own.</b> ADR-008 names it, and the reason
///         is the half that is genuinely hard: an asynchronous writer with a bounded background
///         buffer, UTF-8 formatting straight into that buffer with no intermediate string, and file
///         rolling that does not lose the record being written when the file turns over. That is
///         the same argument as for the ring buffer being ours — this half is a solved problem and
///         that half is engine-specific.
///     </para>
///     <para>
///         <b>This sink forwards the caller's state rather than a formatted line.</b> It is the
///         reason it derives from <see cref="LogSink" /> rather than
///         <see cref="LogRecordSink" />: a <see cref="LogRecord" /> holds a
///         <see cref="string" /> that has already been assembled, and handing that to ZLogger would
///         pay for the allocation this sink exists to avoid and write a JSON document whose only
///         field is a sentence.
///     </para>
///     <para>
///         Rate limiting works here too, and what a suppressed run leaves behind is a record of its
///         own carrying <c>SuppressedCount</c> as a field — appending "(repeated N times)" to a
///         message that is stored structured would be putting the count in the one place a query
///         cannot reach it.
///     </para>
/// </remarks>
public sealed class ZLoggerFileSink : LogSink {
    /// <summary>The file name prefix used when none is given.</summary>
    public const string DefaultFileNamePrefix = "vixen";

    /// <summary>How large a file grows before it rolls, when nothing else is asked for.</summary>
    public const int DefaultRollingSizeKilobytes = 64 * 1024;

    readonly ZLoggerRollingFileLoggerProvider provider;

    /// <summary>
    ///     The directory this sink was given, or empty when it was constructed with a path selector
    ///     instead and the directory is therefore the selector's business.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>Creates a sink writing rolling JSON-line files into a directory.</summary>
    /// <param name="directoryPath">
    ///     The directory, as a host path — this is one of the few places in <c>Core</c> that has
    ///     one, because the file has to be findable by a player asked to attach it to a report and
    ///     a virtual path is not.
    /// </param>
    /// <param name="fileNamePrefix">What each file's name starts with.</param>
    /// <param name="rollingSizeKilobytes">How large a file grows before the next one starts.</param>
    /// <param name="minimumLevel">The level below which nothing is written.</param>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    /// <exception cref="ArgumentException">A path or prefix is null or empty.</exception>
    public ZLoggerFileSink(
        string directoryPath,
        string fileNamePrefix = DefaultFileNamePrefix,
        int rollingSizeKilobytes = DefaultRollingSizeKilobytes,
        LogLevel minimumLevel = LogLevel.Information,
        LogFilter? filter = null
    ) : this(
        SelectorFor(directoryPath, fileNamePrefix),
        rollingSizeKilobytes,
        minimumLevel,
        filter,
        directoryPath
    ) { }

    /// <summary>Creates a sink whose file names are chosen by a caller-supplied selector.</summary>
    /// <param name="filePathSelector">
    ///     Given the roll's timestamp and its sequence number within that timestamp, returns the
    ///     path to write. Called on the writer's thread.
    /// </param>
    /// <param name="rollingSizeKilobytes">How large a file grows before the next one starts.</param>
    /// <param name="minimumLevel">The level below which nothing is written.</param>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="filePathSelector" /> is null.</exception>
    public ZLoggerFileSink(
        Func<DateTimeOffset, int, string> filePathSelector,
        int rollingSizeKilobytes = DefaultRollingSizeKilobytes,
        LogLevel minimumLevel = LogLevel.Information,
        LogFilter? filter = null
    ) : this(filePathSelector, rollingSizeKilobytes, minimumLevel, filter, string.Empty) { }

    ZLoggerFileSink(
        Func<DateTimeOffset, int, string> filePathSelector,
        int rollingSizeKilobytes,
        LogLevel minimumLevel,
        LogFilter? filter,
        string directoryPath
    ) : base(filter) {
        ArgumentNullException.ThrowIfNull(filePathSelector);
        ArgumentOutOfRangeException.ThrowIfLessThan(rollingSizeKilobytes, 1);

        DirectoryPath = directoryPath;

        if (filter is null) {
            MinimumLevel = minimumLevel;
        }

        var options = new ZLoggerRollingFileOptions {
            FilePathSelector = filePathSelector,
            RollingInterval = RollingInterval.Day,
            RollingSizeKB = rollingSizeKilobytes,

            // Drop rather than block or grow. A game that stalls its frame loop because the disk is
            // busy has turned a diagnostic into a defect, and a buffer that grows without bound
            // turns it into an out-of-memory kill.
            FullMode = BackgroundBufferFullMode.Drop
        };

        options.UseJsonFormatter();
        provider = new(options);
    }

    /// <inheritdoc />
    public override ILogger CreateLogger(string categoryName) {
        ArgumentNullException.ThrowIfNull(categoryName);

        return new CategoryLogger(this, categoryName, provider.CreateLogger(categoryName));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Blocks until the background buffer is written. A log file that is missing the last
    ///     seconds before shutdown is missing the part that says why the shutdown happened.
    /// </remarks>
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);

        if (disposing) {
            provider.Dispose();
        }
    }

    static Func<DateTimeOffset, int, string> SelectorFor(string directoryPath, string fileNamePrefix) {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        ArgumentException.ThrowIfNullOrEmpty(fileNamePrefix);

        // Joined with a forward slash rather than System.IO.Path, which Core is barred from using
        // (VXIO0001) and which would be the wrong tool anyway: every operating system this runs on
        // accepts a forward slash, including the one whose own separator is a backslash.
        var directory = directoryPath.EndsWith('/') || directoryPath.EndsWith('\\')
            ? directoryPath[..^1]
            : directoryPath;

        return (timestamp, sequence) =>
            $"{directory}/{fileNamePrefix}-{timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{sequence.ToString("000", CultureInfo.InvariantCulture)}.jsonl";
    }

    sealed class CategoryLogger(ZLoggerFileSink sink, string category, ILogger inner) : ILogger {
        public bool IsEnabled(LogLevel logLevel) => sink.IsEnabled(category, logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!sink.ShouldWrite(category, logLevel, eventId, out var suppressed)) {
                return;
            }

            inner.Log(logLevel, eventId, state, exception, formatter);

            if (suppressed > 0) {
                var run = new SuppressedRun(suppressed);
                inner.Log(logLevel, eventId, run, null, SuppressedRun.Format);
            }
        }
    }

    /// <summary>
    ///     What a run of suppressed repeats leaves in the file: its own line, with the count as a
    ///     field. Implements the key-value shape <c>ILogger</c> consumers expect of a state object,
    ///     so ZLogger's JSON formatter emits <c>"SuppressedCount": 4812</c> rather than a string
    ///     somebody has to parse back out.
    /// </summary>
    /// <remarks>
    ///     The template is the last entry and is named <c>{OriginalFormat}</c>, which is not
    ///     decoration: every <c>ILogger</c> consumer, ZLogger included, reads the trailing entry as
    ///     the message template and the ones before it as the parameters. A state with a single
    ///     entry is therefore a state with no parameters at all, which is how this managed to write
    ///     the count nowhere the first time it was tried.
    /// </remarks>
    sealed class SuppressedRun(int count) : IReadOnlyList<KeyValuePair<string, object?>> {
        const string Template = "(repeated {SuppressedCount} times)";

        public static readonly Func<SuppressedRun, Exception?, string> Format =
            static (state, _) => $"(repeated {state.Repeats.ToString(CultureInfo.InvariantCulture)} times)";

        public int Repeats { get; } = count;

        public int Count => 2;

        public KeyValuePair<string, object?> this[int index] => index switch {
            0 => new("SuppressedCount", Repeats),
            1 => new("{OriginalFormat}", Template),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() {
            yield return this[0];
            yield return this[1];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
