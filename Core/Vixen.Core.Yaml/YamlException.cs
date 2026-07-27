// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Yaml;

/// <summary>A document that is not in the dialect, or not YAML at all.</summary>
public sealed class YamlParseException : Exception {
    /// <summary>The line it went wrong on, one-based, or zero if it is not known.</summary>
    public long Line { get; }

    /// <summary>The column, one-based, or zero if it is not known.</summary>
    public long Column { get; }

    /// <summary>Reports a problem.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="line">Where.</param>
    /// <param name="column">Where.</param>
    /// <param name="inner">What caused it, if anything.</param>
    public YamlParseException(string message, long line = 0, long column = 0, Exception? inner = null)
        : base(line > 0 ? $"({line},{column}): {message}" : message, inner) {
        Line = line;
        Column = column;
    }
}
