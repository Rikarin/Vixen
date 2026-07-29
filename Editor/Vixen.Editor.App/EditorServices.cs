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

        return new EditorServices(
            platform.Capabilities.HasFlag(PlatformCapabilities.NativeDialogs) ? platform.Dialogs : null,
            platform.TryOpenUrl
        );
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
    public void When<T>(ValueTask<T> work, Action<T> next, Action<Exception> failed) {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(failed);

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
