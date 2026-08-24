// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Platform.Ui;

/// <summary>Turns what the cascade says the pointer should look like into what the window shows.</summary>
/// <remarks>
///     <para>
///         <b>The other half of <see cref="PlatformInput" />, and it was missing.</b>
///         <c>UiDocument.Cursor</c> resolved <c>cursor: pointer</c> correctly from the day it was
///         written and nothing ever read it — so every <c>cursor-*</c> utility class in every theme
///         scored <i>works</i> against a probe that asked the document, and changed nothing a user
///         could see. <c>cursor-pointer</c> was in exactly the same position as <c>cursor-help</c>.
///     </para>
///     <para>
///         ⚠ <b>Not gated on <see cref="PlatformCapabilities.Cursor" />, deliberately.</b> The flag
///         says the pointer can be hidden, confined or put into relative mode; drawing a stock
///         cursor is <see cref="IWindow.CursorShape" />, which every window implements and which the
///         platforms with no pointer at all — iOS, Android — implement as a setter that does
///         nothing and a getter that answers <see cref="CursorShape.Arrow" />. Gating would also
///         make this untestable, because the only platform a test can open a window on is the
///         headless one and it advertises <see cref="PlatformCapabilities.MultiWindow" /> and
///         nothing else — a wire whose one gate is a flag no test can set is a wire nobody would
///         ever find out had stopped working.
///     </para>
///     <para>
///         ⚠ <b><see cref="CursorMode" /> is touched only to hide and unhide, and only from
///         <see cref="CursorMode.Normal" />.</b> <c>cursor: none</c> has to be able to do something
///         or it is another silent no-op, and hiding is the only way to say it. But a game that put
///         the window into <see cref="CursorMode.Relative" /> for mouse-look has an interface drawn
///         over the top of it, and a stylesheet that dragged the pointer back out of relative mode
///         between frames would be a camera that stops turning.
///     </para>
/// </remarks>
public static class PlatformCursor {
    /// <summary>What a stylesheet's answer looks like as a stock cursor.</summary>
    /// <param name="cursor">The cursor the cascade resolved.</param>
    /// <returns>The nearest stock shape.</returns>
    /// <remarks>
    ///     ⚠ <b>Not one to one, which is why there are two enums.</b>
    ///     <see cref="UiCursor.ColumnResize" /> and <see cref="UiCursor.EastWest" /> are two
    ///     statements in a stylesheet and one shape on every desktop; <see cref="UiCursor.Grab" />,
    ///     <see cref="UiCursor.Progress" /> and <see cref="UiCursor.Help" /> have no stock cursor in
    ///     <see cref="CursorShape" /> at all and fall back to the nearest thing that does — which is
    ///     what a browser does on the platforms where they are missing.
    /// </remarks>
    public static CursorShape ToShape(UiCursor cursor) =>
        cursor switch {
            UiCursor.Pointer or UiCursor.Grab or UiCursor.Grabbing => CursorShape.Hand,
            UiCursor.Text => CursorShape.TextBeam,
            UiCursor.Move => CursorShape.ResizeAll,
            UiCursor.NotAllowed => CursorShape.NotAllowed,
            UiCursor.Crosshair => CursorShape.Crosshair,
            UiCursor.Wait or UiCursor.Progress => CursorShape.Wait,
            UiCursor.ColumnResize or UiCursor.EastWest => CursorShape.ResizeHorizontal,
            UiCursor.RowResize or UiCursor.NorthSouth => CursorShape.ResizeVertical,

            // `auto`, `default`, `help`, and `none` — which is a mode rather than a shape, and whose
            // shape is what comes back when it stops being hidden.
            _ => CursorShape.Arrow
        };

    /// <summary>Tells the window under the pointer what the cascade says the pointer looks like.</summary>
    /// <param name="host">The host that knows which window shows which surface.</param>
    /// <returns>The window that was told, or <see langword="null" /> if the pointer is over none.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Called once a frame, after the update that resolved the styles.</b> The cursor
    ///         follows the pointer and the pointer moves between frames, so there is no event to
    ///         hang this on that is not "the frame" — and the window's own setters already ignore a
    ///         write of the value they hold, so a still frame costs one comparison.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The window the hovered element is in, not the main one.</b> A document can be
    ///         shown in several windows and only one of them has the pointer over it; writing the
    ///         cursor to the main window would give a torn-off panel the main window's pointer and
    ///         the main window a pointer for something nobody is over.
    ///     </para>
    /// </remarks>
    public static IWindow? Apply(PlatformWindowHost host) {
        ArgumentNullException.ThrowIfNull(host);

        var document = host.Document;

        if (document.Hovered is not { } hovered
            || document.SurfaceOf(hovered) is not { } surface
            || !host.TryWindow(surface, out var window)) {
            return null;
        }

        var cursor = document.Cursor;

        window.CursorShape = ToShape(cursor);

        // ⚠ Read back rather than remembered. Whoever else owns the mode — a game in mouse-look, a
        // window that confined the pointer — is entitled to change it without telling this, and a
        // cached "what we last set" would fight them for it every frame.
        var mode = window.CursorMode;

        if (cursor == UiCursor.None && mode == CursorMode.Normal) {
            window.CursorMode = CursorMode.Hidden;
        } else if (cursor != UiCursor.None && mode == CursorMode.Hidden) {
            window.CursorMode = CursorMode.Normal;
        }

        return window;
    }
}
