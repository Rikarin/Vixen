// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>What a <see cref="PlatformEvent" /> is, and therefore which of its payloads is real.</summary>
public enum PlatformEventKind : byte {
    /// <summary>The default value, which is never enqueued. A <see cref="PlatformEvent" /> that
    /// reads as this one was not initialised.</summary>
    None = 0,

    // ── Window ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The window became visible.</summary>
    WindowShown = 1,

    /// <summary>The window stopped being visible.</summary>
    WindowHidden = 2,

    /// <summary>The window moved. <see cref="PlatformEvent.Position" /> is its new top-left in
    /// desktop coordinates.</summary>
    WindowMoved = 3,

    /// <summary>The window's client area changed size. <see cref="PlatformEvent.Size" /> is the new
    /// size in logical points; <see cref="PlatformEvent.PixelSize" /> is the new framebuffer size.</summary>
    /// <remarks>
    ///     A swapchain is rebuilt from <see cref="PlatformEvent.PixelSize" />. The two differ by the
    ///     window's scale factor, and treating either as the other is how a HiDPI window renders at
    ///     a quarter size or scaled by four.
    /// </remarks>
    WindowResized = 4,

    /// <summary>The window gained keyboard focus.</summary>
    WindowFocusGained = 5,

    /// <summary>The window lost keyboard focus.</summary>
    /// <remarks>
    ///     Held keys are not released by the platform when this happens, so an input layer that
    ///     tracks key state has to clear it here or the player returns to a window that believes
    ///     <c>W</c> is still down.
    /// </remarks>
    WindowFocusLost = 6,

    /// <summary>The window was minimised. Rendering to it is wasted work until it is restored.</summary>
    WindowMinimised = 7,

    /// <summary>The window was maximised.</summary>
    WindowMaximised = 8,

    /// <summary>The window returned to its normal state from minimised or maximised.</summary>
    WindowRestored = 9,

    /// <summary>Somebody asked for the window to close — the title-bar button, <c>Alt+F4</c>, the
    /// dock.</summary>
    /// <remarks>
    ///     A request, not a fact. The window is still open and stays open until it is disposed,
    ///     which is what lets an application ask whether to save first.
    /// </remarks>
    WindowCloseRequested = 10,

    /// <summary>The window's scale factor changed — it was dragged to a display with a different
    /// DPI, or the display's scale was changed under it.</summary>
    WindowDpiChanged = 11,

    /// <summary>The pointer entered the window's client area.</summary>
    WindowMouseEntered = 12,

    /// <summary>The pointer left the window's client area.</summary>
    WindowMouseLeft = 13,

    // ── Keyboard and text ───────────────────────────────────────────────────────────────────

    /// <summary>A key went down. <see cref="PlatformEvent.IsRepeat" /> distinguishes auto-repeat.</summary>
    KeyDown = 20,

    /// <summary>A key came up.</summary>
    KeyUp = 21,

    /// <summary>Committed text. <see cref="PlatformEvent.Text" /> is what the user actually typed,
    /// after the layout, dead keys and any IME have had their say.</summary>
    /// <remarks>
    ///     One event may carry several characters, and a single keystroke may produce none. Text
    ///     only arrives while <see cref="ITextInput" /> is active.
    /// </remarks>
    TextInput = 22,

    /// <summary>In-progress IME composition. <see cref="PlatformEvent.Text" /> is the pre-edit
    /// string, <see cref="PlatformEvent.SelectionStart" /> and
    /// <see cref="PlatformEvent.SelectionLength" /> the composition cursor within it.</summary>
    /// <remarks>
    ///     Shown underlined and replaced in place; it is not committed until a
    ///     <see cref="TextInput" /> arrives, and it may be abandoned entirely.
    /// </remarks>
    TextEditing = 23,

    // ── Pointer ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The pointer moved. <see cref="PlatformEvent.Position" /> is window-relative;
    /// <see cref="PlatformEvent.Delta" /> is the movement since the last event.</summary>
    /// <remarks>
    ///     In relative cursor mode <see cref="PlatformEvent.Delta" /> is the raw motion and
    ///     <see cref="PlatformEvent.Position" /> stops being meaningful — that is the entire point
    ///     of the mode, and a first-person camera must read the delta rather than differencing
    ///     positions.
    /// </remarks>
    MouseMoved = 30,

    /// <summary>A mouse button went down. <see cref="PlatformEvent.ClickCount" /> is 2 for the
    /// second click of a double-click, as the OS defines one.</summary>
    MouseButtonDown = 31,

    /// <summary>A mouse button came up.</summary>
    MouseButtonUp = 32,

    /// <summary>The wheel turned, or a trackpad scrolled.
    /// <see cref="PlatformEvent.Delta" /> is in wheel notches, positive up and right.</summary>
    /// <remarks>
    ///     Trackpads and high-resolution wheels report fractions of a notch, so the value is a
    ///     float and rounding it to an integer loses smooth scrolling on every modern laptop.
    /// </remarks>
    MouseWheel = 33,

    // ── Touch ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A finger touched down. <see cref="PlatformEvent.DeviceId" /> identifies the finger
    /// for as long as it stays down.</summary>
    TouchDown = 40,

    /// <summary>A finger moved.</summary>
    TouchMoved = 41,

    /// <summary>A finger lifted.</summary>
    TouchUp = 42,

    // ── Controllers ─────────────────────────────────────────────────────────────────────────

    /// <summary>A gamepad was plugged in. <see cref="PlatformEvent.DeviceId" /> is its id.</summary>
    GamepadConnected = 50,

    /// <summary>A gamepad was unplugged. Its id is not reused for a different device.</summary>
    GamepadDisconnected = 51,

    /// <summary>A gamepad button went down.</summary>
    GamepadButtonDown = 52,

    /// <summary>A gamepad button came up.</summary>
    GamepadButtonUp = 53,

    /// <summary>A gamepad axis moved. <see cref="PlatformEvent.Value" /> is the new position.</summary>
    GamepadAxisMoved = 54,

    // ── Display ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A display was added, removed, or had its mode or scale changed. Anything cached
    /// from <see cref="IDisplayInfo" /> is stale.</summary>
    DisplaysChanged = 60,

    /// <summary>The user changed the system's light/dark appearance.
    /// <see cref="IPlatform.ColorScheme" /> already reports the new one.</summary>
    /// <remarks>
    ///     ⚠ <b>Not queued at start-up.</b> The first appearance is a fact a host reads out of
    ///     <see cref="IPlatform.ColorScheme" /> before its first frame, not an event it waits for —
    ///     a host that only handled the event would render its first frame, and on a machine whose
    ///     appearance never changes every frame after it, against the wrong palette.
    /// </remarks>
    SystemColorSchemeChanged = 61,

    // ── Lifecycle ───────────────────────────────────────────────────────────────────────────

    /// <summary>The process is about to be suspended. Save now; there may be no later.</summary>
    /// <remarks>
    ///     On mobile this is delivered with a short, unenforced grace period, and on the web with a
    ///     tab switch that may never come back. Work started here should be work that finishes.
    /// </remarks>
    Suspending = 70,

    /// <summary>The process resumed. On Android the graphics surface has been destroyed and
    /// recreated, so the swapchain is invalid whatever it claims.</summary>
    Resumed = 71,

    /// <summary>The OS is under memory pressure and would like some back.</summary>
    /// <remarks>Ignoring it on iOS means being killed rather than being asked twice.</remarks>
    LowMemory = 72,

    /// <summary>The application was asked to quit — the last window closed, a signal arrived, the
    /// dock's quit item was used.</summary>
    Quit = 73,

    // ── Drag and drop ───────────────────────────────────────────────────────────────────────

    /// <summary>A file was dropped on a window. <see cref="PlatformEvent.Text" /> is its native
    /// path, not a virtual one — it comes from outside anything the engine has mounted.</summary>
    DropFile = 80,

    /// <summary>Text was dropped on a window.</summary>
    DropText = 81
}
