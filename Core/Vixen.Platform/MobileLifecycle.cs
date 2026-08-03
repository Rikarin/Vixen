// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Platform;

/// <summary>
///     The application lifecycle as a phone actually has one: foreground, background, and being
///     killed without ceremony.
/// </summary>
/// <remarks>
///     <para>
///         <b>Shared, because the two platforms differ in vocabulary and not in shape.</b> iOS says
///         <c>willResignActive</c>, <c>didEnterBackground</c>, <c>willEnterForeground</c>,
///         <c>didBecomeActive</c>, <c>didReceiveMemoryWarning</c>; Android says <c>onPause</c>,
///         <c>onStop</c>, <c>onRestart</c>, <c>onResume</c>, <c>onTrimMemory</c>. The states behind
///         them are the same three and the transitions are the same, so the state machine is here
///         and each platform is a translation of its own callbacks into
///         <see cref="EnterBackground" />, <see cref="Suspend" /> and the rest.
///     </para>
///     <para>
///         <b>Every transition raises an event, and it is the same event on both.</b> Doc 10 names
///         lifecycle as the biggest source of bugs on Android specifically because the surface is
///         destroyed and recreated underneath a running renderer. A subsystem that must react — the
///         swapchain, the streaming manager — reacts to <see cref="PlatformEventKind.Suspending" />
///         and <see cref="PlatformEventKind.Resumed" />, not to anything platform-shaped.
///     </para>
///     <para>
///         <b>Quitting is a request the platform is entitled to ignore.</b> There is no supported way
///         for an iOS application to terminate itself, and an Android one that finishes its activity
///         may be kept alive. <see cref="RequestQuit" /> therefore raises
///         <see cref="PlatformEventKind.Quit" /> so the frame loop stops, and leaves the process to
///         the operating system — which is the honest behaviour rather than a call to
///         <c>Environment.Exit</c> that Apple rejects applications for.
///     </para>
/// </remarks>
/// <param name="events">Where transitions are posted.</param>
public sealed class MobileLifecycle(PlatformEventBuffer events) : ILifecycle {
    readonly PlatformEventBuffer events = events
        ?? throw new ArgumentNullException(nameof(events));

    /// <inheritdoc />
    public ApplicationState State { get; private set; } = ApplicationState.Starting;

    /// <inheritdoc />
    public MemoryPressure MemoryPressure { get; private set; } = MemoryPressure.Normal;

    /// <inheritdoc />
    public bool IsQuitRequested { get; private set; }

    /// <summary>The application became the one the user is looking at.</summary>
    /// <remarks>
    ///     <c>applicationDidBecomeActive</c>, <c>onResume</c>. Raises
    ///     <see cref="PlatformEventKind.Resumed" /> only when something was actually resumed, so a
    ///     launch is not reported as a resume — a renderer that rebuilds its swapchain on every
    ///     resume would otherwise rebuild one that was never lost.
    /// </remarks>
    public void EnterForeground() {
        if (State is ApplicationState.Stopping or ApplicationState.Running) {
            return;
        }

        var resumed = State is ApplicationState.Background or ApplicationState.Suspended;
        State = ApplicationState.Running;

        if (resumed) {
            events.Post(PlatformEvent.Application(PlatformEventKind.Resumed, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>The application is still alive but no longer frontmost.</summary>
    /// <remarks>
    ///     <c>applicationWillResignActive</c>, <c>onPause</c>. The app switcher, a notification
    ///     shade, a phone call. Rendering should stop; nothing has been destroyed.
    /// </remarks>
    public void EnterBackground() {
        if (State is ApplicationState.Stopping or ApplicationState.Background) {
            return;
        }

        State = ApplicationState.Background;
    }

    /// <summary>The application is off screen and its surface may not survive.</summary>
    /// <remarks>
    ///     <c>applicationDidEnterBackground</c>, <c>onStop</c> and <c>surfaceDestroyed</c>. This is
    ///     the one that matters: from here the GPU may not be touched at all on iOS, and on Android
    ///     the window the swapchain was built from is gone. Raised as
    ///     <see cref="PlatformEventKind.Suspending" /> once, on the way in.
    /// </remarks>
    public void Suspend() {
        if (State is ApplicationState.Stopping or ApplicationState.Suspended) {
            return;
        }

        State = ApplicationState.Suspended;
        events.Post(PlatformEvent.Application(PlatformEventKind.Suspending, Stopwatch.GetTimestamp()));
    }

    /// <summary>The system is short of memory.</summary>
    /// <param name="pressure">How short.</param>
    /// <remarks>
    ///     <c>didReceiveMemoryWarning</c>, <c>onTrimMemory</c>. Raises
    ///     <see cref="PlatformEventKind.LowMemory" /> on every report rather than only on a change,
    ///     because a second warning at the same level means the first was not enough — treating it
    ///     as a no-op is how an application gets killed while holding a cache it was told twice to
    ///     drop.
    /// </remarks>
    public void ReportMemoryPressure(MemoryPressure pressure) {
        MemoryPressure = pressure;

        if (pressure is not MemoryPressure.Normal) {
            events.Post(PlatformEvent.Application(PlatformEventKind.LowMemory, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>The process is going away.</summary>
    /// <remarks><c>applicationWillTerminate</c>, <c>onDestroy</c>. Terminal: nothing moves after it.</remarks>
    public void Stopping() {
        State = ApplicationState.Stopping;
        QuitReceived();
    }

    /// <inheritdoc />
    public void RequestQuit() {
        if (IsQuitRequested) {
            return;
        }

        QuitReceived();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Honoured, but only for a quit this application asked for. Once the operating system has
    ///     said the process is going away — <see cref="Stopping" /> — cancelling is not a thing
    ///     either platform offers, and pretending otherwise would leave the frame loop running
    ///     through a teardown.
    /// </remarks>
    public void CancelQuit() {
        if (State is not ApplicationState.Stopping) {
            IsQuitRequested = false;
        }
    }

    void QuitReceived() {
        IsQuitRequested = true;
        events.Post(PlatformEvent.Application(PlatformEventKind.Quit, Stopwatch.GetTimestamp()));
    }
}
