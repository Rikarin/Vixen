// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Ui.Reactive;

/// <summary>What the signal graph logs, with the ids from docs/manual/log-events.md.</summary>
static partial class ReactiveLog {
    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Error,
        Message = "The effect declared at {Origin} re-triggered itself {Runs} times in one flush and has been "
            + "suspended. It writes something it also reads, directly or through a chain. The rest of the UI "
            + "keeps running; call Resume() once the cause is fixed."
    )]
    public static partial void EffectSuspended(ILogger logger, string origin, int runs);

    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Error,
        Message = "The effect declared at {Origin} threw and has been suspended."
    )]
    public static partial void EffectThrew(ILogger logger, string origin, Exception exception);

    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Warning,
        Message = "An effect flush hit its budget of {Budget} runs with work still queued; the remainder runs "
            + "next frame. A frame that keeps reaching this is a UI that is not settling."
    )]
    public static partial void FlushBudgetExhausted(ILogger logger, int budget);
}
