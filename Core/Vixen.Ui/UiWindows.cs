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
}
