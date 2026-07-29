// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vixen.Core.Diagnostics;

/// <summary>
///     The few pieces of rendering every text sink shares, in one place so that a line in the
///     console, in the platform log and in a remote console read the same.
/// </summary>
static class LogText {
    /// <summary>The four-character level tag. Fixed width, which is what makes a column align.</summary>
    public static string Abbreviate(LogLevel level) => level switch {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none"
    };

    /// <summary>
    ///     Appends the message and, when a rate limiter dropped repeats before it, what it dropped.
    /// </summary>
    public static void AppendMessage(StringBuilder builder, LogRecord record) {
        builder.Append(record.Message);

        if (record.SuppressedCount > 0) {
            builder.Append(" (repeated ")
                .Append(record.SuppressedCount.ToString(CultureInfo.InvariantCulture))
                .Append(" times)");
        }
    }

    /// <summary>
    ///     Shortens a category to fit a column, keeping the end. <c>Vixen.Graphics.Vulkan.Device</c>
    ///     truncated from the front reads as <c>Vixen.Graphics.V…</c>, which distinguishes nothing;
    ///     the type name is the part that says which of forty loggers wrote the line.
    /// </summary>
    public static void AppendCategory(StringBuilder builder, string category, int width) {
        if (width <= 1) {
            return;
        }

        if (category.Length <= width) {
            builder.Append(category).Append(' ', width - category.Length);

            return;
        }

        builder.Append('…').Append(category, category.Length - (width - 1), width - 1);
    }
}
