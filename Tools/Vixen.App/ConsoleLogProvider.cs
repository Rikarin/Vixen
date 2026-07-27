// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.App;

/// <summary>Writes the log to the terminal.</summary>
/// <remarks>
///     <para>
///         <b>On for every variant except <see cref="BuildVariant.Release" /></b>, which is
///         [doc 17](../../docs/plan/17-app-heads-and-shipping.md)'s variant table read literally:
///         Development lists "console" among the things it carries and Server lists "full logging",
///         while Release is "log ring + crash reporter only". A shipped game has no terminal to
///         write to and pays for every string it formats; a dedicated server has nothing else.
///     </para>
///     <para>
///         Until this existed the host added no providers at all, so a scaffolded game printed
///         nothing and <c>Samples/01</c> carried its own copy of these thirty lines — which is the
///         usual sign that they belong one layer down. A server logging into a ring buffer nobody
///         reads is the same bug with worse consequences.
///     </para>
///     <para>
///         Deliberately about thirty lines rather than
///         <c>Microsoft.Extensions.Logging.Console</c>, which would do it better and would put a
///         second logging package in the dependency register — ADR-008 takes
///         <c>Microsoft.Extensions.Logging.Abstractions</c> and no more.
///     </para>
/// </remarks>
/// <param name="minimum">The lowest level to write.</param>
public sealed class ConsoleLogProvider(LogLevel minimum = LogLevel.Information) : ILoggerProvider {
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, minimum);

    /// <inheritdoc />
    public void Dispose() { }

    sealed class ConsoleLogger(string category, LogLevel minimum) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

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

            // Errors to stderr, because that is the stream a container's log driver and a CI runner
            // both treat as the one worth alerting on.
            var writer = logLevel >= LogLevel.Error ? Console.Error : Console.Out;
            writer.WriteLine($"[{Abbreviate(logLevel)}] {category}: {formatter(state, exception)}");

            if (exception is not null) {
                writer.WriteLine(exception);
            }
        }

        static string Abbreviate(LogLevel level) => level switch {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };
    }
}
