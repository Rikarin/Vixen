// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Tests;

/// <summary>
///     The lifecycle state machine both mobile platforms drive. Doc 10 calls lifecycle the biggest
///     source of bugs on Android, and the reason is that the interesting transitions are the ones
///     nobody exercises by hand — so they are exercised here.
/// </summary>
public sealed class MobileLifecycleTests {
    readonly PlatformEventBuffer events = new();
    readonly MobileLifecycle lifecycle;

    public MobileLifecycleTests() {
        lifecycle = new(events);
    }

    [Fact]
    public void ItStartsBeforeItIsRunning() {
        Assert.Equal(ApplicationState.Starting, lifecycle.State);
        Assert.Equal(MemoryPressure.Normal, lifecycle.MemoryPressure);
        Assert.False(lifecycle.IsQuitRequested);
    }

    /// <summary>
    ///     A launch is not a resume, and the difference is load-bearing: a renderer that rebuilds
    ///     its swapchain on Resumed would otherwise rebuild one that was never lost, on the first
    ///     frame of every run.
    /// </summary>
    [Fact]
    public void ComingUpForTheFirstTimeRaisesNothing() {
        lifecycle.EnterForeground();

        Assert.Equal(ApplicationState.Running, lifecycle.State);
        Assert.Empty(Kinds());
    }

    [Fact]
    public void GoingToTheBackgroundAndComingBackRaisesSuspendingThenResumed() {
        lifecycle.EnterForeground();
        Drain();

        lifecycle.EnterBackground();
        lifecycle.Suspend();
        lifecycle.EnterForeground();

        Assert.Equal([PlatformEventKind.Suspending, PlatformEventKind.Resumed], Kinds());
        Assert.Equal(ApplicationState.Running, lifecycle.State);
    }

    /// <summary>
    ///     Losing focus is not losing the surface. iOS raises willResignActive for a notification
    ///     shade and never follows it with didEnterBackground; treating that as suspension would
    ///     tear down the swapchain because somebody swiped down from the top.
    /// </summary>
    [Fact]
    public void BackgroundOnItsOwnIsNotSuspension() {
        lifecycle.EnterForeground();
        Drain();

        lifecycle.EnterBackground();

        Assert.Equal(ApplicationState.Background, lifecycle.State);
        Assert.Empty(Kinds());
    }

    /// <summary>
    ///     Both platforms repeat their callbacks. Suspending twice must not raise twice, or a
    ///     subsystem that releases GPU resources on the way down releases them again.
    /// </summary>
    [Fact]
    public void RepeatedTransitionsRaiseOnce() {
        lifecycle.EnterForeground();
        lifecycle.EnterForeground();
        Drain();

        lifecycle.Suspend();
        lifecycle.Suspend();

        Assert.Equal([PlatformEventKind.Suspending], Kinds());
    }

    /// <summary>
    ///     A second warning at the same level means the first was not enough. Reporting only on a
    ///     change is how an application gets killed while holding a cache it was told twice to drop.
    /// </summary>
    [Fact]
    public void EveryMemoryWarningIsReportedEvenAtAnUnchangedLevel() {
        lifecycle.ReportMemoryPressure(MemoryPressure.Warning);
        lifecycle.ReportMemoryPressure(MemoryPressure.Warning);

        Assert.Equal([PlatformEventKind.LowMemory, PlatformEventKind.LowMemory], Kinds());
        Assert.Equal(MemoryPressure.Warning, lifecycle.MemoryPressure);
    }

    [Fact]
    public void RecoveringFromMemoryPressureRaisesNothing() {
        lifecycle.ReportMemoryPressure(MemoryPressure.Critical);
        Drain();

        lifecycle.ReportMemoryPressure(MemoryPressure.Normal);

        Assert.Empty(Kinds());
        Assert.Equal(MemoryPressure.Normal, lifecycle.MemoryPressure);
    }

    /// <summary>
    ///     There is no supported way for an iOS application to terminate itself. Quitting therefore
    ///     stops the frame loop and leaves the process to the operating system.
    /// </summary>
    [Fact]
    public void RequestingQuitRaisesQuitAndIsIdempotent() {
        lifecycle.RequestQuit();
        lifecycle.RequestQuit();

        Assert.True(lifecycle.IsQuitRequested);
        Assert.Equal([PlatformEventKind.Quit], Kinds());
    }

    [Fact]
    public void AQuitTheApplicationAskedForCanBeCancelled() {
        lifecycle.EnterForeground();
        lifecycle.RequestQuit();

        lifecycle.CancelQuit();

        Assert.False(lifecycle.IsQuitRequested);
    }

    /// <summary>
    ///     But one the operating system announced cannot be. Neither platform offers a way to
    ///     decline termination, and pretending otherwise leaves the frame loop running through a
    ///     teardown.
    /// </summary>
    [Fact]
    public void ATerminationTheSystemAnnouncedCannotBeCancelled() {
        lifecycle.EnterForeground();
        lifecycle.Stopping();

        lifecycle.CancelQuit();

        Assert.True(lifecycle.IsQuitRequested);
        Assert.Equal(ApplicationState.Stopping, lifecycle.State);
    }

    /// <summary>Stopping is terminal: nothing moves after it.</summary>
    [Fact]
    public void NothingLeavesStopping() {
        lifecycle.Stopping();
        Drain();

        lifecycle.EnterForeground();
        lifecycle.EnterBackground();
        lifecycle.Suspend();

        Assert.Equal(ApplicationState.Stopping, lifecycle.State);
        Assert.Empty(Kinds());
    }

    PlatformEventKind[] Kinds() {
        var drained = events.Drain();
        var kinds = new PlatformEventKind[drained.Length];

        for (var index = 0; index < drained.Length; index++) {
            kinds[index] = drained[index].Kind;
        }

        return kinds;
    }

    void Drain() => events.Drain();
}
