// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>One window, or one thing shaped like a window.</summary>
/// <remarks>
///     <para>
///         "Shaped like a window" is not a hedge. A headless platform creates these too: they have a
///         size, an id, a lifecycle and an event stream, and their <see cref="Surface" /> reports
///         <see cref="SurfaceKind.None" />. That is what lets the dedicated server run the same
///         frame loop as the desktop build rather than a second one written for it, and it is why
///         the headless path is exercised on every test run instead of being discovered to have
///         rotted in Phase 9.
///     </para>
///     <para>
///         Properties report the platform's answer, not the last thing that was asked for. Setting
///         <see cref="ClientSize" /> is a request: a tiling window manager may ignore it, a mobile
///         platform certainly will, and reading it back afterwards may return something else. Code
///         that needs to know what happened listens for
///         <see cref="PlatformEventKind.WindowResized" />.
///     </para>
///     <para>
///         Disposing destroys the window. Closing it from the title bar does not — that arrives as
///         <see cref="PlatformEventKind.WindowCloseRequested" /> and the window stays open until the
///         application agrees, which is what makes "save before quitting?" possible.
///     </para>
/// </remarks>
public interface IWindow : IDisposable {
    /// <summary>This window's id, as it appears in <see cref="PlatformEvent.WindowId" />.</summary>
    /// <remarks>Never <c>0</c>, which is reserved for events that belong to no window, and never
    /// reused within a session.</remarks>
    uint Id { get; }

    /// <summary>The title bar text.</summary>
    string Title { get; set; }

    /// <summary>The client area's size in logical points.</summary>
    Int2 ClientSize { get; set; }

    /// <summary>The framebuffer's size in physical pixels — <see cref="ClientSize" /> times
    /// <see cref="DpiScale" />, subject to the platform's rounding.</summary>
    /// <remarks>Read it rather than computing it: the rounding is the platform's, and being one
    /// pixel out is a swapchain that fails validation.</remarks>
    Int2 FramebufferSize { get; }

    /// <summary>The top-left corner in desktop coordinates.</summary>
    /// <remarks>
    ///     Reads as <see cref="Int2.Zero" /> and ignores writes where
    ///     <see cref="PlatformCapabilities.WindowPositioning" /> is absent.
    /// </remarks>
    Int2 Position { get; set; }

    /// <summary>How many physical pixels there are per logical point.</summary>
    float DpiScale { get; }

    /// <summary>How the window relates to its display.</summary>
    WindowMode Mode { get; set; }

    /// <summary>Whether the user may resize it.</summary>
    bool IsResizable { get; set; }

    /// <summary>Whether it is currently on screen.</summary>
    bool IsVisible { get; }

    /// <summary>Whether it has keyboard focus.</summary>
    bool IsFocused { get; }

    /// <summary>Whether it is minimised, and therefore not worth rendering to.</summary>
    bool IsMinimised { get; }

    /// <summary>Whether it has been disposed. Every other member throws once this is true.</summary>
    bool IsClosed { get; }

    /// <summary>Which display it is mostly on.</summary>
    int DisplayIndex { get; }

    /// <summary>What the cursor does over it.</summary>
    CursorMode CursorMode { get; set; }

    /// <summary>Which stock cursor to draw over it.</summary>
    CursorShape CursorShape { get; set; }

    /// <summary>The thing a graphics backend presents to.</summary>
    ISurface Surface { get; }

    /// <summary>Shows the window.</summary>
    void Show();

    /// <summary>Hides the window without destroying it.</summary>
    void Hide();

    /// <summary>Asks for keyboard focus.</summary>
    /// <remarks>
    ///     A request. Every desktop platform has focus-stealing prevention, so an application that
    ///     is not already in the foreground will usually get <see cref="RequestAttention" />'s
    ///     behaviour instead, which is the correct outcome.
    /// </remarks>
    void Focus();

    /// <summary>Centres the window on its display.</summary>
    void Centre();

    /// <summary>Asks the OS to draw attention to the window — a bouncing dock icon, a flashing
    /// taskbar button.</summary>
    void RequestAttention();

    /// <summary>Sets the window and taskbar icon from straight (non-premultiplied) RGBA8 pixels.</summary>
    /// <param name="pixels">
    ///     <c>Size.X * Size.Y * 4</c> bytes, row-major from the top-left.
    /// </param>
    /// <param name="size">The image's dimensions in pixels.</param>
    /// <remarks>
    ///     Raw pixels rather than a file, because the platform layer decoding PNGs would mean a
    ///     runtime assembly carrying an image codec — which ADR-015 keeps out of shipping builds for
    ///     exactly this class of convenience.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="pixels" /> is not
    /// <paramref name="size" />'s worth of RGBA8.</exception>
    void SetIcon(ReadOnlySpan<byte> pixels, Int2 size);
}
