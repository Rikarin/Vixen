// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Headless;

/// <summary>A window that nobody can see.</summary>
/// <remarks>
///     <para>
///         It has a size, an id, a scale factor, focus, a lifecycle and an event stream — everything
///         a window has except a picture. That is the point: the dedicated server and the golden-image
///         test run the same frame loop the desktop build runs, and the no-window path is exercised
///         on every test run rather than being written once for Phase 9 and found broken there.
///     </para>
///     <para>
///         Its state is also writable in a way a real window's is not, and deliberately: setting
///         <see cref="ClientSize" /> here really does resize it and really does raise
///         <see cref="PlatformEventKind.WindowResized" />, so a test can drive a swapchain-rebuild
///         path without a display server. A real window would treat the same assignment as a request
///         the window manager may refuse, which is why code that must know listens for the event on
///         both.
///     </para>
/// </remarks>
public sealed class HeadlessWindow : IWindow {
    readonly PlatformEventBuffer events;
    readonly HeadlessSurface surface;

    Int2 clientSize;
    Int2 position;
    float dpiScale;
    string title;
    WindowMode mode;

    internal HeadlessWindow(uint id, in WindowOptions options, PlatformEventBuffer events) {
        Id = id;
        this.events = events;

        title = options.Title;
        clientSize = options.Size;
        position = options.Position ?? Int2.Zero;
        mode = options.Mode;
        dpiScale = 1f;
        IsResizable = options.IsResizable;
        IsVisible = options.IsVisible;
        surface = new(this);
    }

    /// <inheritdoc />
    public uint Id { get; }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Int2 ClientSize {
        get {
            ThrowIfClosed();
            return clientSize;
        }
        set {
            ThrowIfClosed();

            if (value == clientSize) {
                return;
            }

            clientSize = value;
            events.Post(PlatformEvent.WindowResized(Id, Now, clientSize, FramebufferSize));
        }
    }

    /// <inheritdoc />
    public Int2 FramebufferSize {
        get {
            ThrowIfClosed();
            return new((int)(clientSize.X * dpiScale), (int)(clientSize.Y * dpiScale));
        }
    }

    /// <inheritdoc />
    public Int2 Position {
        get {
            ThrowIfClosed();
            return position;
        }
        set {
            ThrowIfClosed();

            if (value == position) {
                return;
            }

            position = value;
            events.Post(PlatformEvent.WindowMoved(Id, Now, position));
        }
    }

    /// <inheritdoc />
    public float DpiScale {
        get {
            ThrowIfClosed();
            return dpiScale;
        }
    }

    /// <inheritdoc />
    public WindowMode Mode {
        get {
            ThrowIfClosed();
            return mode;
        }
        set {
            ThrowIfClosed();
            mode = value;
        }
    }

    /// <inheritdoc />
    public bool IsResizable { get; set; }

    /// <inheritdoc />
    public bool IsVisible { get; private set; }

    /// <inheritdoc />
    public bool IsFocused { get; private set; }

    /// <inheritdoc />
    public bool IsMinimised { get; private set; }

    /// <inheritdoc />
    public bool IsClosed { get; private set; }

    /// <summary>Always <c>0</c>: a headless platform has no displays to be on.</summary>
    public int DisplayIndex => 0;

    /// <inheritdoc />
    public CursorMode CursorMode { get; set; }

    /// <inheritdoc />
    public CursorShape CursorShape { get; set; }

    /// <inheritdoc />
    public ISurface Surface {
        get {
            ThrowIfClosed();
            return surface;
        }
    }

    /// <summary>
    ///     The icon the window was last given, so a test can assert that an application set one.
    /// </summary>
    /// <remarks>
    ///     Kept rather than discarded because "the icon was set" is otherwise unobservable, and a
    ///     platform that silently swallows what it is handed is a platform that cannot be tested
    ///     against.
    /// </remarks>
    public ReadOnlyMemory<byte> Icon { get; private set; }

    /// <summary>The size of <see cref="Icon" />.</summary>
    public Int2 IconSize { get; private set; }

    /// <summary>How many times <see cref="RequestAttention" /> has been called.</summary>
    public int AttentionRequests { get; private set; }

    static long Now => Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public void Show() {
        ThrowIfClosed();

        if (IsVisible) {
            return;
        }

        IsVisible = true;
        events.Post(PlatformEvent.Window(PlatformEventKind.WindowShown, Id, Now));
    }

    /// <inheritdoc />
    public void Hide() {
        ThrowIfClosed();

        if (!IsVisible) {
            return;
        }

        IsVisible = false;
        events.Post(PlatformEvent.Window(PlatformEventKind.WindowHidden, Id, Now));
    }

    /// <inheritdoc />
    public void Focus() {
        ThrowIfClosed();
        SetFocused(true);
    }

    /// <summary>Does nothing: there is no display to be centred on.</summary>
    public void Centre() => ThrowIfClosed();

    /// <inheritdoc />
    public void RequestAttention() {
        ThrowIfClosed();
        AttentionRequests++;
    }

    /// <inheritdoc />
    public void SetIcon(ReadOnlySpan<byte> pixels, Int2 size) {
        ThrowIfClosed();

        var expected = (long)size.X * size.Y * 4;

        if (size.X < 0 || size.Y < 0 || pixels.Length != expected) {
            throw new ArgumentException(
                $"An icon of {size.X}×{size.Y} needs {expected} bytes of RGBA8; got {pixels.Length}.",
                nameof(pixels)
            );
        }

        Icon = pixels.ToArray();
        IconSize = size;
    }

    /// <summary>Gives or takes keyboard focus, raising the matching event.</summary>
    /// <param name="focused">Whether the window should have focus.</param>
    /// <remarks>
    ///     The platform's job on a desktop, and a test's job here — losing focus is what an input
    ///     layer has to clear its held-key state on, and there is no other way to make that happen
    ///     without a window manager.
    /// </remarks>
    public void SetFocused(bool focused) {
        ThrowIfClosed();

        if (focused == IsFocused) {
            return;
        }

        IsFocused = focused;

        events.Post(
            PlatformEvent.Window(
                focused ? PlatformEventKind.WindowFocusGained : PlatformEventKind.WindowFocusLost,
                Id,
                Now
            )
        );
    }

    /// <summary>Minimises or restores the window, raising the matching event.</summary>
    /// <param name="minimised">Whether the window should be minimised.</param>
    public void SetMinimised(bool minimised) {
        ThrowIfClosed();

        if (minimised == IsMinimised) {
            return;
        }

        IsMinimised = minimised;

        events.Post(
            PlatformEvent.Window(
                minimised ? PlatformEventKind.WindowMinimised : PlatformEventKind.WindowRestored,
                Id,
                Now
            )
        );
    }

    /// <summary>Changes the scale factor, raising
    /// <see cref="PlatformEventKind.WindowDpiChanged" />.</summary>
    /// <param name="scale">Physical pixels per logical point. Must be positive.</param>
    /// <remarks>
    ///     The one way to exercise a HiDPI path without a HiDPI display. Dragging a window between
    ///     a 1× and a 2× monitor is the case that breaks swapchain sizing, and it is not otherwise
    ///     reachable from a test.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale" /> is not
    /// positive.</exception>
    public void SetDpiScale(float scale) {
        ThrowIfClosed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);

        if (Math.Abs(scale - dpiScale) < float.Epsilon) {
            return;
        }

        dpiScale = scale;
        events.Post(PlatformEvent.WindowDpiChanged(Id, Now, scale));
    }

    /// <summary>Raises <see cref="PlatformEventKind.WindowCloseRequested" /> without closing
    /// anything.</summary>
    /// <remarks>What the title bar's close button does on a real platform.</remarks>
    public void RequestClose() {
        ThrowIfClosed();
        events.Post(PlatformEvent.Window(PlatformEventKind.WindowCloseRequested, Id, Now));
    }

    /// <inheritdoc />
    public void Dispose() {
        if (IsClosed) {
            return;
        }

        IsClosed = true;
        IsVisible = false;
        IsFocused = false;
    }

    void ThrowIfClosed() =>
        ObjectDisposedException.ThrowIf(IsClosed, this);

    /// <summary>The nothing a headless window presents to.</summary>
    sealed class HeadlessSurface(HeadlessWindow window) : ISurface {
        public SurfaceHandle Handle => SurfaceHandle.None;

        public Int2 PixelSize => window.FramebufferSize;
    }
}
