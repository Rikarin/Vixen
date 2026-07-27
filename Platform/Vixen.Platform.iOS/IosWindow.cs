// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using UIKit;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Ios;

/// <summary>The screen, behind <see cref="IWindow" />.</summary>
/// <remarks>
///     <para>
///         <b>Most of <see cref="IWindow" /> is meaningless here and says so rather than lying.</b>
///         There is one window, it is the size of the screen, it cannot be moved or resized by the
///         application, there is no cursor and no title bar. Position reads as
///         <see cref="Int2.Zero" />, <see cref="Mode" /> is always
///         <see cref="WindowMode.BorderlessFullscreen" />, and the setters that cannot do anything
///         do nothing — which is the contract <see cref="WindowOptions" /> already describes for a
///         platform lacking <see cref="PlatformCapabilities.WindowPositioning" />.
///     </para>
///     <para>
///         <b>What is real is the size, the scale and the surface</b>, and all three change: on
///         rotation, on a split-view resize, and when an external display is attached.
///         <see cref="IosMetalView" /> raises <see cref="PlatformEventKind.WindowResized" /> for
///         each, carrying the logical size and the pixel size separately — which is the distinction
///         doc 10 asks for and the one a swapchain needs.
///     </para>
///     <para>
///         <b>Closing is not an application's decision on iOS.</b> Disposing releases the UIKit
///         objects and marks this closed so the frame loop stops asking; the process itself goes
///         away when the system decides, which is what <see cref="MobileLifecycle" /> models.
///     </para>
/// </remarks>
public sealed class IosWindow : IWindow {
    readonly UIWindow window;
    readonly IosViewController controller;
    readonly IosSurface surface;

    bool closed;

    internal IosWindow(uint id, UIWindow window, IosViewController controller, PlatformEventBuffer events) {
        Id = id;
        this.window = window;
        this.controller = controller;

        surface = new(this);
        controller.MetalView.Attach(events, id);
    }

    /// <inheritdoc />
    public uint Id { get; }

    /// <summary>The view Vulkan presents to.</summary>
    internal IosMetalView View => controller.MetalView;

    /// <inheritdoc />
    /// <remarks>
    ///     Kept and returned, never shown: iOS has no title bar. Held rather than discarded so that
    ///     code which sets a title and reads it back is not surprised, and because it is what a
    ///     crash reporter labels the window with.
    /// </remarks>
    public string Title {
        get {
            ThrowIfClosed();
            return title;
        }
        set {
            ThrowIfClosed();
            title = value ?? string.Empty;
        }
    }

    string title = string.Empty;

    /// <inheritdoc />
    /// <remarks>Settable in name only. The system owns the size of the screen.</remarks>
    public Int2 ClientSize {
        get {
            ThrowIfClosed();
            var bounds = controller.MetalView.Bounds;
            return new((int)bounds.Width, (int)bounds.Height);
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
    public Int2 FramebufferSize {
        get {
            ThrowIfClosed();
            return controller.MetalView.DrawableSize;
        }
    }

    /// <inheritdoc />
    /// <remarks>Always the origin: there is nowhere else for a window to be.</remarks>
    public Int2 Position {
        get {
            ThrowIfClosed();
            return Int2.Zero;
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
    public float DpiScale {
        get {
            ThrowIfClosed();
            return (float)controller.MetalView.ContentScaleFactor;
        }
    }

    /// <inheritdoc />
    /// <remarks>Always borderless fullscreen, and writes are ignored.</remarks>
    public WindowMode Mode {
        get {
            ThrowIfClosed();
            return WindowMode.BorderlessFullscreen;
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     True, and not settable. The user resizes the window by rotating the device or by putting
    ///     the application in a split view, and an application cannot decline either.
    /// </remarks>
    public bool IsResizable {
        get {
            ThrowIfClosed();
            return true;
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
    public bool IsVisible {
        get {
            ThrowIfClosed();
            return !window.Hidden;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Key-window state, which on iOS means "the window receiving events". It is the closest
    ///     thing to focus that exists, and it is what the frame limiter's unfocused case needs.
    /// </remarks>
    public bool IsFocused {
        get {
            ThrowIfClosed();
            return window.IsKeyWindow;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Always false. An iOS application is not minimised; it is backgrounded, which
    ///     <see cref="ILifecycle" /> reports and which is a different thing — a minimised window is
    ///     still a window, a backgrounded application may not touch the GPU at all.
    /// </remarks>
    public bool IsMinimised {
        get {
            ThrowIfClosed();
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsClosed => closed;

    /// <inheritdoc />
    /// <remarks>Always zero: the main screen. External displays are not windows this platform makes.</remarks>
    public int DisplayIndex {
        get {
            ThrowIfClosed();
            return 0;
        }
    }

    /// <inheritdoc />
    /// <remarks>There is no cursor. Reads as <see cref="CursorMode.Normal" />; writes do nothing.</remarks>
    public CursorMode CursorMode {
        get {
            ThrowIfClosed();
            return CursorMode.Normal;
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
    /// <remarks>There is no cursor to give a shape.</remarks>
    public CursorShape CursorShape {
        get {
            ThrowIfClosed();
            return CursorShape.Arrow;
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
    public ISurface Surface {
        get {
            ThrowIfClosed();
            return surface;
        }
    }

    /// <inheritdoc />
    public void Show() {
        ThrowIfClosed();
        window.MakeKeyAndVisible();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Hides the window, which is legal and almost never what anyone wants: an iOS application
    ///     with no visible window is a black screen, not a backgrounded one.
    /// </remarks>
    public void Hide() {
        ThrowIfClosed();
        window.Hidden = true;
    }

    /// <inheritdoc />
    public void Focus() {
        ThrowIfClosed();
        window.MakeKeyWindow();
    }

    /// <inheritdoc />
    /// <remarks>Nothing to centre.</remarks>
    public void Centre() => ThrowIfClosed();

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing. Demanding attention is what a local notification is for, and posting one from a
    ///     window API would be a surprising thing for this call to do.
    /// </remarks>
    public void RequestAttention() => ThrowIfClosed();

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing. An iOS icon comes from the application bundle and cannot be set at run time.
    ///     Validating the arguments anyway, so code that is wrong here is wrong everywhere rather
    ///     than only on the platforms that look.
    /// </remarks>
    public void SetIcon(ReadOnlySpan<byte> pixels, Int2 size) {
        ThrowIfClosed();

        var expected = (long)size.X * size.Y * 4;

        if (size.X <= 0 || size.Y <= 0 || pixels.Length != expected) {
            throw new ArgumentException(
                $"An icon of {size.X}×{size.Y} needs {expected} bytes of RGBA8; got {pixels.Length}.",
                nameof(pixels)
            );
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (closed) {
            return;
        }

        closed = true;
        window.Hidden = true;
        controller.Dispose();
        window.Dispose();
    }

    void ThrowIfClosed() => ObjectDisposedException.ThrowIf(closed, this);
}
