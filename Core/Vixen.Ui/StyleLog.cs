// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Ui;

/// <summary>What the styling pipeline says out loud, with the ids from docs/manual/log-events.md.</summary>
static partial class StyleLog {
    [LoggerMessage(
        EventId = 7004,
        Level = LogLevel.Warning,
        Message = "{Source} refused '{Text}': {Reason}. It was dropped; the rest of the stylesheet still "
            + "applies, so the visible effect is a rule that does nothing."
    )]
    public static partial void Refused(ILogger logger, string source, string text, string reason);

    [LoggerMessage(
        EventId = 7005,
        Level = LogLevel.Warning,
        Message = "An @apply could not be expanded: {Reason}. The declarations it stood for are missing "
            + "from the rule it was written in."
    )]
    public static partial void ApplyRefused(ILogger logger, string reason);

    /// <summary>Reports every refusal an <c>ApplyExpander</c> made while expanding one sheet.</summary>
    /// <param name="logger">Where it goes.</param>
    /// <param name="refusals">The expander's diagnostics, which it clears on its next call.</param>
    /// <remarks>
    ///     A loop rather than one line holding a joined string, so that
    ///     <c>LogRateLimiter</c> sees one event per distinct problem and a sheet with forty bad
    ///     utilities does not arrive as one unreadable record.
    /// </remarks>
    public static void ReportApplyRefusals(ILogger logger, IReadOnlyList<string> refusals) {
        for (var i = 0; i < refusals.Count; i++) {
            ApplyRefused(logger, refusals[i]);
        }
    }
}
