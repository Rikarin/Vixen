// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Core.Diagnostics;

/// <summary>Which categories, at which levels, a sink keeps.</summary>
/// <remarks>
///     <para>
///         Its own object rather than a set of properties on a sink, because doc 13 asks for
///         per-category levels that are live-editable in the editor and read from
///         <c>vixen.log.yaml</c> — and a host with five sinks would otherwise have five copies of
///         that configuration to keep in step. Hand the same <see cref="LogFilter" /> to every sink
///         and "turn on verbose asset loading" is one call; give each its own and the file can stay
///         verbose while the console stays quiet, which is the other thing hosts want.
///     </para>
///     <para>
///         Categories are matched by prefix, longest first, so <c>Vixen.Graphics</c> is a single
///         switch for everything under it and a rule naming one type still beats it whatever order
///         the two were added in.
///     </para>
/// </remarks>
public sealed class LogFilter {
    readonly Lock gate = new();
    readonly List<(string Prefix, LogLevel Level)> categoryLevels = [];

    /// <summary>The level below which nothing is recorded, regardless of category.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>How many per-category rules are in force.</summary>
    public int CategoryRuleCount {
        get {
            lock (gate) {
                return categoryLevels.Count;
            }
        }
    }

    /// <summary>
    ///     Sets the minimum level for a category and everything beneath it. The longest matching
    ///     prefix wins, so a specific rule beats a general one whatever order they were added in.
    /// </summary>
    /// <param name="categoryPrefix">The category prefix, such as <c>"Vixen.Graphics"</c>.</param>
    /// <param name="level">The minimum level for it.</param>
    /// <exception cref="ArgumentException"><paramref name="categoryPrefix" /> is null or empty.</exception>
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
        ArgumentNullException.ThrowIfNull(category);

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
}

/// <summary>
///     What every sink in this assembly has in common: a <see cref="LogFilter" />, an optional
///     <see cref="LogRateLimiter" />, and the <see cref="ILoggerProvider" /> face that
///     <c>ILoggerFactory</c> composes.
/// </summary>
/// <remarks>
///     <para>
///         Doc 13 lists six sinks. Five of them differ only in where the line ends up, and writing
///         the level check, the prefix match and the repeat suppression five times would guarantee
///         they drifted — the file sink would keep a rule the console had stopped honouring, and
///         nobody would notice until a support log was missing the line that mattered.
///     </para>
///     <para>
///         The one that does not fit is <see cref="ZLoggerFileSink" />: it forwards the caller's
///         <c>TState</c> untouched so that the JSON line keeps its structured fields, which a base
///         class that hands its subclasses a formatted <see cref="LogRecord" /> cannot express. So
///         the split is here rather than one level down — this class decides *whether* a record is
///         written, <see cref="LogRecordSink" /> decides *what shape* it arrives in.
///     </para>
/// </remarks>
public abstract class LogSink : ILoggerProvider {
    /// <summary>Which categories and levels this sink keeps.</summary>
    public LogFilter Filter { get; }

    /// <summary>
    ///     Suppression of repeated events, or <see langword="null" /> for none — which is the
    ///     default, because a sink that silently drops lines has to be asked for.
    /// </summary>
    public LogRateLimiter? RateLimiter { get; set; }

    /// <summary>The level below which nothing is recorded, regardless of category.</summary>
    /// <remarks>Shorthand for <see cref="LogFilter.MinimumLevel" /> on <see cref="Filter" />.</remarks>
    public LogLevel MinimumLevel {
        get => Filter.MinimumLevel;
        set => Filter.MinimumLevel = value;
    }

    /// <summary>Creates a sink, optionally sharing an existing filter.</summary>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    protected LogSink(LogFilter? filter = null) => Filter = filter ?? new();

    /// <summary>Sets the minimum level for a category and everything beneath it.</summary>
    /// <param name="categoryPrefix">The category prefix, such as <c>"Vixen.Graphics"</c>.</param>
    /// <param name="level">The minimum level for it.</param>
    public void SetCategoryLevel(string categoryPrefix, LogLevel level) =>
        Filter.SetCategoryLevel(categoryPrefix, level);

    /// <summary>Forgets every per-category rule.</summary>
    public void ClearCategoryLevels() => Filter.ClearCategoryLevels();

    /// <summary>Whether a record at this level and category would be kept.</summary>
    /// <param name="category">The logger category.</param>
    /// <param name="level">The level.</param>
    /// <returns><see langword="true" /> if it would be recorded.</returns>
    public bool IsEnabled(string category, LogLevel level) => Filter.IsEnabled(category, level);

    /// <inheritdoc />
    public abstract ILogger CreateLogger(string categoryName);

    /// <inheritdoc />
    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     The filter and the rate limiter in the order they have to run: a record the level check
    ///     rejects must not consume a rate-limit token, or a category nobody is listening to would
    ///     decide what a category somebody is listening to gets to say.
    /// </summary>
    /// <param name="category">The logger category.</param>
    /// <param name="level">The level.</param>
    /// <param name="eventId">The event id, which is what identifies a repeat.</param>
    /// <param name="suppressedCount">
    ///     How many identical records were dropped since the last one that got through. Non-zero
    ///     only on the record that ends a run of suppression.
    /// </param>
    /// <returns><see langword="true" /> if the record should be written.</returns>
    protected bool ShouldWrite(string category, LogLevel level, EventId eventId, out int suppressedCount) {
        suppressedCount = 0;

        if (!Filter.IsEnabled(category, level)) {
            return false;
        }

        var limiter = RateLimiter;

        return limiter is null || limiter.TryAdmit(category, eventId, level, out suppressedCount);
    }

    /// <summary>Releases what the sink holds.</summary>
    /// <param name="disposing">
    ///     <see langword="true" /> when called from <see cref="Dispose()" /> rather than a finalizer.
    /// </param>
    protected virtual void Dispose(bool disposing) { }
}

/// <summary>
///     A sink that receives each line as a formatted <see cref="LogRecord" />: everything except the
///     ZLogger file sink, which is the one that has a reason to see the caller's state instead.
/// </summary>
public abstract class LogRecordSink : LogSink {
    /// <summary>Creates a sink, optionally sharing an existing filter.</summary>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    protected LogRecordSink(LogFilter? filter = null) : base(filter) { }

    /// <summary>What stamps a record's time.</summary>
    /// <remarks>
    ///     ⚠ <b>Injectable for the same reason <see cref="LogRateLimiter" />'s is: a wall clock in a
    ///     record makes anything that looks at the record untestable.</b> The editor's console shows
    ///     the timestamp, and a golden screenshot of it is a picture that differs from itself every
    ///     run — which is a suite that fails at random and is therefore ignored. Defaulted to the
    ///     system clock, so nothing that does not care has to say anything.
    /// </remarks>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public sealed override ILogger CreateLogger(string categoryName) {
        ArgumentNullException.ThrowIfNull(categoryName);

        return new CategoryLogger(this, categoryName);
    }

    /// <summary>Writes one record wherever this sink writes.</summary>
    /// <param name="record">The record, already past the filter and the rate limiter.</param>
    /// <remarks>
    ///     Called on the thread that logged. A sink whose destination is slow — a socket, a file —
    ///     hands off here rather than blocking that thread, which is what
    ///     <see cref="RemoteSink" /> and <see cref="ZLoggerFileSink" /> both do.
    /// </remarks>
    protected abstract void Write(LogRecord record);

    sealed class CategoryLogger(LogRecordSink sink, string category) : ILogger {
        public bool IsEnabled(LogLevel logLevel) => sink.IsEnabled(category, logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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

            sink.Write(
                new(
                    sink.TimeProvider.GetUtcNow(),
                    logLevel,
                    eventId,
                    category,
                    formatter(state, exception),
                    exception,
                    Environment.CurrentManagedThreadId,
                    suppressed
                )
            );
        }
    }
}
