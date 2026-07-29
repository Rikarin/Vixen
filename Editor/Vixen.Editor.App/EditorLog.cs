// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>Where the editor's own log goes, and what puts anything in it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The editor had no log at all, which is why the Console panel was a line of text.</b>
///         <c>RingBufferSink</c> is on in every <i>game</i> — <c>VixenApp</c> builds one — and the
///         editor is not built by that host: it is a platform, a window and a frame loop of its own,
///         and nothing along that path ever made a sink. A console over an empty ring is a panel that
///         works perfectly and shows nothing, which is the worst of the two failures.
///     </para>
///     <para>
///         ⚠ <b>Every notification becomes a log line, and that is the point rather than a
///         convenience.</b> A notification is the editor deciding something is worth saying; a toast
///         says it for four seconds and an error's toast says it until dismissed, and after that the
///         only record is the notification history nothing has a view over. Mirroring them means the
///         console answers "what happened while I was in the other panel", which is the question it
///         is opened to answer.
///     </para>
///     <para>
///         ⚠ <b>The mirror is one-way and must stay that way.</b> The console reads the ring; the ring
///         is fed from notifications. A console that raised notifications for log lines would close
///         the loop, and a single logged warning would toast, log, toast, log.
///     </para>
/// </remarks>
sealed class EditorLog : IDisposable {
    /// <summary>What the editor's own messages are filed under in the console's category picker.</summary>
    /// <remarks>
    ///     Not a type name, unlike everything else that logs. A notification does not come from one
    ///     class — <c>ContentTasks</c>, the plugin loader and half of <c>EditorApplication</c> raise
    ///     them — and filing them under whichever of those happened to call would make the category
    ///     filter useless for exactly the lines somebody most wants to isolate.
    /// </remarks>
    public const string Category = "Vixen.Editor";

    readonly ILogger logger;

    Action<Notification>? mirror;
    NotificationCenter? center;

    /// <summary>Makes the sink the editor logs into.</summary>
    /// <param name="level">The lowest severity kept.</param>
    /// <remarks>
    ///     ⚠ <b>Debug rather than Information, and only because this ring is not a game's.</b> The
    ///     editor's own volume is a handful of lines a minute, and the console's default filter hides
    ///     the verbose stream anyway — so keeping it costs nothing and makes "turn on debug logging"
    ///     a toggle in the panel rather than a restart.
    /// </remarks>
    public EditorLog(LogLevel level = LogLevel.Debug) {
        Sink = new RingBufferSink(filter: new LogFilter { MinimumLevel = level });
        logger = Sink.CreateLogger(Category);
    }

    /// <summary>The ring the console reads.</summary>
    public RingBufferSink Sink { get; }

    /// <summary>Copies every notification into the log, for as long as this is alive.</summary>
    /// <param name="notifications">The centre to watch.</param>
    public void Mirror(NotificationCenter notifications) {
        ArgumentNullException.ThrowIfNull(notifications);

        center = notifications;
        mirror = Write;

        notifications.Shown += mirror;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (center is not null && mirror is not null) {
            center.Shown -= mirror;
        }

        Sink.Dispose();
    }

    /// <summary>Writes a line the editor wants in the console but not on screen.</summary>
    /// <param name="level">How much it matters.</param>
    /// <param name="message">What it says.</param>
    /// <remarks>
    ///     ⚠ <b>Through the raw <c>ILogger.Log</c> and not a <c>[LoggerMessage]</c> method, which
    ///     the analyzers would rather see.</b> Those exist so a disabled log line costs nothing and
    ///     so a structured sink gets its fields; both are arguments about a hot path, and this is
    ///     the editor telling somebody it saved a file. A generated method per sentence would be
    ///     forty partial methods for forty strings.
    /// </remarks>
    public void Write(LogLevel level, string message) =>
        logger.Log(level, default, message, null, static (state, _) => state);

    void Write(Notification notification) =>
        Write(
            notification.Severity switch {
                NotificationSeverity.Error => LogLevel.Error,
                NotificationSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Information
            },
            notification.Detail is { Length: > 0 } detail
                ? notification.Message + " — " + detail
                : notification.Message
        );
}
