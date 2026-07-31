// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Ui;

/// <summary>The long operations the editor is running, and what they have got to.</summary>
/// <remarks>
///     <para>
///         <b>Never a modal progress dialog</b> — doc 11 — so this is a list, a status bar and a
///         panel. An import that takes forty seconds must not stop somebody opening a different
///         file while it runs, and an editor whose long operations block it is one where every long
///         operation has to be made short.
///     </para>
///     <para>
///         ⚠ <b>Every mutation lands on the UI thread, in <see cref="Pump" />.</b> Work runs
///         wherever the caller put it; what it reports is queued and applied at one point in the
///         frame. This is the whole of the threading design and it is deliberately the smallest one
///         that works: no locks around the task list, no concurrent collection for the UI to walk,
///         and a frame that sees one consistent set of numbers.
///     </para>
///     <para>
///         ⚠ <b>A finished task stays in the list until it is pumped away.</b> Otherwise a task that
///         completes between two frames never appears at all, and an import that failed instantly
///         reports nothing — which reads as an import that silently did nothing.
///     </para>
/// </remarks>
public sealed class BackgroundTaskManager {
    readonly ConcurrentQueue<Action> pending = new();

    // ⚠ A `CollectionSignal` rather than a `List`, and `Tasks` below is unchanged because it already
    // implements `IReadOnlyList<T>`. Counting it or indexing it inside a binding subscribes, so a
    // panel over this list is rebuilt when a task starts or stops rather than when somebody
    // remembers to ask. Every mutation is on the UI thread already — that is what `Pump` is.
    readonly CollectionSignal<BackgroundTask> tasks = new();
    readonly List<BackgroundTask> finished = [];

    /// <summary>What is running, oldest first.</summary>
    public IReadOnlyList<BackgroundTask> Tasks => tasks;

    /// <summary>Whether anything is running.</summary>
    public bool IsBusy => tasks.Count > 0;

    /// <summary>How far along everything is together, from zero to one.</summary>
    /// <remarks>
    ///     The mean over the determinate tasks, which is what a single status-bar bar shows. A task
    ///     that has not reported progress is left out rather than counted as zero: three imports of
    ///     which two are indeterminate would otherwise sit at a third of the way along and not move.
    /// </remarks>
    public float Progress {
        get {
            var total = 0f;
            var counted = 0;

            foreach (var task in tasks) {
                if (task.IsIndeterminate) {
                    continue;
                }

                total += task.Progress;
                counted++;
            }

            return counted == 0 ? 0f : total / counted;
        }
    }

    /// <summary>Raised after <see cref="Pump" /> applies anything, and when a task starts.</summary>
    public event Action<BackgroundTaskManager>? Changed;

    /// <summary>Raised once per task, after it stops, whatever it stopped as.</summary>
    /// <remarks>
    ///     Where the notification comes from: the manager knows a build failed and the notification
    ///     centre knows how to say so, and joining them here would make one of them need the other.
    /// </remarks>
    public event Action<BackgroundTask>? Ended;

    /// <summary>Starts a task the caller drives itself.</summary>
    /// <param name="title">What it is called.</param>
    /// <returns>The task. The caller must finish it with <see cref="Complete" /> or <see cref="Fail" />.</returns>
    /// <remarks>For work that is already asynchronous in its own way — a file watcher, a subprocess,
    ///     a server request — and does not want its body wrapped in a delegate.</remarks>
    public BackgroundTask Begin(string title) {
        ArgumentNullException.ThrowIfNull(title);

        var task = new BackgroundTask(this, title);

        tasks.Add(task);
        Changed?.Invoke(this);

        return task;
    }

    /// <summary>Starts a task and runs it.</summary>
    /// <param name="title">What it is called.</param>
    /// <param name="work">
    ///     What it does. Handed the task, so it can report progress and watch for cancellation.
    /// </param>
    /// <returns>The task.</returns>
    /// <remarks>
    ///     ⚠ <b>The work is not awaited and its exceptions do not escape.</b> A background task that
    ///     threw into an unobserved <c>Task</c> would take the process down on a later garbage
    ///     collection with a stack trace pointing at nothing. It ends as
    ///     <see cref="BackgroundTaskState.Failed" /> with the exception on it, which is what the
    ///     notification the user sees is made of.
    /// </remarks>
    public BackgroundTask Start(string title, Func<BackgroundTask, Task> work) {
        ArgumentNullException.ThrowIfNull(work);

        var task = Begin(title);

        _ = Task.Run(
            async () => {
                try {
                    await work(task).ConfigureAwait(false);
                    Post(() => Stop(task, BackgroundTaskState.Completed));
                } catch (OperationCanceledException) when (task.IsCancellationRequested) {
                    Post(() => Stop(task, BackgroundTaskState.Cancelled));
                } catch (Exception failure) {
                    Post(() => Stop(task, BackgroundTaskState.Failed, failure));
                }
            }
        );

        // ⚠ The token is deliberately not handed to `Task.Run`. A task cancelled before the pool
        // gets to it would then never start, so none of the three arms above would run and the
        // entry would sit in the list at nought per cent for as long as the editor is open.

        return task;
    }

    /// <summary>Says a task the caller was driving has finished.</summary>
    /// <param name="task">The task.</param>
    public void Complete(BackgroundTask task) => Post(() => Stop(task, BackgroundTaskState.Completed));

    /// <summary>Says a task the caller was driving has failed.</summary>
    /// <param name="task">The task.</param>
    /// <param name="failure">What went wrong.</param>
    public void Fail(BackgroundTask task, Exception? failure = null) =>
        Post(() => Stop(task, BackgroundTaskState.Failed, failure));

    /// <summary>Asks everything running to stop.</summary>
    /// <remarks>What the editor does on the way down, and what a project being closed does.</remarks>
    public void CancelAll() {
        foreach (var task in tasks) {
            task.Cancel();
        }
    }

    /// <summary>Applies everything the tasks have reported since the last frame.</summary>
    /// <param name="budget">
    ///     How many queued changes to apply at most. Zero applies all of them.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The budget is not a performance nicety, it is a livelock guard.</b> A task reporting
    ///     progress per file over a hundred thousand files can enqueue faster than a frame can
    ///     drain, and an unbounded pump would then never return — the editor would stop drawing
    ///     while claiming to be showing progress.
    /// </remarks>
    public void Pump(int budget = 4096) {
        var applied = 0;

        while ((budget <= 0 || applied < budget) && pending.TryDequeue(out var change)) {
            change();
            applied++;
        }

        if (finished.Count > 0) {
            foreach (var task in finished) {
                Ended?.Invoke(task);
            }

            finished.Clear();
        }

        if (applied > 0) {
            Changed?.Invoke(this);
        }
    }

    /// <summary>Queues something to happen on the UI thread.</summary>
    /// <param name="change">What to do.</param>
    internal void Post(Action change) => pending.Enqueue(change);

    void Stop(BackgroundTask task, BackgroundTaskState state, Exception? failure = null) {
        if (!tasks.Remove(task)) {
            return;
        }

        task.Finish(state, failure);

        // ⚠ Raised from Pump rather than here, even though this already runs there. A handler that
        // starts another task — a build that kicks off a content bake — would otherwise mutate the
        // list this call is in the middle of.
        finished.Add(task);
    }
}
