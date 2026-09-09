// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Platform;

namespace Vixen.Editor.App;

/// <summary>What the host can do that the application cannot ask for itself.</summary>
/// <param name="Dialogs">The OS's own pickers, or <see langword="null" /> where there are none.</param>
/// <param name="OpenUrl">Hands a link to the machine's browser, or <see langword="null" />.</param>
/// <remarks>
///     <para>
///         <b>Three things, and every one of them is a runtime question with a runtime answer.</b>
///         Doc 20's rule for the file dialogs is that the commands <i>grey themselves out</i> on Web
///         and Android rather than being absent — the same shape <c>view.float-panel</c> already
///         follows — so what arrives here is a capability and not a compile-time decision. An
///         application handed none of them is the one the tests build: every command still exists,
///         and the ones that need a picker are disabled with a reason.
///     </para>
///     <para>
///         ⚠ <b>The application takes this rather than an <c>IPlatform</c>.</b> A platform is a
///         window, a swapchain, an event pump and a file system, and an editor holding one would be
///         an editor that cannot be constructed without a display — which is exactly what
///         <c>EditorFixture</c> does forty times a test run.
///     </para>
/// </remarks>
sealed record EditorServices(INativeDialogs? Dialogs, Func<string, bool>? OpenUrl) {
    /// <summary>What a headless host, and every test, gets.</summary>
    public static EditorServices None { get; } = new(null, null);

    /// <summary>What the running editor gets.</summary>
    /// <param name="platform">The platform the host opened its window on.</param>
    /// <returns>The services, with whatever the platform actually supports in them.</returns>
    public static EditorServices Of(IPlatform platform) {
        ArgumentNullException.ThrowIfNull(platform);

        // ⚠ `Pickers()` rather than the capability test this open-coded, and the two are the same
        // line. It was the only place in the repository that knew to ask — `IPlatform.Dialogs` is
        // never null and a platform with no pickers answers every one of them with nothing-chosen,
        // which is exactly what Cancel looks like — so the question is spelled once, where the next
        // caller will find it.
        return new EditorServices(platform.Pickers(), platform.TryOpenUrl);
    }

    /// <summary>Whether there is a file picker to open.</summary>
    /// <remarks>What every dialog-backed command's enablement reads.</remarks>
    public bool CanPick => Dialogs is not null;
}

/// <summary>Work that finished somewhere else, waiting for the frame thread to apply it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A native dialog's answer does not come back on the thread that asked for it.</b>
///         <c>INativeDialogs</c> is asynchronous because the platform runs its own event loop while a
///         picker is open, and the continuation resumes wherever the implementation happened to
///         complete — which for the SDL and Win32 paths is not reliably the frame thread. Everything
///         a picker's answer touches — the document, the panels, the notifications — belongs to
///         whoever is drawing them.
///     </para>
///     <para>
///         The same queue-and-pump shape <see cref="ContentTasks" /> uses, kept separate because the
///         two drain at different points in the frame and merging them would mean a dialog's answer
///         waiting on a content build's.
///     </para>
/// </remarks>
sealed class Deferred {
    readonly ConcurrentQueue<Action> queue = [];

    /// <summary>Queues something to run on the next frame.</summary>
    /// <param name="work">What to run.</param>
    public void Post(Action work) {
        ArgumentNullException.ThrowIfNull(work);
        queue.Enqueue(work);
    }

    /// <summary>Runs everything queued. Called once a frame, on the frame thread.</summary>
    public void Pump() {
        // ⚠ Bounded by what was there when the pump started, not by the queue emptying. Work posted
        // from inside a handler here — a picker whose answer opens a second picker — belongs to the
        // next frame; draining until empty is how one frame becomes a loop.
        for (var remaining = queue.Count; remaining > 0 && queue.TryDequeue(out var work); remaining--) {
            work();
        }
    }

    /// <summary>Runs a continuation on the frame thread when a platform call answers.</summary>
    /// <typeparam name="T">What it answers with.</typeparam>
    /// <param name="work">The call.</param>
    /// <param name="next">What to do with the answer.</param>
    /// <param name="failed">What to do if it threw.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A call that has already answered is queued from here, and until it was, this
    ///         method turned every synchronous answer into a wait on the thread pool's mood.</b>
    ///         <c>ContinueWith</c> against <see cref="TaskScheduler.Default" /> schedules a pool work
    ///         item <em>even for a task that has already completed</em> — it does not run inline —
    ///         so a provider that answered without yielding still had its <c>Post</c> made by a pool
    ///         thread at whatever moment the pool got round to it. On an idle machine that is
    ///         microseconds and invisible; on a machine running fifteen other test assemblies it is
    ///         however long the queue is, and it is
    ///         <see href="https://github.com/Rikarin/Vixen/issues/1179">#1179</see>: the sweep
    ///         <c>SourceControlColumnTests</c> waited for was <em>finished</em>, and what had not
    ///         happened was the hand-off.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Posted rather than run, and that distinction is the whole contract.</b> The
    ///         promise is "on the frame thread, at the next pump", not "on the frame thread now" — a
    ///         caller part-way through a frame must not have its own continuation re-enter it. So the
    ///         fast path removes a pool hop and changes nothing else about when the work runs.
    ///     </para>
    ///     <para>
    ///         A cancelled call runs neither callback, on both paths. That was already the behaviour:
    ///         the continuation below tests <c>IsFaulted</c> and <c>IsCompletedSuccessfully</c> and
    ///         does nothing when neither holds.
    ///     </para>
    /// </remarks>
    public void When<T>(ValueTask<T> work, Action<T> next, Action<Exception> failed) {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(failed);

        if (work.IsCompletedSuccessfully) {
            var answer = work.Result;

            Post(() => next(answer));
            return;
        }

        if (work.IsCompleted) {
            // Faulted or cancelled. `AsTask` on a completed ValueTask hands back a completed Task
            // rather than starting anything, and a cancelled one carries no exception to report.
            if (work.AsTask().Exception is { } thrown) {
                var failure = thrown.GetBaseException();

                Post(() => failed(failure));
            }

            return;
        }

        work.AsTask().ContinueWith(
            finished => {
                if (finished.IsFaulted) {
                    var failure = finished.Exception.GetBaseException();
                    Post(() => failed(failure));
                } else if (finished.IsCompletedSuccessfully) {
                    var result = finished.Result;
                    Post(() => next(result));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default
        );
    }
}
