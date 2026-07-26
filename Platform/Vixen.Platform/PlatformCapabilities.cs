// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Platform;

/// <summary>What a platform can actually do, asked at runtime rather than assumed from a
/// <c>#if</c>.</summary>
/// <remarks>
///     <para>
///         Every capability here is absent on at least one supported target: a dedicated server has
///         no display, a browser has no native file dialogs, a phone has no free-floating windows,
///         and a Linux box under Wayland cannot position a window at all. Code that needs one asks;
///         code that does not, does not care.
///     </para>
///     <para>
///         The rule from <c>docs/plan/10 § Cross-platform discipline</c> is that feature detection
///         is always a runtime query with a fallback and never a compile-time branch. This enum is
///         where that rule is cashed in.
///     </para>
/// </remarks>
[Flags]
public enum PlatformCapabilities {
    /// <summary>Nothing — a platform that is only a clock and a file system.</summary>
    None = 0,

    /// <summary>Windows can be created and will be shown to somebody.</summary>
    /// <remarks>
    ///     A headless platform still creates window objects — they carry a size, a lifecycle and an
    ///     event stream, so the frame loop is the same one a desktop runs — but nothing is
    ///     displayed and <see cref="SurfaceKind.None" /> is what the swapchain gets told.
    /// </remarks>
    Windowing = 1 << 0,

    /// <summary>More than one window may exist at a time.</summary>
    /// <remarks>
    ///     True on the three desktops and headless, false on mobile and in a browser tab. Separate
    ///     from <see cref="Windowing" /> because the two questions are genuinely independent: a
    ///     headless head shows nobody anything and still has no reason to limit an editor under test
    ///     to one document window.
    /// </remarks>
    MultiWindow = 1 << 1,

    /// <summary>Window position can be read and set in desktop coordinates.</summary>
    /// <remarks>
    ///     False under Wayland, which deliberately does not let a client know or choose where it
    ///     is. An editor that saves window placement has to tolerate that rather than assume it.
    /// </remarks>
    WindowPositioning = 1 << 2,

    /// <summary>Displays can be enumerated, with bounds, refresh rates and scale factors.</summary>
    DisplayEnumeration = 1 << 3,

    /// <summary>The mouse cursor can be hidden, confined, or put into relative mode.</summary>
    Cursor = 1 << 4,

    /// <summary>The system clipboard is readable and writable.</summary>
    Clipboard = 1 << 5,

    /// <summary>Native open/save/folder pickers and message boxes.</summary>
    NativeDialogs = 1 << 6,

    /// <summary>Composed text input with an IME, and an on-screen keyboard where there is one.</summary>
    TextInput = 1 << 7,

    /// <summary>Gamepads, joysticks and other hot-pluggable input devices.</summary>
    GameControllers = 1 << 8,

    /// <summary>At least one input device can produce force feedback.</summary>
    Haptics = 1 << 9,

    /// <summary>Battery level, charging state and thermal pressure are reported.</summary>
    PowerInfo = 1 << 10,

    /// <summary>The process can be suspended and resumed by the OS.</summary>
    /// <remarks>
    ///     True on mobile and in a browser tab, false on the desktop. Its absence is why desktop
    ///     code gets away with ignoring <see cref="ILifecycle" /> and mobile code does not.
    /// </remarks>
    Suspension = 1 << 11,

    /// <summary>Files and text can be dropped onto a window.</summary>
    DragAndDrop = 1 << 12
}
