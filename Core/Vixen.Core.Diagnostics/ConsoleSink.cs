// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     The log on the terminal: one line per record, colourised by level, with the timestamp, the
///     level and the category in fixed columns.
/// </summary>
/// <remarks>
///     <para>
///         Development and server builds, not shipping ones. A shipped game has no terminal to write
///         to and pays for every string it formats; a dedicated server has nothing else, which is
///         why doc 17's variant table lists console for Development and full logging for Server and
///         gives Release the ring and the crash reporter only.
///     </para>
///     <para>
///         Aligned because a log is read by eye and by <c>grep</c>, and both want the level in the
///         same place on every line. The category column truncates from the front — a category that
///         does not fit keeps its tail, since <c>…Vulkan.Device</c> identifies the writer and
///         <c>Vixen.Graphics.V…</c> does not.
///     </para>
///     <para>
///         Colour is off when the output is redirected, when <c>NO_COLOR</c> is set, and when
///         <c>TERM</c> is <c>dumb</c> — a build log full of escape sequences is worse than a build
///         log without colour, and <c>NO_COLOR</c> is the one convention every tool agrees on.
///     </para>
///     <para>
///         Errors go to standard error, because that is the stream a container's log driver and a CI
///         runner both treat as the one worth alerting on.
///     </para>
/// </remarks>
public sealed class ConsoleSink : LogRecordSink {
    /// <summary>How wide the category column is when nothing else is asked for.</summary>
    public const int DefaultCategoryWidth = 28;

    // The 256-colour SGR codes, which every terminal that claims colour at all supports. Bright
    // white on red for Critical: the one level that should be impossible to scroll past.
    const string Reset = "\e[0m";
    const string Dim = "\e[2;37m";

    readonly Lock gate = new();
    readonly StringBuilder builder = new(256);
    readonly TextWriter output;
    readonly TextWriter error;

    /// <summary>Whether lines carry ANSI colour. Detected from the environment by default.</summary>
    public bool UseColour { get; set; }

    /// <summary>Whether lines start with a timestamp.</summary>
    public bool ShowTimestamps { get; set; } = true;

    /// <summary>The level from which lines go to standard error rather than standard output.</summary>
    public LogLevel StandardErrorThreshold { get; set; } = LogLevel.Error;

    /// <summary>How wide the category column is.</summary>
    public int CategoryWidth { get; set; } = DefaultCategoryWidth;

    /// <summary>Creates a sink writing to the process's console.</summary>
    /// <param name="minimumLevel">The level below which nothing is written.</param>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    public ConsoleSink(LogLevel minimumLevel = LogLevel.Information, LogFilter? filter = null)
        : this(Console.Out, Console.Error, minimumLevel, filter) =>
        UseColour = DetectColourSupport();

    internal ConsoleSink(
        TextWriter output,
        TextWriter error,
        LogLevel minimumLevel = LogLevel.Information,
        LogFilter? filter = null
    ) : base(filter) {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        this.output = output;
        this.error = error;

        // Only when this sink owns its filter. A shared one belongs to whoever composed it, and a
        // sink quietly rewriting the host's minimum level as it is constructed is a bug that takes
        // an afternoon to find.
        if (filter is null) {
            MinimumLevel = minimumLevel;
        }
    }

    /// <inheritdoc />
    protected override void Write(LogRecord record) {
        // One string, one write, under one lock: two threads each writing four fragments to the
        // console interleave into lines that belong to neither of them.
        lock (gate) {
            builder.Clear();
            Format(builder, record);

            var writer = record.Level >= StandardErrorThreshold ? error : output;
            writer.WriteLine(builder.ToString());

            if (record.Exception is not null) {
                writer.WriteLine(record.Exception);
            }
        }
    }

    static bool DetectColourSupport() {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) {
            return false;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal)) {
            return false;
        }

        return !Console.IsOutputRedirected;
    }

    static string ColourFor(LogLevel level) => level switch {
        LogLevel.Trace => "\e[90m",
        LogLevel.Debug => "\e[90m",
        LogLevel.Information => "\e[32m",
        LogLevel.Warning => "\e[33m",
        LogLevel.Error => "\e[31m",
        LogLevel.Critical => "\e[97;41m",
        _ => Reset
    };

    void Format(StringBuilder target, LogRecord record) {
        if (ShowTimestamps) {
            Paint(target, Dim);
            target.Append(record.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            Paint(target, Reset);
            target.Append(' ');
        }

        Paint(target, ColourFor(record.Level));
        target.Append(LogText.Abbreviate(record.Level));
        Paint(target, Reset);
        target.Append(' ');

        Paint(target, Dim);
        LogText.AppendCategory(target, record.Category, CategoryWidth);
        Paint(target, Reset);
        target.Append(' ');

        LogText.AppendMessage(target, record);
    }

    void Paint(StringBuilder target, string code) {
        if (UseColour) {
            target.Append(code);
        }
    }
}
