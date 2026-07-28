// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Web;

/// <summary>A canvas, behind <see cref="IWindow" />.</summary>
/// <remarks>
///     <para>
///         <b>A page is not a window manager, and this does not pretend otherwise.</b> A canvas has
///         no position on any desktop, cannot be minimised, cannot be raised above another
///         application, and cannot have an icon of its own. Every one of those is reported absent
///         through <see cref="PlatformCapabilities" /> and implemented as the honest no-op, rather
///         than as an approximation that would make an editor's saved window placement restore to
///         somewhere meaningless.
///     </para>
///     <para>
///         What <em>is</em> real is size, scale, focus, fullscreen and the cursor, and all five are
///         read back from the DOM rather than remembered. Setting <see cref="ClientSize" /> writes a
///         CSS size, which the page's layout may then ignore entirely — a canvas at
///         <c>width: 100%</c> is sized by its container and by nothing else. The
///         <c>ResizeObserver</c> reports what the page decided, and that is what these properties
///         return.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebWindow : IWindow {
    readonly int canvas;
    readonly PlatformEventBuffer events;
    readonly WebSurface surface;

    string title;
    WindowMode mode;
    CursorMode cursorMode;
    CursorShape cursorShape = CursorShape.Arrow;
    bool disposed;

    internal WebWindow(uint id, int canvas, PlatformEventBuffer events, in WindowOptions options) {
        Id = id;
        this.canvas = canvas;
        this.events = events;
        surface = new(canvas);
        title = options.Title;
        mode = options.Mode;

        WebInterop.SetTitle(title);

        if (options.Mode is WindowMode.BorderlessFullscreen or WindowMode.ExclusiveFullscreen) {
            // Deferred to the first user gesture rather than refused: requestFullscreen outside one
            // rejects, and the frame loop is not a gesture. Recorded so that a click handler asking
            // for the mode it was already in is not a no-op.
            WebInterop.RequestFullscreen(canvas);
        }

        if (!options.IsVisible) {
            WebInterop.SetVisible(canvas, visible: false);
        }
    }

    /// <summary>The canvas handle, which is also <see cref="ISurface.Handle" />'s
    /// <c>Handle</c>.</summary>
    internal int Canvas => canvas;

    /// <inheritdoc />
    public uint Id { get; }

    /// <inheritdoc />
    /// <remarks>The document's title. A tab has one and a canvas has none, so this is the closest
    /// true thing — and it is what the browser shows the user.</remarks>
    public string Title {
        get => title;
        set {
            ThrowIfClosed();
            title = value ?? string.Empty;
            WebInterop.SetTitle(title);
        }
    }

    /// <inheritdoc />
    public Int2 ClientSize {
        get {
            ThrowIfClosed();
            return new(WebInterop.ClientWidth(canvas), WebInterop.ClientHeight(canvas));
        }
        set {
            ThrowIfClosed();
            WebInterop.SetClientSize(canvas, Math.Max(1, value.X), Math.Max(1, value.Y));
        }
    }

    /// <inheritdoc />
    public Int2 FramebufferSize {
        get {
            ThrowIfClosed();
            return surface.PixelSize;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Always <see cref="Int2.Zero" />, and writes are ignored. A page cannot know where its own
    ///     window is on the user's desktop and cannot move it, which is why
    ///     <see cref="PlatformCapabilities.WindowPositioning" /> is absent here.
    /// </remarks>
    public Int2 Position {
        get => Int2.Zero;
        set { }
    }

    /// <inheritdoc />
    public float DpiScale {
        get {
            ThrowIfClosed();
            return (float)WebInterop.DpiScale(canvas);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="WindowMode.ExclusiveFullscreen" /> is granted as
    ///     <see cref="WindowMode.BorderlessFullscreen" />, which is what
    ///     <see cref="WindowMode" /> says every platform without a mode-setting API should do: a
    ///     page cannot change the display's resolution and asking is not an error.
    ///     <see cref="WindowMode.Maximised" /> means nothing to a canvas and reads back as
    ///     <see cref="WindowMode.Windowed" />.
    /// </remarks>
    public WindowMode Mode {
        get {
            ThrowIfClosed();
            return WebInterop.IsFullscreen(canvas) ? WindowMode.BorderlessFullscreen : WindowMode.Windowed;
        }
        set {
            ThrowIfClosed();
            mode = value;

            if (value is WindowMode.BorderlessFullscreen or WindowMode.ExclusiveFullscreen) {
                WebInterop.RequestFullscreen(canvas);
            } else if (WebInterop.IsFullscreen(canvas)) {
                WebInterop.ExitFullscreen();
            }
        }
    }

    /// <summary>The mode that was last asked for, which fullscreen may not have been granted
    /// yet.</summary>
    /// <remarks>
    ///     Kept so that an application can re-ask from a click handler. <c>requestFullscreen</c> only
    ///     succeeds inside a user gesture, so a window created fullscreen is not fullscreen until
    ///     the user touches something — which is the browser's rule and not one worth hiding.
    /// </remarks>
    internal WindowMode RequestedMode => mode;

    /// <inheritdoc />
    /// <remarks>
    ///     Always <see langword="true" />, and writes are ignored: whether a canvas can be resized
    ///     is a question about the page's CSS, and the user resizes the browser window rather than
    ///     the element.
    /// </remarks>
    public bool IsResizable {
        get => true;
        set { }
    }

    /// <inheritdoc />
    public bool IsVisible {
        get {
            ThrowIfClosed();
            return WebInterop.IsVisible(canvas);
        }
    }

    /// <inheritdoc />
    public bool IsFocused {
        get {
            ThrowIfClosed();
            return WebInterop.IsFocused(canvas);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Never. A tab that is not being composited is <see cref="ApplicationState.Background" />
    ///     and arrives as a <see cref="PlatformEventKind.Suspending" />, which is the signal that
    ///     matters and the one a renderer should be reading. Reporting it as a minimised window
    ///     would be reporting one platform's concept in another's vocabulary.
    /// </remarks>
    public bool IsMinimised => false;

    /// <inheritdoc />
    public bool IsClosed => disposed;

    /// <inheritdoc />
    /// <remarks>Always <c>0</c>. A page has one display as far as it is allowed to know.</remarks>
    public int DisplayIndex => 0;

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="CursorMode.Relative" /> is Pointer Lock, which — like fullscreen — is only
    ///     granted inside a user gesture. Reading this back gives what the browser did, not what was
    ///     asked for, which is why a first-person camera should set it from a click handler and
    ///     check.
    /// </remarks>
    public CursorMode CursorMode {
        get {
            ThrowIfClosed();
            return WebInterop.IsPointerLocked(canvas) ? CursorMode.Relative : cursorMode;
        }
        set {
            ThrowIfClosed();

            if (cursorMode == CursorMode.Relative && value != CursorMode.Relative) {
                WebInterop.ExitPointerLock();
            }

            cursorMode = value;

            switch (value) {
                case CursorMode.Relative:
                    WebInterop.RequestPointerLock(canvas);
                    break;

                case CursorMode.Hidden:
                    WebInterop.SetCursor(canvas, "none");
                    break;

                // Confinement without pointer lock is not a thing a page may do — it would let a
                // page trap the pointer — so it is served as an ordinary cursor rather than
                // silently locked, which would hide it and stop reporting a position.
                case CursorMode.Normal:
                case CursorMode.Confined:
                default:
                    WebInterop.SetCursor(canvas, CssCursor(cursorShape));
                    break;
            }
        }
    }

    /// <inheritdoc />
    public CursorShape CursorShape {
        get => cursorShape;
        set {
            ThrowIfClosed();
            cursorShape = value;

            if (cursorMode != CursorMode.Hidden) {
                WebInterop.SetCursor(canvas, CssCursor(value));
            }
        }
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
        WebInterop.SetVisible(canvas, visible: true);
    }

    /// <inheritdoc />
    public void Hide() {
        ThrowIfClosed();
        WebInterop.SetVisible(canvas, visible: false);
    }

    /// <inheritdoc />
    public void Focus() {
        ThrowIfClosed();
        WebInterop.Focus(canvas);
    }

    /// <inheritdoc />
    /// <remarks>Nothing. A canvas has no position to centre, and a page cannot move its
    /// window.</remarks>
    public void Centre() => ThrowIfClosed();

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing. The browser's own attention mechanism is a notification, which needs a
    ///     permission prompt and reaches the user's desktop rather than the tab — that is
    ///     <see cref="PermissionKind.Notifications" />'s business and an application's decision, not
    ///     something a window method should do behind one.
    /// </remarks>
    public void RequestAttention() => ThrowIfClosed();

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing. A tab's icon is the favicon, which is a document-level resource the page's own
    ///     HTML owns; replacing it from here would take that away from the page for the sake of an
    ///     API shape.
    /// </remarks>
    public void SetIcon(ReadOnlySpan<byte> pixels, Int2 size) {
        ThrowIfClosed();

        if (pixels.Length != size.X * size.Y * 4) {
            throw new ArgumentException(
                $"{size.X}×{size.Y} RGBA8 is {size.X * size.Y * 4} bytes, not {pixels.Length}.",
                nameof(pixels)
            );
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (WebInterop.IsPointerLocked(canvas)) {
            WebInterop.ExitPointerLock();
        }

        WebInterop.DestroyCanvas(canvas);
        events.Post(PlatformEvent.Window(PlatformEventKind.WindowHidden, Id, WebClock.Now));
    }

    /// <summary>The CSS name for a stock cursor.</summary>
    /// <remarks>
    ///     CSS names them by <em>role</em> — <c>text</c>, <c>grab</c>, <c>not-allowed</c> — which is
    ///     the same idea <see cref="Vixen.Platform.CursorShape" /> has, so this is a rename and not
    ///     a set of bitmaps. That is the point of the enum: the user's theme, size and accessibility
    ///     settings are honoured because the browser draws its own.
    /// </remarks>
    static string CssCursor(CursorShape shape) => shape switch {
        CursorShape.TextBeam => "text",
        CursorShape.Wait => "wait",
        CursorShape.Crosshair => "crosshair",
        CursorShape.Hand => "pointer",
        CursorShape.ResizeHorizontal => "ew-resize",
        CursorShape.ResizeVertical => "ns-resize",
        CursorShape.ResizeDiagonalUp => "nesw-resize",
        CursorShape.ResizeDiagonalDown => "nwse-resize",
        CursorShape.ResizeAll => "move",
        CursorShape.NotAllowed => "not-allowed",
        CursorShape.Arrow => "default",
        _ => "default"
    };

    void ThrowIfClosed() => ObjectDisposedException.ThrowIf(disposed, this);
}
