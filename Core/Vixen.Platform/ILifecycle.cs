// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>Where the application is in the OS's idea of its life.</summary>
public enum ApplicationState : byte {
    /// <summary>Not started yet.</summary>
    Starting = 0,

    /// <summary>Running and in the foreground. The only state in which rendering is worth
    /// doing.</summary>
    Running = 1,

    /// <summary>Running but not in the foreground: another window is on top, or the browser tab is
    /// hidden.</summary>
    /// <remarks>
    ///     Still ticking, and on the desktop that is often what the user wants — a level keeps
    ///     loading while they read something else. Frame rate should drop; the simulation need not
    ///     stop.
    /// </remarks>
    Background = 2,

    /// <summary>Suspended by the OS. No frames, no GPU work, and on Android no valid graphics
    /// surface.</summary>
    Suspended = 3,

    /// <summary>Shutting down.</summary>
    Stopping = 4
}

/// <summary>How much memory pressure the OS is reporting.</summary>
public enum MemoryPressure : byte {
    /// <summary>Nothing to worry about.</summary>
    Normal = 0,

    /// <summary>Give back what is easy to reconstruct: caches, decoded textures, pooled
    /// buffers.</summary>
    Warning = 1,

    /// <summary>Give back everything that is not needed for the current frame. The next step the OS
    /// takes is killing the process.</summary>
    Critical = 2
}

/// <summary>The process's lifecycle, and the platform's ability to interrupt it.</summary>
/// <remarks>
///     <para>
///         Transitions are reported as ordinary events —
///         <see cref="PlatformEventKind.Suspending" />, <see cref="PlatformEventKind.Resumed" />,
///         <see cref="PlatformEventKind.LowMemory" />, <see cref="PlatformEventKind.Quit" /> — so
///         they arrive in the same stream and the same order as everything else. This interface is
///         the state that stream describes, plus the two things a frame loop needs to *do* rather
///         than observe.
///     </para>
///     <para>
///         On the desktop most of this never fires, which is precisely why it must be implemented
///         and tested from the start: <c>docs/plan/10 § Android</c> puts lifecycle at the top of the
///         list of bug sources, and a suspend path first exercised in Phase 3 on a physical device
///         is a suspend path that has never worked.
///     </para>
/// </remarks>
public interface ILifecycle {
    /// <summary>Where the application is now.</summary>
    ApplicationState State { get; }

    /// <summary>How much memory pressure the OS last reported.</summary>
    /// <remarks>
    ///     Latched: it stays at the last reported level until the platform says otherwise, so a
    ///     subsystem that reacts on the next frame rather than inside the callback still sees it.
    /// </remarks>
    MemoryPressure MemoryPressure { get; }

    /// <summary>Whether a quit has been requested and not yet cancelled.</summary>
    bool IsQuitRequested { get; }

    /// <summary>Asks the application to quit.</summary>
    /// <remarks>
    ///     Raises <see cref="PlatformEventKind.Quit" /> and sets
    ///     <see cref="IsQuitRequested" />. It does not terminate anything: the frame loop decides
    ///     what to do, which is what lets an unsaved-changes prompt exist.
    /// </remarks>
    void RequestQuit();

    /// <summary>Withdraws a quit request.</summary>
    /// <remarks>
    ///     Ignored once the platform has committed — an OS-initiated shutdown or an Android
    ///     <c>onDestroy</c> is not negotiable, and pretending otherwise would let an application
    ///     believe it had more time than it has.
    /// </remarks>
    void CancelQuit();
}
