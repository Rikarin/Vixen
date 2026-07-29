// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Ui;

namespace Vixen.Platform.Ui;

/// <summary>Gives a document's surfaces real operating-system windows.</summary>
/// <remarks>
///     <para>
///         <b>The implementation of <see cref="IUiWindowHost" />, which is the seam a
///         <c>Core/</c> assembly could declare and could not fill.</b> A control asks the document
///         for a window; the document asks this; this asks the platform. Nothing above
///         <c>Vixen.Platform</c> is named anywhere in the chain, so a docking host that tears a panel
///         out onto the desktop is the same docking host that floats it inside the page in a browser.
///     </para>
///     <para>
///         ⚠ <b>The main window is registered rather than created.</b> The application head made it,
///         gave it a swapchain and is presenting to it; this only needs to know which surface it is
///         showing, because that is what makes <see cref="TryLocate" /> able to answer for it — and
///         a drag out of the main window is measured from exactly there.
///     </para>
///     <para>
///         <b>Threading.</b> The platform is owned by the thread that created it and so is this.
///         Every window operation here is a platform call, and AppKit refuses to touch a window from
///         anywhere but the main thread.
///     </para>
/// </remarks>
public sealed class PlatformWindowHost : IUiWindowHost, IDisposable {
    readonly IPlatform platform;
    readonly UiDocument owner;
    readonly List<PlatformUiWindow> windows = [];

    /// <summary>Registers a document's primary surface as living in an existing window.</summary>
    /// <param name="platform">The platform.</param>
    /// <param name="document">The document.</param>
    /// <param name="main">The window the primary surface is already being presented to.</param>
    /// <remarks>
    ///     Installs itself on the document, because a host nobody was told about is a host that never
    ///     gets asked. An application that wants the framework to <i>not</i> open windows simply does
    ///     not construct one.
    /// </remarks>
    public PlatformWindowHost(IPlatform platform, UiDocument document, IWindow main) {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(main);

        this.platform = platform;
        owner = document;

        Main = main;
        owner.Windows = this;

        // The primary surface is laid out for the window it is already in, which nothing else has
        // told it. Without this a document constructed at a nominal size and then handed a real
        // window would lay out at the nominal one until the first resize arrived.
        Fit(owner.Primary, main);
    }

    /// <summary>The window the primary surface is shown in.</summary>
    public IWindow Main { get; }

    /// <summary>The windows this host opened, in the order it opened them.</summary>
    /// <remarks>
    ///     Excludes <see cref="Main" />, which it did not open. A renderer walks this to give each
    ///     one a swapchain of its own.
    /// </remarks>
    public IReadOnlyList<PlatformUiWindow> Windows => windows;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><see cref="PlatformCapabilities.MultiWindow" /> alone, and deliberately not
    ///     <see cref="PlatformCapabilities.Windowing" /> as well.</b> A headless window has a size,
    ///     an id and an event stream and shows nobody anything — <c>IPlatform.CreateWindow</c> says
    ///     so — which is what lets a headless <c>--frames</c> run lay out a torn-off panel and prove
    ///     the tear-out path rather than skip it. What is absent on such a platform is somewhere to
    ///     present, and the head is what notices that.
    /// </remarks>
    public bool CanOpen => platform.Has(PlatformCapabilities.MultiWindow);

    /// <inheritdoc />
    public IUiWindow? Open(UiDocument document, in UiWindowRequest request) {
        ArgumentNullException.ThrowIfNull(document);

        if (!ReferenceEquals(document, owner)) {
            throw new ArgumentException("this host was built for another document.", nameof(document));
        }

        if (!CanOpen) {
            return null;
        }

        var window = platform.CreateWindow(
            new WindowOptions {
                Title = request.Title,
                Size = Whole(request.Width, request.Height),
                Position = Whole(request.X, request.Y),
                IsResizable = request.IsResizable,
                IsDecorated = request.IsDecorated,
                IsVisible = true
            }
        );

        // ⚠ Created from what the window actually is rather than from what was asked for. A window
        // manager may have placed it elsewhere, clamped its size to the display, or put it on a
        // second monitor at a different scale — and a surface laid out for the request would be laid
        // out for a window that does not exist.
        var opened = new PlatformUiWindow(this, window, owner.CreateSurface(1f, 1f, 1f, request.Owner));
        Fit(opened.Surface, window);

        windows.Add(opened);
        return opened;
    }

    /// <inheritdoc />
    public bool TryLocate(UiSurface surface, out float x, out float y) {
        x = 0f;
        y = 0f;

        if (surface is null || !platform.Has(PlatformCapabilities.WindowPositioning)) {
            return false;
        }

        if (!TryWindow(surface, out var window)) {
            return false;
        }

        var position = window.Position;

        x = position.X;
        y = position.Y;

        return true;
    }

    /// <summary>Which surface a window's events belong to.</summary>
    /// <param name="windowId">The id <see cref="PlatformEvent.WindowId" /> carries.</param>
    /// <param name="surface">The surface.</param>
    /// <returns>Whether this host knows that window.</returns>
    public bool TryResolve(uint windowId, [NotNullWhen(true)] out UiSurface? surface) {
        if (windowId == Main.Id) {
            surface = owner.Primary;
            return true;
        }

        foreach (var window in windows) {
            if (window.Window.Id == windowId) {
                surface = window.Surface;
                return true;
            }
        }

        surface = null;
        return false;
    }

    /// <summary>Which window a surface is shown in.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="window">The window.</param>
    /// <returns>Whether this host knows that surface.</returns>
    public bool TryWindow(UiSurface surface, [NotNullWhen(true)] out IWindow? window) {
        if (ReferenceEquals(surface, owner.Primary)) {
            window = Main;
            return true;
        }

        foreach (var opened in windows) {
            if (ReferenceEquals(opened.Surface, surface)) {
                window = opened.Window;
                return true;
            }
        }

        window = null;
        return false;
    }

    /// <summary>Deals with the events that are about a window rather than about input.</summary>
    /// <param name="platformEvent">What happened.</param>
    /// <returns>Whether this dealt with it, so that a host loop need not.</returns>
    /// <remarks>
    ///     ⚠ <b>A close on the main window is deliberately <i>not</i> handled.</b> Closing the last
    ///     window is the application quitting, which is the head's decision and not a window host's —
    ///     and swallowing it here would give an editor whose title-bar close button does nothing.
    ///     A close on a torn-off one is a request the docking host answers by bringing the panels
    ///     home.
    /// </remarks>
    public bool Handle(in PlatformEvent platformEvent) {
        switch (platformEvent.Kind) {
            case PlatformEventKind.WindowResized:
            case PlatformEventKind.WindowDpiChanged:
                if (!TryResolve(platformEvent.WindowId, out var resized) || !TryWindow(resized, out var window)) {
                    return false;
                }

                Fit(resized, window);

                // Only a torn-off window announces itself. The main one's size is the head's
                // business, and it is already reading it to rebuild a swapchain.
                Announce(platformEvent.WindowId);

                // ⚠ Not consumed. The head still has a swapchain to rebuild for this window, and a
                // resize this quietly ate would be a window that lays out at its new size and
                // presents at its old one.
                return false;

            case PlatformEventKind.WindowMoved:
                Announce(platformEvent.WindowId);
                return false;

            case PlatformEventKind.WindowCloseRequested:
                foreach (var opened in windows) {
                    if (opened.Window.Id == platformEvent.WindowId) {
                        opened.RequestClose();
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>Closes every window this opened. The main one is the head's and is left alone.</remarks>
    public void Dispose() {
        for (var i = windows.Count - 1; i >= 0; i--) {
            windows[i].Dispose();
        }

        windows.Clear();

        if (ReferenceEquals(owner.Windows, this)) {
            owner.Windows = null;
        }
    }

    internal void Forget(PlatformUiWindow window) => windows.Remove(window);

    internal UiDocument Document => owner;

    /// <summary>Lays a surface out for the window it is in.</summary>
    /// <remarks>
    ///     ⚠ <b>The framebuffer divided by the scale, not the client size.</b> The two agree except
    ///     for the platform's rounding, and where they disagree the framebuffer is the one the
    ///     scissor rectangle is derived from — so a document laid out from the client size can be a
    ///     pixel wider than what is drawn, which is a column of interface clipped off the right-hand
    ///     edge on exactly the fractional-scale displays that are hardest to test on.
    /// </remarks>
    static void Fit(UiSurface surface, IWindow window) {
        var scale = window.DpiScale <= 0f ? 1f : window.DpiScale;
        var framebuffer = window.FramebufferSize;

        surface.Document.Resize(surface, framebuffer.X / scale, framebuffer.Y / scale, scale);
    }

    void Announce(uint windowId) {
        foreach (var opened in windows) {
            if (opened.Window.Id == windowId) {
                opened.Announce();
                return;
            }
        }
    }

    static Int2 Whole(float x, float y) => new((int) MathF.Round(x), (int) MathF.Round(y));
}

/// <summary>One operating-system window showing one surface of a document.</summary>
public sealed class PlatformUiWindow : IUiWindow {
    readonly PlatformWindowHost host;

    internal PlatformUiWindow(PlatformWindowHost host, IWindow window, UiSurface surface) {
        this.host = host;

        Window = window;
        Surface = surface;
    }

    /// <summary>The platform's window.</summary>
    /// <remarks>
    ///     Exposed because a renderer needs the surface handle to build a swapchain, and this
    ///     assembly deliberately knows nothing about graphics.
    /// </remarks>
    public IWindow Window { get; }

    /// <inheritdoc />
    public UiSurface Surface { get; }

    /// <inheritdoc />
    public string Title {
        get => Window.Title;
        set => Window.Title = value;
    }

    /// <inheritdoc />
    public (float X, float Y, float Width, float Height) Bounds {
        get {
            var position = Window.Position;
            var scale = Window.DpiScale <= 0f ? 1f : Window.DpiScale;
            var framebuffer = Window.FramebufferSize;

            return (position.X, position.Y, framebuffer.X / scale, framebuffer.Y / scale);
        }

        set {
            Window.Position = new Int2((int) MathF.Round(value.X), (int) MathF.Round(value.Y));
            Window.ClientSize = new Int2((int) MathF.Round(value.Width), (int) MathF.Round(value.Height));
        }
    }

    /// <inheritdoc />
    public float DpiScale => Window.DpiScale <= 0f ? 1f : Window.DpiScale;

    /// <inheritdoc />
    public bool IsClosed { get; private set; }

    /// <inheritdoc />
    public void Focus() => Window.Focus();

    /// <inheritdoc />
    public event Action<IUiWindow>? CloseRequested;

    /// <inheritdoc />
    public event Action<IUiWindow>? Moved;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The surface goes with the window, and everything still inside it goes with the
    ///     surface.</b> That is what makes closing a torn-off panel that nobody rescued a close
    ///     rather than a leak — and it is why the docking host reparents its panels out <i>before</i>
    ///     it lets a window be disposed.
    /// </remarks>
    public void Dispose() {
        if (IsClosed) {
            return;
        }

        IsClosed = true;

        host.Forget(this);
        host.Document.RemoveSurface(Surface);

        Window.Dispose();
    }

    internal void RequestClose() => CloseRequested?.Invoke(this);

    internal void Announce() => Moved?.Invoke(this);
}
