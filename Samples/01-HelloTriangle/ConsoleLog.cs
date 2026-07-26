// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Samples.HelloTriangle;

/// <summary>Writes the log to the terminal.</summary>
/// <remarks>
///     <para>
///         The host keeps its log in a ring buffer and adds no providers of its own — the editor
///         console reads the ring, and a shipped game has no terminal to write to. A sample does, and
///         a smoke test that prints nothing is one you cannot tell apart from a smoke test that did
///         not run.
///     </para>
///     <para>
///         Deliberately about thirty lines. <c>Microsoft.Extensions.Logging.Console</c> would do this
///         better and would put a second logging package in the dependency register for the sake of a
///         sample, which ADR-008 is explicit about not doing.
///     </para>
/// </remarks>
sealed class ConsoleLogProvider : ILoggerProvider {
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    sealed class ConsoleLogger(string category) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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
