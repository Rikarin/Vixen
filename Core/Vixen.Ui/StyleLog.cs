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

    /// <summary>The same refusal, when the fragment that caused it is not the whole rule.</summary>
    /// <remarks>
    ///     ⚠ <b>A second event rather than a <c>{Rule}</c> in 7004, because most refusals do not have
    ///     one.</b> A refusal names the fragment the compiler stopped on — <c>::before</c>, a
    ///     combinator, one declaration out of a block — and the fragment on its own says nothing
    ///     about <i>which</i> rule to go and change: a sheet with two <c>::before</c> rules produces
    ///     two 7004 lines differing only in their reason. Where the enclosing rule is known it is
    ///     named here. Where the fragment already <i>is</i> the rule — <c>@media (min-width: bananas)</c>
    ///     is both — 7004 stands, rather than a line reading "refused 'X' in 'X'".
    /// </remarks>
    [LoggerMessage(
        EventId = 7006,
        Level = LogLevel.Warning,
        Message = "{Source} refused '{Text}' in '{Rule}': {Reason}. It was dropped; the rest of the stylesheet "
            + "still applies, so the visible effect is a rule that does nothing."
    )]
    public static partial void RefusedIn(ILogger logger, string source, string text, string rule, string reason);

    /// <summary>A query container whose own box was still moving when the budget ran out.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure doc 43 § D3 predicted, given a name.</b> A <c>container-type</c> makes an
    ///     element answerable about its own measured box, so a container whose inline size is decided
    ///     by its <i>contents</i> closes a loop: the verdict widens the content, the content widens
    ///     the container, and the container's next verdict is different. The settle loop bounds that
    ///     rather than hanging, and until the containment coercion lands this is what an author gets
    ///     instead of a silently stale panel. The cure is a definite inline size — or a
    ///     <c>width: auto</c> in normal flow, which is sized by the parent and cannot depend on what
    ///     is inside it.
    /// </remarks>
    [LoggerMessage(
        EventId = 7007,
        Level = LogLevel.Warning,
        Message = "The query container '{Container}' never settled: it measured {Width}×{Height} on the last "
            + "of {Passes} layout passes and its box was still moving. Its own @container verdicts are one "
            + "pass stale, because a container sized by its contents can change the contents that size it. "
            + "Give it a definite inline size."
    )]
    public static partial void ContainerNeverSettled(
        ILogger logger,
        string container,
        float width,
        float height,
        int passes
    );

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
