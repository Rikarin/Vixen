// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>How a window relates to the display it is on.</summary>
public enum WindowMode : byte {
    /// <summary>An ordinary window with decorations, sized and placed by the user.</summary>
    Windowed = 0,

    /// <summary>Windowed, but filling the display's work area.</summary>
    Maximised = 1,

    /// <summary>
    ///     Undecorated and covering the display at the display's current mode — what almost every
    ///     modern game means by "fullscreen".
    /// </summary>
    /// <remarks>
    ///     Alt-tabs instantly, composites with notifications and overlays, and never leaves the
    ///     desktop at the wrong resolution if the process crashes. The compositor may still hand it
    ///     a direct scanout path, so the performance argument for the exclusive mode is much weaker
    ///     than it was.
    /// </remarks>
    BorderlessFullscreen = 2,

    /// <summary>Fullscreen with the display switched to a mode of the application's choosing.</summary>
    /// <remarks>
    ///     Offered because it is the only way to render at a resolution the display can scan out
    ///     itself, and because HDR handoff still wants it on some drivers. Platforms that do not
    ///     have it — Wayland, mobile, the browser — silently give
    ///     <see cref="BorderlessFullscreen" /> instead, so nothing that asks for it fails.
    /// </remarks>
    ExclusiveFullscreen = 3
}

/// <summary>What the cursor does over a window.</summary>
public enum CursorMode : byte {
    /// <summary>Visible, and free to leave the window.</summary>
    Normal = 0,

    /// <summary>Hidden while over the window, but still an ordinary cursor at a real position.</summary>
    Hidden = 1,

    /// <summary>Visible and confined to the window's client area.</summary>
    Confined = 2,

    /// <summary>
    ///     Hidden, warped to the centre and reporting motion only — the first-person camera mode.
    /// </summary>
    /// <remarks>
    ///     <see cref="PlatformEvent.Position" /> stops meaning anything here and
    ///     <see cref="PlatformEvent.Delta" /> becomes the raw device motion, unaccelerated where the
    ///     platform can give us that. Differencing positions in this mode reads the warping rather
    ///     than the mouse.
    /// </remarks>
    Relative = 3
}

/// <summary>Which stock cursor to draw.</summary>
/// <remarks>
///     The OS's own cursors, not bitmaps of ours. A resize cursor that does not match the rest of
///     the desktop is the kind of detail users notice without being able to say why, and the theme,
///     size and accessibility settings are the platform's to honour.
/// </remarks>
public enum CursorShape : byte {
    /// <summary>The ordinary pointer.</summary>
    Arrow = 0,

    /// <summary>The I-beam, over editable text.</summary>
    TextBeam = 1,

    /// <summary>The busy indicator.</summary>
    Wait = 2,

    /// <summary>The precision crosshair, over a viewport or a colour picker.</summary>
    Crosshair = 3,

    /// <summary>The pointing hand, over something clickable.</summary>
    Hand = 4,

    /// <summary>Resize left and right, over a vertical splitter or edge.</summary>
    ResizeHorizontal = 5,

    /// <summary>Resize up and down, over a horizontal splitter or edge.</summary>
    ResizeVertical = 6,

    /// <summary>Resize along the bottom-left to top-right diagonal.</summary>
    ResizeDiagonalUp = 7,

    /// <summary>Resize along the top-left to bottom-right diagonal.</summary>
    ResizeDiagonalDown = 8,

    /// <summary>Move, or resize in every direction.</summary>
    ResizeAll = 9,

    /// <summary>The drop target refuses what is being dragged.</summary>
    NotAllowed = 10
}

/// <summary>What to create a window as.</summary>
/// <remarks>
///     A record struct with defaults that give a sensible 1280×720 resizable window, so
///     <c>new WindowOptions { Title = "Game" }</c> is enough to get started and every other field is
///     an opt-in.
/// </remarks>
public readonly record struct WindowOptions() {
    /// <summary>The title bar text.</summary>
    public string Title { get; init; } = "Vixen";

    /// <summary>The client area's size in logical points.</summary>
    /// <remarks>
    ///     Points, not pixels: asking for 1280×720 on a 2× display gives a window that looks the
    ///     same size as it does everywhere else and a framebuffer of 2560×1440. A window sized in
    ///     pixels is a window that is half the intended size on a laptop.
    /// </remarks>
    public Int2 Size { get; init; } = new(1280, 720);

    /// <summary>Where to put the top-left corner, or <see langword="null" /> to let the platform
    /// decide.</summary>
    /// <remarks>
    ///     Ignored where <see cref="PlatformCapabilities.WindowPositioning" /> is absent, which
    ///     includes Wayland — so restoring a saved window position has to tolerate not happening.
    /// </remarks>
    public Int2? Position { get; init; }

    /// <summary>The smallest the user may make it, or <see langword="null" /> for no limit.</summary>
    public Int2? MinimumSize { get; init; }

    /// <summary>The largest the user may make it, or <see langword="null" /> for no limit.</summary>
    public Int2? MaximumSize { get; init; }

    /// <summary>How it relates to its display.</summary>
    public WindowMode Mode { get; init; } = WindowMode.Windowed;

    /// <summary>Whether the user can resize it.</summary>
    public bool IsResizable { get; init; } = true;

    /// <summary>Whether it has a title bar and border.</summary>
    public bool IsDecorated { get; init; } = true;

    /// <summary>Whether to show it immediately.</summary>
    /// <remarks>
    ///     Creating hidden and showing after the first frame is drawn is how a window avoids
    ///     flashing an undrawn background, so this defaults to hidden and the application decides
    ///     when it is ready to be seen.
    /// </remarks>
    public bool IsVisible { get; init; }

    /// <summary>Whether it should stay above other windows.</summary>
    public bool IsAlwaysOnTop { get; init; }

    /// <summary>Which display to place it on, or <see langword="null" /> for the primary one.</summary>
    public int? DisplayIndex { get; init; }

    /// <summary>Whether files and text dropped on it should be reported.</summary>
    public bool AcceptsDrops { get; init; }
}
