// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.SDL;
using Vixen.Core.Mathematics;
using SdlWindow = Silk.NET.SDL.Window;

namespace Vixen.Platform.Desktop;

/// <summary>An SDL window.</summary>
/// <remarks>
///     <para>
///         Properties read SDL rather than a cached copy, because a window manager is entitled to
///         ignore what it was asked for: a tiling compositor decides the size, Wayland decides the
///         position and will not say what it chose, and a fullscreen transition changes both. Code
///         that must know what happened listens for <see cref="PlatformEventKind.WindowResized" />.
///     </para>
///     <para>
///         Disposing destroys the window; the title-bar close button does not, and arrives as
///         <see cref="PlatformEventKind.WindowCloseRequested" /> instead.
///     </para>
/// </remarks>
public sealed unsafe class DesktopWindow : IWindow {
    readonly Sdl sdl;
    readonly DesktopSurface surface;

    SdlWindow* handle;
    Cursor* cursor;
    CursorShape cursorShape;
    CursorMode cursorMode;

    internal DesktopWindow(Sdl sdl, SdlWindow* handle) {
        this.sdl = sdl;
        this.handle = handle;
        Id = sdl.GetWindowID(handle);
        surface = new(sdl, this);
    }

    /// <inheritdoc />
    public uint Id { get; }

    /// <inheritdoc />
    public string Title {
        get {
            ThrowIfClosed();
            return sdl.GetWindowTitleS(handle) ?? string.Empty;
        }
        set {
            ThrowIfClosed();
            sdl.SetWindowTitle(handle, value ?? string.Empty);
        }
    }

    /// <inheritdoc />
    public Int2 ClientSize {
        get {
            ThrowIfClosed();
            int width, height;
            sdl.GetWindowSize(handle, &width, &height);
            return new(width, height);
        }
        set {
            ThrowIfClosed();
            sdl.SetWindowSize(handle, value.X, value.Y);
        }
    }

    /// <inheritdoc />
    public Int2 FramebufferSize {
        get {
            ThrowIfClosed();
            int width, height;
            sdl.GetWindowSizeInPixels(handle, &width, &height);
            return new(width, height);
        }
    }

    /// <inheritdoc />
    public Int2 Position {
        get {
            ThrowIfClosed();
            int x, y;
            sdl.GetWindowPosition(handle, &x, &y);
            return new(x, y);
        }
        set {
            ThrowIfClosed();
            sdl.SetWindowPosition(handle, value.X, value.Y);
        }
    }

    /// <summary>
    ///     Physical pixels per logical point, derived from the two sizes SDL reports rather than
    ///     from its DPI query.
    /// </summary>
    /// <remarks>
    ///     <c>SDL_GetDisplayDPI</c> returns the display's advertised dots per inch, which is not the
    ///     same number: a 4K laptop panel reports something near 220 while the window is scaled by
    ///     2, and on Linux it frequently returns whatever EDID claimed, which is often nonsense. The
    ///     ratio of the framebuffer to the client area is the scale that actually applies to this
    ///     window on the display it is on.
    /// </remarks>
    public float DpiScale {
        get {
            var logical = ClientSize;
            return logical.X <= 0 ? 1f : FramebufferSize.X / (float)logical.X;
        }
    }

    /// <inheritdoc />
    public WindowMode Mode {
        get {
            ThrowIfClosed();
            var flags = (WindowFlags)sdl.GetWindowFlags(handle);

            if (flags.HasFlag(WindowFlags.Fullscreen)) {
                return WindowMode.ExclusiveFullscreen;
            }

            if (flags.HasFlag(WindowFlags.FullscreenDesktop)) {
                return WindowMode.BorderlessFullscreen;
            }

            return flags.HasFlag(WindowFlags.Maximized) ? WindowMode.Maximised : WindowMode.Windowed;
        }
        set {
            ThrowIfClosed();

            switch (value) {
                case WindowMode.ExclusiveFullscreen:
                    sdl.SetWindowFullscreen(handle, (uint)WindowFlags.Fullscreen);
                    break;
                case WindowMode.BorderlessFullscreen:
                    sdl.SetWindowFullscreen(handle, (uint)WindowFlags.FullscreenDesktop);
                    break;
                case WindowMode.Maximised:
                    sdl.SetWindowFullscreen(handle, 0);
                    sdl.MaximizeWindow(handle);
                    break;
                default:
                    sdl.SetWindowFullscreen(handle, 0);
                    sdl.RestoreWindow(handle);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public bool IsResizable {
        get => HasFlag(WindowFlags.Resizable);
        set {
            ThrowIfClosed();
            sdl.SetWindowResizable(handle, value ? SdlBool.True : SdlBool.False);
        }
    }

    /// <inheritdoc />
    public bool IsVisible => HasFlag(WindowFlags.Shown);

    /// <inheritdoc />
    public bool IsFocused => HasFlag(WindowFlags.InputFocus);

    /// <inheritdoc />
    public bool IsMinimised => HasFlag(WindowFlags.Minimized);

    /// <inheritdoc />
    public bool IsClosed => handle is null;

    /// <inheritdoc />
    public int DisplayIndex {
        get {
            ThrowIfClosed();
            return Math.Max(0, sdl.GetWindowDisplayIndex(handle));
        }
    }

    /// <inheritdoc />
    public CursorMode CursorMode {
        get => cursorMode;
        set {
            ThrowIfClosed();

            if (value == cursorMode) {
                return;
            }

            cursorMode = value;

            // Relative mode is global in SDL rather than per-window, which is correct in practice —
            // only the focused window can own the pointer — but means the last window to ask wins.
            sdl.SetRelativeMouseMode(value == CursorMode.Relative ? SdlBool.True : SdlBool.False);
            sdl.SetWindowGrab(handle, value == CursorMode.Confined ? SdlBool.True : SdlBool.False);
            sdl.ShowCursor(value is CursorMode.Hidden or CursorMode.Relative ? 0 : 1);
        }
    }

    /// <inheritdoc />
    public CursorShape CursorShape {
        get => cursorShape;
        set {
            ThrowIfClosed();

            if (value == cursorShape && cursor is not null) {
                return;
            }

            cursorShape = value;
            var replacement = sdl.CreateSystemCursor(SdlTranslation.ToSdl(value));

            if (replacement is null) {
                return;
            }

            sdl.SetCursor(replacement);

            // Freed only after the new one is installed: SDL keeps using the active cursor until it
            // is replaced, and freeing it first is a use-after-free that shows up as a corrupt
            // pointer image rather than a crash.
            if (cursor is not null) {
                sdl.FreeCursor(cursor);
            }

            cursor = replacement;
        }
    }

    /// <inheritdoc />
    public ISurface Surface {
        get {
            ThrowIfClosed();
            return surface;
        }
    }

    internal SdlWindow* Handle => handle;

    /// <inheritdoc />
    public void Show() {
        ThrowIfClosed();
        sdl.ShowWindow(handle);
    }

    /// <inheritdoc />
    public void Hide() {
        ThrowIfClosed();
        sdl.HideWindow(handle);
    }

    /// <inheritdoc />
    public void Focus() {
        ThrowIfClosed();
        sdl.RaiseWindow(handle);
    }

    /// <inheritdoc />
    public void Centre() {
        ThrowIfClosed();
        sdl.SetWindowPosition(handle, Sdl.WindowposCentered, Sdl.WindowposCentered);
    }

    /// <inheritdoc />
    public void RequestAttention() {
        ThrowIfClosed();
        sdl.FlashWindow(handle, FlashOperation.UntilFocused);
    }

    /// <inheritdoc />
    public void SetIcon(ReadOnlySpan<byte> pixels, Int2 size) {
        ThrowIfClosed();

        var expected = (long)size.X * size.Y * 4;

        if (size.X <= 0 || size.Y <= 0 || pixels.Length != expected) {
            throw new ArgumentException(
                $"An icon of {size.X}×{size.Y} needs {expected} bytes of RGBA8; got {pixels.Length}.",
                nameof(pixels)
            );
        }

        fixed (byte* data = pixels) {
            // Masks given explicitly rather than through a format constant: SDL's RGBA8888 is
            // byte-order-dependent, and hard-coding it puts the alpha channel in the red one on a
            // big-endian build. These masks describe the byte order the caller was told to supply.
            var image = sdl.CreateRGBSurfaceFrom(
                data,
                size.X,
                size.Y,
                32,
                size.X * 4,
                0x000000FFu,
                0x0000FF00u,
                0x00FF0000u,
                0xFF000000u
            );

            if (image is null) {
                return;
            }

            sdl.SetWindowIcon(handle, image);
            sdl.FreeSurface(image);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (handle is null) {
            return;
        }

        surface.Release();

        if (cursor is not null) {
            sdl.FreeCursor(cursor);
            cursor = null;
        }

        sdl.DestroyWindow(handle);
        handle = null;
    }

    bool HasFlag(WindowFlags flag) {
        ThrowIfClosed();
        return ((WindowFlags)sdl.GetWindowFlags(handle)).HasFlag(flag);
    }

    void ThrowIfClosed() => ObjectDisposedException.ThrowIf(handle is null, this);
}
