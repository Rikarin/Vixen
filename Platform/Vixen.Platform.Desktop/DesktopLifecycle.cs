// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Platform.Desktop;

/// <summary>The process's lifecycle on a desktop, where most of it never happens.</summary>
/// <remarks>
///     No desktop suspends a process, so <see cref="ApplicationState.Suspended" /> is unreachable
///     here and <see cref="PlatformCapabilities.Suspension" /> is absent.
///     <see cref="ApplicationState.Background" /> is reachable and is tracked from window focus,
///     because it is worth knowing: an application with no focused window can drop its frame rate
///     without anybody noticing, and on a laptop that is the difference between a warm palm rest and
///     a cool one.
/// </remarks>
public sealed class DesktopLifecycle(PlatformEventBuffer events) : ILifecycle {
    /// <inheritdoc />
    public ApplicationState State { get; private set; } = ApplicationState.Running;

    /// <summary>Always <see cref="Platform.MemoryPressure.Normal" />: no desktop reports memory
    /// pressure to a process.</summary>
    /// <remarks>
    ///     Not because memory pressure does not exist on a desktop, but because the OS deals with it
    ///     by swapping rather than by asking. A subsystem that responds to this is exercised by
    ///     <c>Vixen.Platform.Headless</c> and on mobile, which is the point of the headless one
    ///     being driveable.
    /// </remarks>
    public MemoryPressure MemoryPressure => MemoryPressure.Normal;

    /// <inheritdoc />
    public bool IsQuitRequested { get; private set; }

    /// <inheritdoc />
    public void RequestQuit() {
        if (IsQuitRequested) {
            return;
        }

        IsQuitRequested = true;
        events.Post(PlatformEvent.Application(PlatformEventKind.Quit, Stopwatch.GetTimestamp()));
    }

    /// <inheritdoc />
    public void CancelQuit() => IsQuitRequested = false;

    /// <summary>Records a quit the OS delivered, without raising a second event for it.</summary>
    /// <param name="timestamp">When the platform said it happened.</param>
    internal void QuitReceived(long timestamp) {
        IsQuitRequested = true;
        events.Post(PlatformEvent.Application(PlatformEventKind.Quit, timestamp));
    }

    internal void SetForeground(bool foreground) {
        if (State is ApplicationState.Stopping) {
            return;
        }

        State = foreground ? ApplicationState.Running : ApplicationState.Background;
    }

    internal void Stopping() => State = ApplicationState.Stopping;
}
