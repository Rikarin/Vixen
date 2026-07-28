// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>Where a piece of background work has got to.</summary>
public enum BackgroundTaskState : byte {
    /// <summary>Still going.</summary>
    Running,

    /// <summary>It finished.</summary>
    Completed,

    /// <summary>It threw.</summary>
    Failed,

    /// <summary>It was stopped.</summary>
    Cancelled
}

/// <summary>One long operation: an import, a build, a bake.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Reporting is safe from any thread and reading is only safe from the UI one.</b> The
///         work runs on a pool thread and the progress bar is read during layout, so a task's
///         numbers would otherwise change halfway through a frame — a status line drawn from a title
///         that was replaced between two reads is the kind of tearing nobody reproduces. So
///         <see cref="Report" /> queues, and <see cref="BackgroundTaskManager.Pump" /> applies the
///         queue once per frame at a point of the shell's choosing.
///     </para>
///     <para>
///         <b>Cancellation is a token, and it is the reason this exists rather than a
///         <c>Task</c>.</b> Doc 11 asks for "progress with cancellation, never a modal progress
///         dialog" — a modal dialog is what an editor grows when its long operations have no way to
///         be stopped, because stopping them means killing the editor.
///     </para>
/// </remarks>
public sealed class BackgroundTask : IDisposable {
    readonly CancellationTokenSource cancellation = new();

    internal BackgroundTask(BackgroundTaskManager owner, string title) {
        Owner = owner;
        Title = title;

        // ⚠ Taken once and kept, because `CancellationTokenSource.Token` throws once the source has
        // been disposed — and the source is disposed the moment the task ends. A caller holding a
        // finished task and asking what it was watching would otherwise get an exception rather
        // than a token that is simply never going to fire again.
        Cancellation = cancellation.Token;
    }

    /// <summary>What it is called.</summary>
    public string Title { get; private set; }

    /// <summary>What it is doing right now — a file name, a step — or <c>null</c>.</summary>
    public string? Status { get; private set; }

    /// <summary>How far along it is, from zero to one.</summary>
    /// <remarks>Meaningless while <see cref="IsIndeterminate" />, which is what a task that has not
    ///     reported any progress is.</remarks>
    public float Progress { get; private set; }

    /// <summary>Whether it has said how far along it is.</summary>
    /// <remarks>
    ///     ⚠ <b>A flag rather than a negative <see cref="Progress" /></b>, for the reason
    ///     <c>ProgressBar</c> gives about the same distinction: a bar told <c>-1</c> for "I do not
    ///     know" is a bar that fills up the day somebody's arithmetic produces that number.
    /// </remarks>
    public bool IsIndeterminate { get; private set; } = true;

    /// <summary>Where it has got to.</summary>
    public BackgroundTaskState State { get; private set; }

    /// <summary>What it threw, if it threw.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Whether it has been asked to stop.</summary>
    public bool IsCancellationRequested => cancellation.IsCancellationRequested;

    /// <summary>What the work should watch to know it has been asked to stop.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Whether it is still going.</summary>
    public bool IsRunning => State == BackgroundTaskState.Running;

    internal BackgroundTaskManager Owner { get; }

    /// <summary>Says how far along it is.</summary>
    /// <param name="progress">From zero to one.</param>
    /// <param name="status">What it is doing, or <c>null</c> to leave it as it was.</param>
    /// <remarks>Callable from the thread doing the work; the change lands on the next
    ///     <see cref="BackgroundTaskManager.Pump" />.</remarks>
    public void Report(float progress, string? status = null) {
        var clamped = Math.Clamp(progress, 0f, 1f);

        Owner.Post(
            () => {
                Progress = clamped;
                IsIndeterminate = false;
                Status = status ?? Status;
            }
        );
    }

    /// <summary>Says what it is doing without saying how far along it is.</summary>
    /// <param name="status">What it is doing.</param>
    public void Report(string status) {
        ArgumentNullException.ThrowIfNull(status);
        Owner.Post(() => Status = status);
    }

    /// <summary>Renames it.</summary>
    /// <param name="title">What to call it.</param>
    public void Rename(string title) {
        ArgumentNullException.ThrowIfNull(title);
        Owner.Post(() => Title = title);
    }

    /// <summary>Asks it to stop.</summary>
    /// <remarks>
    ///     ⚠ <b>Asks. The task is not cancelled until the work notices.</b> A manager that moved the
    ///     task to <see cref="BackgroundTaskState.Cancelled" /> here would take it out of the list
    ///     while it was still writing files, and the user would start the import again over the top
    ///     of the one that had not stopped.
    /// </remarks>
    public void Cancel() {
        if (IsRunning) {
            cancellation.Cancel();
        }
    }

    /// <summary>Releases what the cancellation cost.</summary>
    /// <remarks>Called by the manager when the task ends; a caller that finished one itself does
    ///     not have to.</remarks>
    public void Dispose() => cancellation.Dispose();

    internal void Finish(BackgroundTaskState state, Exception? failure = null) {
        if (!IsRunning) {
            return;
        }

        State = state;
        Failure = failure;

        if (state == BackgroundTaskState.Completed) {
            Progress = 1f;
            IsIndeterminate = false;
        }

        Dispose();
    }
}
