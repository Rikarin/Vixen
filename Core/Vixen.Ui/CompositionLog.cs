// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Ui;

/// <summary>What composition says out loud, with the ids from docs/manual/log-events.md.</summary>
static partial class CompositionLog {
    /// <summary>A two-way binding whose forward leg can never run again.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The failure a <c>bind:</c> is most likely to have and the one it could not
    ///         report.</b> The forward leg is an <c>Effect</c>, so it follows whatever the expression
    ///         <i>read</i>; over a plain property it reads nothing, runs once and is finished, while
    ///         the write-back leg keeps working perfectly. The result is a control that follows the
    ///         model until something other than the control writes it, and then silently stops —
    ///         which is the direction an author tests second.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A warning rather than the refusal a type mismatch gets, because this one
    ///         half-works.</b> <c>bind:Value="@Model.Name"</c> over a POCO nothing else writes is a
    ///         reasonable thing to have written and does what its author wanted; a mismatch is not
    ///         and never can be. What is not reasonable is being unable to tell the two apart, which
    ///         is what this line is for.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 7008,
        Level = LogLevel.Warning,
        Message = "The two-way binding on '{Tag}.{Property}' reads nothing reactive, so its forward leg runs "
            + "once and never again: the control will stop following the model as soon as anything other than "
            + "the control writes it. The write-back leg is unaffected. Bind a Signal<T>'s Value, or use "
            + "'change:' if a one-way write-back is what was meant."
    )]
    public static partial void InertBinding(ILogger logger, string tag, string property);
}
