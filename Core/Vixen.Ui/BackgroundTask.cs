// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;

namespace Vixen.Ui;


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
///         <see cref="Report(float, string?)" /> queues, and <see cref="BackgroundTaskManager.Pump" /> applies the
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

    // ⚠ **Signal-backed properties, not signal-typed ones.** Every one of these keeps the shape it
    // had — `float Progress { get; }` — so nothing that reads a task had to change. What changed is
    // that reading one *inside a binding* subscribes to it, which is what lets a panel be written
    // as markup over the model rather than as a view that is told to look once a frame.
    //
    // ⚠ Safe because every write below already lands on the UI thread: the work reports through
    // `Owner.Post`, and the queue is applied in `BackgroundTaskManager.Pump`. A signal written from
    // the thread pool would be a race the reactive graph is entitled to refuse.
    readonly Signal<string> title;
    readonly Signal<string?> status = new(null);
    readonly Signal<float> progress = new(0f);
    readonly Signal<bool> indeterminate = new(true);
    readonly Signal<BackgroundTaskState> state = new(BackgroundTaskState.Running);
    readonly Signal<Exception?> failure = new(null);

    // ⚠ **A mirror of the token rather than the token itself.** `cancellation.IsCancellationRequested`
    // is read by the work, on whatever thread the work is on, and a signal read asserts the owning
    // thread — so the token stays a token and this says the same thing for the interface. `Cancel`
    // runs on the UI thread, which is the only place it is written.
    readonly Signal<bool> cancelled = new(false);

    internal BackgroundTask(BackgroundTaskManager owner, string title) {
        Owner = owner;
        this.title = new(title);

        // ⚠ Taken once and kept, because `CancellationTokenSource.Token` throws once the source has
        // been disposed — and the source is disposed the moment the task ends. A caller holding a
        // finished task and asking what it was watching would otherwise get an exception rather
        // than a token that is simply never going to fire again.
        Cancellation = cancellation.Token;
    }

    /// <summary>What it is called.</summary>
    public string Title {
        get => title.Value;
        private set => title.Value = value;
    }

    /// <summary>What it is doing right now — a file name, a step — or <c>null</c>.</summary>
    public string? Status {
        get => status.Value;
        private set => status.Value = value;
    }

    /// <summary>How far along it is, from zero to one.</summary>
    /// <remarks>Meaningless while <see cref="IsIndeterminate" />, which is what a task that has not
    ///     reported any progress is.</remarks>
    public float Progress {
        get => progress.Value;
        private set => progress.Value = value;
    }

    /// <summary>Whether it has said how far along it is.</summary>
    /// <remarks>
    ///     ⚠ <b>A flag rather than a negative <see cref="Progress" /></b>, for the reason
    ///     <c>ProgressBar</c> gives about the same distinction: a bar told <c>-1</c> for "I do not
    ///     know" is a bar that fills up the day somebody's arithmetic produces that number.
    /// </remarks>
    public bool IsIndeterminate {
        get => indeterminate.Value;
        private set => indeterminate.Value = value;
    }

    /// <summary>Where it has got to.</summary>
    public BackgroundTaskState State {
        get => state.Value;
        private set => state.Value = value;
    }

    /// <summary>What it threw, if it threw.</summary>
    public Exception? Failure {
        get => failure.Value;
        private set => failure.Value = value;
    }

    /// <summary>Whether it has been asked to stop.</summary>
    /// <remarks>
    ///     ⚠ <b>Answered from a signal the UI thread writes, not from the token.</b> The token is
    ///     what the <i>work</i> watches, from whatever thread it is on; a signal read asserts the
    ///     owning thread, so making this read the token reactive would put the graph in the way of
    ///     every cancellation check in a tight loop. <see cref="Cancel" /> sets both.
    /// </remarks>
    public bool IsCancellationRequested => cancelled.Value;

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
        if (!IsRunning) {
            return;
        }

        cancellation.Cancel();

        // ⚠ The mirror, and it is set after the token rather than before: the work may notice the
        // token and finish inside `Cancel`, and an interface that had already been told the request
        // landed is right either way round — one that had not would be a button still offering to
        // do what has been done.
        cancelled.Value = true;
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
