// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Collections;

namespace Vixen.Core.Diagnostics;

/// <summary>One log line, as it sits in the ring.</summary>
/// <param name="Timestamp">When it was written.</param>
/// <param name="Level">Its severity.</param>
/// <param name="EventId">The stable numeric id — see <c>docs/manual/log-events.md</c>.</param>
/// <param name="Category">The logger's category, which is the type that logged it.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">The exception, if one was attached.</param>
/// <param name="ThreadId">Which managed thread wrote it.</param>
public sealed record LogRecord(
    DateTimeOffset Timestamp,
    LogLevel Level,
    EventId EventId,
    string Category,
    string Message,
    Exception? Exception,
    int ThreadId
);

/// <summary>
///     The always-on log sink: the last N records in a ring, in memory, in every build. The editor
///     console reads it live and the crash reporter dumps it.
/// </summary>
/// <remarks>
///     <para>
///         A log that only exists on disk is a log nobody has when it matters — the interesting
///         moment is usually the thirty seconds before a crash, and asking a player for a file is
///         asking for nothing. A bounded ring costs a fixed amount of memory and always has that
///         thirty seconds.
///     </para>
///     <para>
///         Per-category minimum levels, so "turn on verbose asset loading without drowning in render
///         spam" works. Categories are matched by prefix, longest first, which makes
///         <c>Vixen.Graphics</c> a single switch for everything under it.
///     </para>
///     <para>
///         <b>Where this is not yet what doc 13 asks for.</b> Records hold a formatted
///         <see cref="string" />, not UTF-8 bytes with the structured fields intact, so an enabled
///         log line allocates. The disabled path already allocates nothing — a <c>[LoggerMessage]</c>
///         method returns before touching its arguments — and the enabled path is not in any hot
///         loop, because <c>[HotPath]</c> methods are barred from logging. Packing records into a
///         byte ring is the optimisation to make when a profile asks for it, and doing it now would
///         be guessing at the field layout the editor console wants.
///     </para>
/// </remarks>
public sealed class RingBufferSink : ILoggerProvider {
    /// <summary>How many records the ring holds when no capacity is given.</summary>
    public const int DefaultCapacity = 100_000;

    readonly RingBuffer<LogRecord> records;
    readonly Lock gate = new();
    readonly List<(string Prefix, LogLevel Level)> categoryLevels = [];

    /// <summary>The level below which nothing is recorded, regardless of category.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>How many records the ring holds.</summary>
    public int Capacity => records.Capacity;

    /// <summary>How many records it currently holds.</summary>
    public int Count {
        get {
            lock (gate) {
                return records.Count;
            }
        }
    }

    /// <summary>
    ///     How many records have been overwritten. Distinguishes "the log is missing its beginning"
    ///     from "nothing was logged", which a bare ring cannot.
    /// </summary>
    public long DroppedCount {
        get {
            lock (gate) {
                return records.OverwrittenCount;
            }
        }
    }

    /// <summary>Creates a sink holding at most <paramref name="capacity" /> records.</summary>
    /// <param name="capacity">The ring size.</param>
    public RingBufferSink(int capacity = DefaultCapacity) => records = new(capacity);

    /// <summary>
    ///     Sets the minimum level for a category and everything beneath it. The longest matching
    ///     prefix wins, so a specific rule beats a general one whatever order they were added in.
    /// </summary>
    /// <param name="categoryPrefix">The category prefix, such as <c>"Vixen.Graphics"</c>.</param>
    /// <param name="level">The minimum level for it.</param>
    public void SetCategoryLevel(string categoryPrefix, LogLevel level) {
        ArgumentException.ThrowIfNullOrEmpty(categoryPrefix);

        lock (gate) {
            categoryLevels.RemoveAll(rule => string.Equals(rule.Prefix, categoryPrefix, StringComparison.Ordinal));
            categoryLevels.Add((categoryPrefix, level));
            categoryLevels.Sort(static (left, right) => right.Prefix.Length.CompareTo(left.Prefix.Length));
        }
    }

    /// <summary>Forgets every per-category rule.</summary>
    public void ClearCategoryLevels() {
        lock (gate) {
            categoryLevels.Clear();
        }
    }

    /// <summary>Whether a record at this level and category would be kept.</summary>
    /// <param name="category">The logger category.</param>
    /// <param name="level">The level.</param>
    /// <returns><see langword="true" /> if it would be recorded.</returns>
    public bool IsEnabled(string category, LogLevel level) {
        if (level == LogLevel.None) {
            return false;
        }

        lock (gate) {
            foreach (var (prefix, configured) in categoryLevels) {
                if (category.StartsWith(prefix, StringComparison.Ordinal)) {
                    return level >= configured;
                }
            }
        }

        return level >= MinimumLevel;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

    /// <summary>Copies the records out, oldest first.</summary>
    /// <returns>The current contents of the ring.</returns>
    public LogRecord[] Snapshot() {
        lock (gate) {
            var snapshot = new LogRecord[records.Count];
            records.CopyTo(snapshot);
            return snapshot;
        }
    }

    /// <summary>Copies the newest records out, oldest of them first.</summary>
    /// <param name="destination">Where they go. Its length is how many are wanted.</param>
    /// <returns>How many were written, which is fewer if the ring holds fewer.</returns>
    /// <remarks>
    ///     What a log overlay wants and what <see cref="Snapshot" /> is wrong for: the ring holds a
    ///     hundred thousand records by default and a tail is thirty, so snapshotting once a frame to
    ///     read the end of it would allocate several megabytes a frame to show half a screen of text.
    /// </remarks>
    public int CopyTail(Span<LogRecord> destination) {
        lock (gate) {
            var take = Math.Min(destination.Length, records.Count);
            var first = records.Count - take;

            for (var index = 0; index < take; index++) {
                destination[index] = records[first + index];
            }

            return take;
        }
    }

    /// <summary>Empties the ring. Does not reset <see cref="DroppedCount" />.</summary>
    public void Clear() {
        lock (gate) {
            records.Clear();
        }
    }

    /// <summary>Nothing to release — the ring is managed memory and the sink outlives its loggers.</summary>
    public void Dispose() { }

    void Write(LogRecord record) {
        lock (gate) {
            records.Enqueue(record);
        }
    }

    sealed class CategoryLogger(RingBufferSink sink, string category) : ILogger {
        public bool IsEnabled(LogLevel logLevel) => sink.IsEnabled(category, logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            if (!IsEnabled(logLevel)) {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            sink.Write(
                new(
                    DateTimeOffset.UtcNow,
                    logLevel,
                    eventId,
                    category,
                    formatter(state, exception),
                    exception,
                    Environment.CurrentManagedThreadId
                )
            );
        }
    }
}
