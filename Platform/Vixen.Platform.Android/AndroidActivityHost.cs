// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Android.App;
using Android.OS;
using Android.Content;
using Android.Views;

namespace Vixen.Platform.Android;

/// <summary>
///     The entry point of an Android game: owns the platform, translates the activity lifecycle, and
///     drives one frame per display refresh.
/// </summary>
/// <remarks>
///     <para>
///         <b>Derive from this and override <see cref="Start" />.</b> As on iOS, this assembly knows
///         nothing about <c>Vixen.App</c> — it is in <c>Platform/</c> and the host is in
///         <c>Tools/</c> — so the frame callback is a delegate rather than a reference.
///     </para>
///     <para>
///         <b>Why the Choreographer rather than a render thread.</b> A dedicated thread is the usual
///         Android answer and is the wrong one here: the surface callbacks arrive on the main thread
///         and <c>surfaceDestroyed</c> may not return until nothing is using the window, which across
///         a thread boundary means a handshake on the hot path of every suspend. The Choreographer
///         posts on the main thread in step with vsync, so the surface's lifetime and the frame's are
///         ordered by construction rather than by a lock.
///     </para>
///     <para>
///         <b>The order on the way down is the part that matters.</b> <c>OnPause</c> stops the frame
///         callback before anything else happens, so by the time <c>surfaceDestroyed</c> arrives —
///         which is after <c>OnStop</c> — no frame is in flight and the window can be released
///         without a handshake. Doc 10 calls this the biggest source of bugs on the platform, and
///         this ordering is the whole of the answer.
///     </para>
/// </remarks>
public abstract class AndroidActivityHost : Activity, Choreographer.IFrameCallback {
    Choreographer? choreographer;
    Action? frame;
    bool running;

    /// <summary>The platform, once the activity has been created.</summary>
    public AndroidPlatform Platform { get; private set; } = null!;

    /// <summary>
    ///     Builds the game on the platform provided, and returns what to call once per frame.
    /// </summary>
    /// <param name="platform">The platform, already constructed and with no window yet.</param>
    /// <returns>The per-frame callback — in practice <c>application.RunFrame</c>.</returns>
    protected abstract Action Start(AndroidPlatform platform);

    /// <inheritdoc />
    public void DoFrame(long frameTimeNanos) {
        if (!running) {
            return;
        }

        // Re-posted first, so a frame that throws does not silently stop the loop as well —
        // the exception still escapes to the frame loop's own handling, which is where the
        // decision to log, tear down and rethrow belongs.
        choreographer?.PostFrameCallback(this);

        frame?.Invoke();
    }

    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState) {
        base.OnCreate(savedInstanceState);

        Platform = new(this);
        frame = Start(Platform);

        // The view the game asked for goes on screen here rather than in Start: the platform makes
        // it, and the activity is what has a content view to put it in.
        if (Platform.View is { } view) {
            SetContentView(view);
        }

        choreographer = Choreographer.Instance;
    }

    /// <inheritdoc />
    protected override void OnResume() {
        base.OnResume();

        Platform.MobileLifecycle.EnterForeground();

        if (!running) {
            running = true;
            choreographer?.PostFrameCallback(this);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Stops the frame callback <em>before</em> anything else. Everything below depends on no
    ///     frame being in flight.
    /// </remarks>
    protected override void OnPause() {
        running = false;
        choreographer?.RemoveFrameCallback(this);

        Platform.ReleaseTouches();
        Platform.MobileLifecycle.EnterBackground();

        base.OnPause();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The surface is about to go. <see cref="PlatformEventKind.Suspending" /> is raised here so
    ///     a renderer releases its swapchain while the window is still valid — after
    ///     <c>surfaceDestroyed</c> it is not, and there is no way to ask for it back.
    /// </remarks>
    protected override void OnStop() {
        Platform.MobileLifecycle.Suspend();
        base.OnStop();
    }

    /// <inheritdoc />
    protected override void OnDestroy() {
        running = false;
        choreographer?.RemoveFrameCallback(this);

        Platform.MobileLifecycle.Stopping();

        // One last frame, so the loop notices it is stopping and runs the game's shutdown. Android
        // gives no second chance after this returns.
        frame?.Invoke();

        Platform.Dispose();
        choreographer = null;

        base.OnDestroy();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>OnTrimMemory</c> is the graded one and this is the blunt one; both arrive, so this
    ///     defers to the graded reading rather than reporting twice at different levels.
    /// </remarks>
    public override void OnLowMemory() {
        Platform.MobileLifecycle.ReportMemoryPressure(MemoryPressure.Critical);
        base.OnLowMemory();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Android's levels are two scales in one enum: what to trim while running, and how close to
    ///     being killed a backgrounded process is. Both are mapped, because a backgrounded game about
    ///     to be killed has exactly as much reason to drop caches as a running one under pressure.
    /// </remarks>
    public override void OnTrimMemory(TrimMemory level) {
        Platform.MobileLifecycle.ReportMemoryPressure(
            level switch {
                TrimMemory.RunningCritical or TrimMemory.Complete => MemoryPressure.Critical,
                TrimMemory.RunningLow or TrimMemory.Moderate or TrimMemory.Background =>
                    MemoryPressure.Warning,
                TrimMemory.RunningModerate => MemoryPressure.Warning,
                _ => MemoryPressure.Normal
            }
        );

        base.OnTrimMemory(level);
    }
}
