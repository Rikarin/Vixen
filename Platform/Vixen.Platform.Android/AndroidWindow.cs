// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Android;

/// <summary>The activity's surface, behind <see cref="IWindow" />.</summary>
/// <remarks>
///     <para>
///         The same shape as the iOS window and for the same reasons: one of them, screen-sized,
///         unmovable, no cursor, no title bar. What differs is underneath —
///         <see cref="PlatformEventKind.WindowResized" /> here also covers the surface being
///         destroyed and recreated, which has no desktop equivalent at all.
///     </para>
///     <para>
///         <b><see cref="Surface" /> can have nothing behind it, and says so.</b> Between
///         <c>surfaceDestroyed</c> and the next <c>surfaceCreated</c> there is no
///         <c>ANativeWindow</c>, so the handle reads as <see cref="SurfaceHandle.None" /> and
///         <c>CanPresent</c> is false. A renderer that checks it — as the Hello Triangle sample
///         already does before building a device — needs no Android-specific path.
///     </para>
/// </remarks>
internal sealed class AndroidWindow : IWindow {
    readonly AndroidGameView view;
    readonly AndroidSurface surface;

    bool closed;
    string title = string.Empty;

    internal AndroidWindow(uint id, AndroidGameView view) {
        Id = id;
        this.view = view;
        surface = new(view);
        view.Attach(id);
    }

    /// <inheritdoc />
    public uint Id { get; }

    internal AndroidGameView View => view;

    /// <inheritdoc />
    /// <remarks>Kept and returned; Android shows it nowhere, the activity's label being a manifest
    /// entry rather than a runtime one.</remarks>
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
            var density = Density;
            var pixels = view.PixelSize;
            return new((int)(pixels.X / density), (int)(pixels.Y / density));
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
    public Int2 FramebufferSize {
        get {
            ThrowIfClosed();
            return view.PixelSize;
        }
    }

    /// <inheritdoc />
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
            return Density;
        }
    }

    /// <inheritdoc />
    public WindowMode Mode {
        get {
            ThrowIfClosed();
            return WindowMode.BorderlessFullscreen;
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
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
            return view.Visibility == global::Android.Views.ViewStates.Visible;
        }
    }

    /// <inheritdoc />
    public bool IsFocused {
        get {
            ThrowIfClosed();
            return view.HasWindowFocus;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Always false. An Android application is stopped rather than minimised, which
    ///     <see cref="ILifecycle" /> reports and which is a stronger statement: a minimised window
    ///     still owns its surface, a stopped activity does not.
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
    public int DisplayIndex {
        get {
            ThrowIfClosed();
            return 0;
        }
    }

    /// <inheritdoc />
    public CursorMode CursorMode {
        get {
            ThrowIfClosed();
            return CursorMode.Normal;
        }
        set { ThrowIfClosed(); }
    }

    /// <inheritdoc />
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
        view.Visibility = global::Android.Views.ViewStates.Visible;
    }

    /// <inheritdoc />
    public void Hide() {
        ThrowIfClosed();
        view.Visibility = global::Android.Views.ViewStates.Gone;
    }

    /// <inheritdoc />
    public void Focus() {
        ThrowIfClosed();
        view.RequestFocus();
    }

    /// <inheritdoc />
    public void Centre() => ThrowIfClosed();

    /// <inheritdoc />
    public void RequestAttention() => ThrowIfClosed();

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing. An Android icon is a manifest resource. Validated anyway, so code that is wrong
    ///     here is wrong everywhere rather than only on the platforms that look.
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
        view.Dispose();
    }

    float Density => view.Resources?.DisplayMetrics?.Density ?? 1f;

    void ThrowIfClosed() => ObjectDisposedException.ThrowIf(closed, this);
}

/// <summary>The <c>ANativeWindow</c> Vulkan presents to, when there is one.</summary>
/// <param name="view">The view that owns it.</param>
internal sealed class AndroidSurface(AndroidGameView view) : ISurface {
    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="SurfaceHandle.None" /> between <c>surfaceDestroyed</c> and the next
    ///     <c>surfaceCreated</c>, which is a real state an Android application spends time in rather
    ///     than an error.
    /// </remarks>
    public SurfaceHandle Handle =>
        view.HasSurface ? new(SurfaceKind.Android, 0, view.NativeWindow) : SurfaceHandle.None;

    /// <inheritdoc />
    public Int2 PixelSize => view.PixelSize;
}
