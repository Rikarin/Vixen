// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>What the pointer should look like over an element.</summary>
/// <remarks>
///     <para>
///         <b>CSS's question, not the platform's.</b> This assembly cannot see
///         <c>Vixen.Platform</c>'s <c>CursorShape</c> and should not — a UI tree that knew about
///         windows would be a UI tree that could only be shown in one. So the cascade's answer stops
///         here as an intent, and whoever owns the window maps it to a stock cursor.
///     </para>
///     <para>
///         The mapping is not one to one in either direction, which is the other reason for two
///         enums. <see cref="ColumnResize" /> and <see cref="EastWest" /> are one shape on every
///         desktop and two different statements in a stylesheet; <see cref="Grabbing" /> has no
///         stock cursor at all on some platforms and falls back to <see cref="Grab" />.
///     </para>
/// </remarks>
public enum UiCursor : byte {
    /// <summary>Nothing said — the host picks, which is an arrow almost everywhere.</summary>
    Auto,

    /// <summary>The ordinary arrow, said explicitly.</summary>
    Default,

    /// <summary>Nothing at all: the pointer is hidden over this element.</summary>
    None,

    /// <summary>The pointing hand, over something clickable.</summary>
    Pointer,

    /// <summary>The I-beam, over selectable or editable text.</summary>
    Text,

    /// <summary>The four-way move.</summary>
    Move,

    /// <summary>The drop target refuses what is over it.</summary>
    NotAllowed,

    /// <summary>An open hand, over something draggable.</summary>
    Grab,

    /// <summary>A closed hand, while something is being dragged.</summary>
    Grabbing,

    /// <summary>The precision crosshair.</summary>
    Crosshair,

    /// <summary>The busy indicator, while nothing can be done.</summary>
    Wait,

    /// <summary>Busy, but the interface still responds.</summary>
    Progress,

    /// <summary>Resize a column — a vertical splitter between two panels.</summary>
    ColumnResize,

    /// <summary>Resize a row — a horizontal splitter.</summary>
    RowResize,

    /// <summary>Resize left and right, over a box's vertical edge.</summary>
    EastWest,

    /// <summary>Resize up and down, over a box's horizontal edge.</summary>
    NorthSouth
}

public sealed partial class UiDocument {
    static readonly (string Keyword, UiCursor Cursor)[] CursorKeywords = [
        ("auto", UiCursor.Auto),
        ("default", UiCursor.Default),
        ("none", UiCursor.None),
        ("pointer", UiCursor.Pointer),
        ("text", UiCursor.Text),
        ("move", UiCursor.Move),
        ("not-allowed", UiCursor.NotAllowed),
        ("grab", UiCursor.Grab),
        ("grabbing", UiCursor.Grabbing),
        ("crosshair", UiCursor.Crosshair),
        ("wait", UiCursor.Wait),
        ("progress", UiCursor.Progress),
        ("col-resize", UiCursor.ColumnResize),
        ("row-resize", UiCursor.RowResize),
        ("ew-resize", UiCursor.EastWest),
        ("ns-resize", UiCursor.NorthSouth)
    ];

    readonly Dictionary<int, UiCursor> cursors = [];
    int cursor;

    /// <summary>What the pointer should look like where it currently is.</summary>
    /// <remarks>
    ///     <para>
    ///         Read once a frame by whoever owns the window, rather than pushed from here — this
    ///         assembly has no window to push it to, and a document shown in two places at once would
    ///         have to push to both.
    ///     </para>
    ///     <para>
    ///         <b>No walk up the tree, because <c>cursor</c> inherits.</b> The hovered element's
    ///         computed style already carries whatever its nearest ancestor with a declaration said,
    ///         which is the same answer a walk would find and is one lookup rather than a loop. An
    ///         element covering its parent therefore does <i>not</i> get the parent's cursor unless it
    ///         inherited it — which is exactly the CSS rule, and is why a button inside a draggable
    ///         panel can say <c>cursor: pointer</c> and be believed.
    ///     </para>
    /// </remarks>
    public UiCursor Cursor => Hovered is { } element ? CursorOf(element.Style) : UiCursor.Auto;

    /// <summary>What a computed style asks the pointer to look like.</summary>
    /// <param name="style">The style, from <see cref="UiElement.Style" />.</param>
    /// <returns>The cursor, or <see cref="UiCursor.Auto" /> if it says nothing this knows.</returns>
    /// <remarks>
    ///     An unrecognised keyword reads as <see cref="UiCursor.Auto" /> rather than as nothing,
    ///     because <c>cursor: url(hand.png)</c> is valid CSS this engine has no reading of, and the
    ///     host's default is a better answer to it than the ancestor's cursor would be.
    /// </remarks>
    public UiCursor CursorOf(ComputedStyle style) =>
        style.TryGet(cursor, out var id) && cursors.TryGetValue(id, out var shape) ? shape : UiCursor.Auto;

    void InternCursors() {
        cursor = Styles.Properties.Intern("cursor");

        foreach (var (keyword, shape) in CursorKeywords) {
            cursors[Styles.Values.Intern(keyword)] = shape;
        }
    }
}
