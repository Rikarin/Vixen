// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>What a control asks for when it wants a window of its own.</summary>
/// <remarks>
///     The coordinates are in device-independent pixels and in <i>desktop</i> space, which is the
///     only space two windows share. A host that cannot honour a position — a tiling window manager,
///     a platform with no window positioning at all — places the window where it likes and reports
///     what happened through <see cref="IUiWindow.Bounds" />.
/// </remarks>
/// <param name="Title">The title bar text.</param>
/// <param name="X">Where its left edge goes.</param>
/// <param name="Y">Its top edge.</param>
/// <param name="Width">How wide.</param>
/// <param name="Height">How tall.</param>
public readonly record struct UiWindowRequest(string Title, float X, float Y, float Width, float Height) {
    /// <summary>Whether the user may resize it.</summary>
    public bool IsResizable { get; init; } = true;

    /// <summary>Whether the operating system draws a title bar and a frame around it.</summary>
    public bool IsDecorated { get; init; } = true;

    /// <summary>Which element the window's contents belong under, or <c>null</c> for the root.</summary>
    /// <remarks>
    ///     ⚠ <b>A control asking for a window should always pass itself.</b> A routed event climbs
    ///     the element tree, so a control whose window hung off the document root would never hear a
    ///     click on anything inside it — the chain from a torn-off tab would reach the document
    ///     having missed the control that put the tab there. See
    ///     <see cref="UiDocument.CreateSurface" />.
    /// </remarks>
    public UiElement? Owner { get; init; }
}

/// <summary>A real operating-system window showing one surface of a document.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The interface is here and the implementation cannot be.</b> A window belongs to
///         <c>Vixen.Platform</c>, which is a layer above <c>Core/</c> — so a UI framework that
///         referenced it would invert the dependency doc 00 makes non-negotiable, and would stop
///         being usable with no backend at all. This is the seam: the framework states what it needs
///         of a window and the application head, which is allowed to know about both, supplies it.
///     </para>
///     <para>
///         The surface is the window's, created by the host and owned by it: closing the window
///         removes the surface from the document. Everything <i>inside</i> the surface is the
///         caller's, put there by reparenting rather than by building — which is the whole reason
///         windows are surfaces of one document rather than documents of their own.
///     </para>
/// </remarks>
public interface IUiWindow : IDisposable {
    /// <summary>The part of the document it shows.</summary>
    UiSurface Surface { get; }

    /// <summary>The title bar text.</summary>
    string Title { get; set; }

    /// <summary>Where it is and how big, in device-independent pixels in desktop space.</summary>
    /// <remarks>
    ///     Reads what the platform did rather than what was last asked for, in the way
    ///     <c>IWindow</c> does and for the same reasons: a window manager is entitled to place a
    ///     window somewhere else, and a caller that persists the requested position would save a
    ///     number the user never saw.
    /// </remarks>
    (float X, float Y, float Width, float Height) Bounds { get; set; }

    /// <summary>How many physical pixels one device-independent one is on its display.</summary>
    float DpiScale { get; }

    /// <summary>Whether it has been closed.</summary>
    bool IsClosed { get; }

    /// <summary>Brings it to the front and asks for keyboard focus.</summary>
    void Focus();

    /// <summary>Whether this is the window the user is in — <c>NSWindow.isKeyWindow</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Derived rather than stored, and that is what keeps two windows from both
    ///         believing it.</b> There is exactly one key surface per document, so a window that
    ///         kept a <c>bool</c> of its own would be a second copy of a fact the document already
    ///         holds — and the failure mode of a second copy is two windows drawing an active title
    ///         bar with no error anywhere. A host that answers this question by writing
    ///         <c>document.KeySurface == surface</c> for itself has made the same copy, spelled out.
    ///     </para>
    ///     <para>
    ///         <c>false</c> for every window while the application is in the background, which is
    ///         what <see cref="UiDocument.KeySurface" /> being <c>null</c> means.
    ///     </para>
    /// </remarks>
    bool IsKey => ReferenceEquals(Surface.Document.KeySurface, Surface);

    /// <summary>Raised when this window becomes the key one, and again when it stops being.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>One event for both edges, carrying the window whose <see cref="IsKey" /> is now to
    ///         be read.</b> AppKit has <c>didBecomeKey</c> and <c>didResignKey</c> as a pair; a
    ///         handler that has to redraw a title bar wants both and would subscribe to both, so
    ///         splitting them buys two subscriptions for one question.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not every window raises it.</b> It is raised by whatever host opened the window,
    ///         which for the document's primary surface may be the application head rather than an
    ///         <see cref="IUiWindowHost" /> — the ground truth is
    ///         <see cref="UiDocument.KeySurfaceChanged" />, and this is the per-window convenience
    ///         over it.
    ///     </para>
    /// </remarks>
    event Action<IUiWindow>? DidBecomeKey;

    /// <summary>Raised when the user asks to close it, before anything is destroyed.</summary>
    /// <remarks>
    ///     ⚠ <b>A request, not a notification.</b> The window stays open until somebody disposes it,
    ///     which is what lets the docking host put the panels back into the main window first — and
    ///     what would otherwise make closing a torn-off inspector the same as deleting it.
    /// </remarks>
    event Action<IUiWindow>? CloseRequested;

    /// <summary>Raised after the user has moved or resized it.</summary>
    event Action<IUiWindow>? Moved;
}

/// <summary>Whatever can turn a surface into a window on the user's desktop.</summary>
/// <remarks>
///     ⚠ <b>Absent is the ordinary case rather than a failure.</b> A browser tab has one canvas,
///     Android has one activity surface and iOS has one window; the framework asks
///     <see cref="CanOpen" /> and a control that wanted a real window falls back to a floating panel
///     inside the one it has. That is a runtime question with a runtime answer, which is the same
///     shape <c>PlatformCapabilities</c> uses and for the same reason: nothing above the platform
///     layer carries a <c>#if</c>.
/// </remarks>
public interface IUiWindowHost {
    /// <summary>Whether this host can open a window at all.</summary>
    bool CanOpen { get; }

    /// <summary>Opens one, with a surface of the document in it.</summary>
    /// <param name="document">The document a surface is wanted of.</param>
    /// <param name="request">Where it goes and what it is called.</param>
    /// <returns>The window, or <c>null</c> if this host cannot open one.</returns>
    IUiWindow? Open(UiDocument document, in UiWindowRequest request);

    /// <summary>Where a surface's top-left corner is on the desktop.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="x">Its left edge in device-independent desktop pixels.</param>
    /// <param name="y">Its top edge.</param>
    /// <returns>Whether the host knows — which it does not for a surface it did not place, and
    /// cannot on a platform with no window positioning.</returns>
    /// <remarks>
    ///     ⚠ <b>The one thing a control cannot work out for itself, and the reason a drag can cross
    ///     a window boundary.</b> Two surfaces have two coordinate spaces with nothing in common;
    ///     the desktop is what they have in common, and only the head that placed the windows knows
    ///     where each one starts. A host that answers <see langword="false" /> gets docking that
    ///     works within each window and refuses to drag between them, which is the honest degradation
    ///     rather than a drop landing somewhere arbitrary.
    /// </remarks>
    bool TryLocate(UiSurface surface, out float x, out float y);

    /// <summary>Which window a surface is being shown in.</summary>
    /// <param name="surface">The surface.</param>
    /// <returns>The window, or <c>null</c> if this host does not know.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The direction <see cref="Open" /> never gave back, and its absence is what left
    ///         <see cref="UiWindowTitle.Bind" /> with no caller.</b> Everything that has a window
    ///         here got it by opening one, so only the application head ever held an
    ///         <see cref="IUiWindow" /> — a component holding a <see cref="UiDocument" /> and its own
    ///         element could not name the window it was drawn in, which is the one thing a title-bar
    ///         binding needs. The host is the object that knows, because it is what opened every
    ///         window there is and was told about the one it did not open.
    ///     </para>
    ///     <para>
    ///         <c>null</c> is a real answer and stays one, exactly as it is for
    ///         <see cref="TryLocate" />: a surface nobody placed, a platform with no windowing, and a
    ///         host that only knows the windows it opened all give it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Defaulted rather than required, so an existing host still compiles.</b> A test
    ///         double that opens fake windows is not wrong to have no answer here, and answering
    ///         <c>null</c> is what it would have written.
    ///     </para>
    /// </remarks>
    IUiWindow? WindowOf(UiSurface surface) => null;
}

public sealed partial class UiDocument {
    /// <summary>What turns a surface of this document into a window, if anything can.</summary>
    /// <remarks>
    ///     Installed by the application head at boot, the way the platform itself is. Null — the
    ///     default — means this document has one window and controls that would have opened another
    ///     do their in-document thing instead.
    /// </remarks>
    public IUiWindowHost? Windows { get; set; }

    /// <summary>Whether a control may ask this document for a window of its own.</summary>
    public bool CanOpenWindows => Windows is { CanOpen: true };

    /// <summary>Which window a surface of this document is being shown in.</summary>
    /// <param name="surface">The surface, or <c>null</c>.</param>
    /// <returns>The window, or <c>null</c> if nothing here knows of one.</returns>
    /// <remarks>
    ///     The document's forward of <see cref="IUiWindowHost.WindowOf" />, and the reason it exists
    ///     is that a component has the document and does not have the host. A document with no
    ///     windowing installed answers <c>null</c> rather than throwing, in the way
    ///     <see cref="CanOpenWindows" /> does.
    /// </remarks>
    public IUiWindow? WindowOf(UiSurface? surface) => surface is null ? null : Windows?.WindowOf(surface);

    /// <summary>Which window an element is being shown in.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The window, or <c>null</c> if nothing here knows of one.</returns>
    /// <remarks>
    ///     ⚠ <b>The overload a component actually calls.</b> A control knows itself and its
    ///     <see cref="UiElement.Document" /> and nothing else — asking either of them for a window
    ///     was impossible until this — so the walk from an element to its surface
    ///     (<see cref="SurfaceOf" />) and from the surface to its window is put in one place rather
    ///     than written out at every call site that wants to name its own window.
    /// </remarks>
    public IUiWindow? WindowOf(UiElement element) => WindowOf(SurfaceOf(element));
}
