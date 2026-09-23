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
    ///     ⚠ <b>The failure doc 43 § D3 predicted, given a name — and narrowed since.</b> A
    ///     <c>container-type</c> makes an element answerable about its own measured box. It used to
    ///     close a loop through the container's own contents — the verdict widens the content, the
    ///     content widens the container — and that loop is gone: a query container is a contained box
    ///     (<c>ContainmentReader</c>), so its contents cannot size it on the axis it answers. What is
    ///     left is a loop through its <i>surroundings</i>: the verdict changes the container's block
    ///     size, the block size moves it onto another flex line or into another track, and the line
    ///     or track gives it a different width. The settle loop bounds that rather than hanging, and
    ///     this is what an author gets instead of a silently stale panel. The cure is a definite
    ///     inline size.
    /// </remarks>
    [LoggerMessage(
        EventId = 7007,
        Level = LogLevel.Warning,
        Message = "The query container '{Container}' never settled: it measured {Width}×{Height} on the last "
            + "of {Passes} layout passes and its box was still moving. Its own @container verdicts are one "
            + "pass stale: its contents cannot size it, so what moved it is its surroundings answering what "
            + "the verdict did to its height — a flex line it wrapped onto, a track it resized. "
            + "Give it a definite inline size."
    )]
    public static partial void ContainerNeverSettled(
        ILogger logger,
        string container,
        float width,
        float height,
        int passes
    );

    /// <summary>A box that declared a scroll container and got a clip.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a refusal, and the second event in this range that is not.</b> The declaration
    ///         is understood and applied: <c>auto</c> and <c>scroll</c> reach the layout as
    ///         <c>Overflow.Scroll</c>, which drops the flex item's content-sized floor and reserves a
    ///         scrollbar gutter, and the draw list clips at the box's edges. What the author wrote it
    ///         for — reaching the content that hangs outside — is the one thing it does not do.
    ///         Nothing in <c>Vixen.Ui</c> scrolls off <c>overflow</c>; the only scroller is
    ///         <c>ScrollView</c>, which is styled <c>overflow: hidden</c> and drives bars of its own.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Invisible by construction, which is what makes it worth a line.</b> A box capped
    ///         at 320 px over 1 007 px of rows looks exactly like a box holding six rows; the seventh
    ///         is not drawn, not hit, not wheeled to, and nothing says it is there. The New Asset…
    ///         picker sat like that for as long as it had more than six kinds, with a passing test
    ///         that chose a row near the top. See <c>Rikarin/Vixen#1275</c>.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 7009,
        Level = LogLevel.Warning,
        Message = "'{Element}' declares '{Declaration}', and in this UI that clips and does not scroll: the box "
            + "cuts its content off at its edges and what hangs outside it cannot be reached by pointer, wheel or "
            + "keyboard. Put a ScrollView there, or write 'overflow: hidden' if the clip is what was meant."
    )]
    public static partial void OverflowDoesNotScroll(ILogger logger, string element, string declaration);

    /// <summary>A control put under a tag of its own that lost what its own tag's rules declare.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a refusal either — the third event in this range that is not.</b> The rename is
    ///         sanctioned and nothing was dropped. But a control's own user-agent rule is keyed on its
    ///         <c>TagName</c>, so <c>&lt;ScrollView tag="choice-scroller"&gt;</c> matches none of
    ///         <c>scroll-view { overflow: hidden; position: relative }</c>: without the first the
    ///         scrolled-off rows draw over whatever is above the view, and without the second the bars
    ///         anchor to some ancestor and the thumb is somewhere else on screen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Invisible where the mistake is.</b> The rule under the new tag was written by
    ///         copying the old tag's declarations, which is what a person does, and it is exactly what
    ///         loses the control's own. The New Asset… picker shipped like that. See
    ///         <c>Rikarin/Vixen#1327</c>.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 7010,
        Level = LogLevel.Warning,
        Message = "'{Element}' is a <{Control}> under a tag of its own, so it matches none of the rules for "
            + "<{Control}> and has none of what they declare: {Properties}. Restate them on the new tag's rule — "
            + "for a scroll view the clip and the bars' anchor are among them."
    )]
    public static partial void ControlLostItsOwnRule(ILogger logger, string element, string control, string properties);

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
