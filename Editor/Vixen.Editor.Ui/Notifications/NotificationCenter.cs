// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>How much a message matters.</summary>
public enum NotificationSeverity : byte {
    /// <summary>Something happened.</summary>
    Info,

    /// <summary>Something worked.</summary>
    Success,

    /// <summary>Something is not right, and the editor carried on.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error
}

/// <summary>One message.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Message">What it says.</param>
/// <param name="Detail">The second line — a file, a path, a compiler diagnostic — or <c>null</c>.</param>
/// <param name="When">When it was raised, on the shell's clock.</param>
public readonly record struct Notification(
    NotificationSeverity Severity,
    string Message,
    string? Detail,
    TimeSpan When
);

/// <summary>What the editor has told the user, and the corner it told them in.</summary>
/// <remarks>
///     <para>
///         <b>A toast plus a history, because a toast on its own is a message nobody can go back
///         to.</b> The thing a user does after an import fails is look away, look back, and find
///         the message gone — so every toast is also an entry in a list the notification panel
///         shows.
///     </para>
///     <para>
///         ⚠ <b>An error does not expire.</b> <see cref="ToastHost.Tick" /> takes a toast away when
///         its duration is up, and a duration is right for "Saved" and wrong for "Shader failed to
///         compile" — an error that disappears while somebody is reading the line number is an
///         error they have to reproduce. Errors are dismissed by hand.
///     </para>
///     <para>
///         <b>The history is bounded.</b> An editor left open for a week with a watcher re-importing
///         a texture every save would otherwise hold every message it ever made.
///     </para>
/// </remarks>
public sealed class NotificationCenter {
    readonly List<Notification> history = [];
    readonly ToastHost toasts;

    TimeSpan now;

    /// <summary>Shows messages in a document's toast corner.</summary>
    /// <param name="toasts">The corner.</param>
    public NotificationCenter(ToastHost toasts) {
        ArgumentNullException.ThrowIfNull(toasts);
        this.toasts = toasts;
    }

    /// <summary>What has been shown, newest first.</summary>
    public IReadOnlyList<Notification> History => history;

    /// <summary>How many messages are kept.</summary>
    public int HistoryLimit { get; set; } = 200;

    /// <summary>How long an ordinary message stays on screen.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Raised when a message arrives or the history is cleared.</summary>
    public event Action<NotificationCenter>? Changed;

    /// <summary>Shows a message.</summary>
    /// <param name="message">What it says.</param>
    /// <param name="severity">How much it matters.</param>
    /// <param name="detail">A second line, or <c>null</c>.</param>
    /// <returns>The entry, which is also the first of <see cref="History" />.</returns>
    public Notification Show(string message, NotificationSeverity severity = NotificationSeverity.Info, string? detail = null) {
        ArgumentNullException.ThrowIfNull(message);

        var entry = new Notification(severity, message, detail, now);

        history.Insert(0, entry);

        if (history.Count > HistoryLimit) {
            history.RemoveRange(HistoryLimit, history.Count - HistoryLimit);
        }

        var toast = toasts.Show(detail is null ? message : message + " — " + detail, Variant(severity));

        // ⚠ `TimeSpan.MaxValue` rather than a very long one, so that no arithmetic anywhere makes an
        // error expire after a fortnight of uptime.
        toast.Duration = severity == NotificationSeverity.Error ? TimeSpan.MaxValue : Duration;

        Changed?.Invoke(this);
        return entry;
    }

    /// <summary>Shows a message about something that worked.</summary>
    /// <param name="message">What it says.</param>
    /// <returns>The entry.</returns>
    public Notification Success(string message) => Show(message, NotificationSeverity.Success);

    /// <summary>Shows a message about something that did not.</summary>
    /// <param name="message">What it says.</param>
    /// <param name="detail">A second line, or <c>null</c>.</param>
    /// <returns>The entry.</returns>
    public Notification Error(string message, string? detail = null) =>
        Show(message, NotificationSeverity.Error, detail);

    /// <summary>Throws the history away. What is on screen stays there.</summary>
    public void Clear() {
        history.Clear();
        Changed?.Invoke(this);
    }

    /// <summary>Tells the corner what time it is, so that expired toasts go away.</summary>
    /// <param name="time">The current time, on the same clock as the input events.</param>
    /// <remarks>
    ///     Forwarded to <see cref="ToastHost.Tick" /> rather than replacing it, and kept here as
    ///     well because it is what stamps an entry's <see cref="Notification.When" /> — a
    ///     notification centre that read the wall clock would put a different time on the entry
    ///     from the one the rest of the frame is working in.
    /// </remarks>
    public void Tick(TimeSpan time) {
        now = time;
        toasts.Tick(time);
    }

    /// <summary>How a severity is drawn.</summary>
    /// <param name="severity">The severity.</param>
    /// <returns>The variant a toast, an alert or a badge should use.</returns>
    /// <remarks>
    ///     One mapping, used by the toast and by the notification list, so the two cannot disagree
    ///     about what a warning looks like.
    /// </remarks>
    public static ControlVariant Variant(NotificationSeverity severity) =>
        severity switch {
            NotificationSeverity.Success => ControlVariant.Success,
            NotificationSeverity.Warning => ControlVariant.Warning,
            NotificationSeverity.Error => ControlVariant.Danger,
            _ => ControlVariant.Default
        };
}
