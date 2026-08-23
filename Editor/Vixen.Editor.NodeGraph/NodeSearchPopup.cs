// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.NodeGraph;

/// <summary>One offered node type.</summary>
public sealed partial class NodeSearchRow : ButtonBase {
    string category = string.Empty;
    string port = string.Empty;

    /// <inheritdoc />
    protected override string TagName => "node-search-row";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where the type sits in the create menu.</summary>
    public UiElement CategoryPart { get; private set; } = null!;

    /// <summary>The port a dragged wire would land on, when there is one.</summary>
    public UiElement PortPart { get; private set; } = null!;

    /// <summary>Which result it is showing, as an index into the popup's list.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>Where the type sits in the create menu, as text a binding can write.</summary>
    /// <remarks>
    ///     ⚠ <b>The panel ledger's shape 5, in the form a control takes it.</b> A cell's own
    ///     <c>Text</c> has no markup spelling, and the row is a control rather than a bare element —
    ///     so the escape is a property on the row that writes the cell, which is the same shape
    ///     <c>FactRow.Name</c> has and for the same reason. Kept in a field as well, because a caller
    ///     may assign it before <c>OnCreated</c> has run.
    /// </remarks>
    public string Category {
        get => category;
        set {
            category = value;

            if (CategoryPart is not null) {
                CategoryPart.Text = value;
            }
        }
    }

    /// <inheritdoc cref="Category" />
    /// <remarks>
    ///     ⚠ Empty is meaningful: <c>node-search-port:empty { display: none }</c> is what makes the
    ///     port name absent rather than blank on a popup nobody opened with a wire.
    /// </remarks>
    public string Port {
        get => port;
        set {
            port = value;

            if (PortPart is not null) {
                PortPart.Text = value;
            }
        }
    }

    /// <summary>Whether the arrows have landed on this row.</summary>
    /// <remarks>
    ///     ⚠ <b>One bit of <see cref="ElementState" />, exposed so that a binding may write it.</b>
    ///     <c>State</c> is a flag set holding Hover, Focused and Pressed beside Checked, and a
    ///     binding that assigned the whole set would undo whatever the pointer had just put there.
    ///     <c>SettingsTab.Selected</c> is the same four lines, written up under the ledger's shape 5.
    /// </remarks>
    public bool Selected {
        get => (State & ElementState.Checked) != 0;
        set {
            if (value) {
                State |= ElementState.Checked;
            } else {
                State &= ~ElementState.Checked;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        CategoryPart = Part("node-search-category");
        PortPart = Part("node-search-port");

        CategoryPart.Text = category;
        PortPart.Text = port;
    }
}

/// <summary>What the popup says when nothing matched, as an element markup can write.</summary>
/// <remarks>
///     <para>
///         The ledger's shape 5 and its four-line escape:
///         <c>&lt;node-search-empty&gt;@Text&lt;/…&gt;</c> would append a <c>text</c> child where the
///         hand-written popup set the element's own text.
///     </para>
///     <para>
///         ⚠ <b><c>internal</c>, and that is the answer to <c>CheckDocs</c> rather than an exemption
///         line.</b> A port that extracts shared parts creates types by construction, and wave 5
///         shipped six of them public with no guide page. The right question is not "what does the
///         exemption file say" but "is this surface" — and a caption whose only caller is the
///         <c>.vxml</c> beside it is not. <see cref="NodeSearchPopup.EmptyPart" /> is
///         <c>UiElement</c>, so nothing outside needs the name.
///     </para>
/// </remarks>
internal sealed class NodeSearchEmpty : UiElement {
    /// <inheritdoc />
    protected override string TagName => "node-search-empty";
}

/// <summary>One row of the popup, as the <c>@for</c> keys it.</summary>
/// <param name="Index">Where it is in the ranking, which is what a click reports.</param>
/// <param name="Title">The node type's name.</param>
/// <param name="Category">Where it sits in the create menu.</param>
/// <param name="Port">The port a dragged wire would land on, or empty.</param>
/// <remarks>
///     ⚠ <b>A projection of <see cref="NodeSearchResult" /> rather than the result itself</b>, and the
///     reason is the score: two rankings that produce the same twelve types in the same order with
///     different scores are the same twelve rows, and keying on the result would rebuild all of them.
///     ⚠ <b>And the highlight is deliberately <i>not</i> in it</b> — it is a bound property, so
///     arrowing down changes no key at all.
/// </remarks>
internal readonly record struct NodeSearchRowData(int Index, string Title, string Category, string Port);

/// <summary>
///     Create-a-node by typing, and by dragging a wire into empty space.
/// </summary>
/// <remarks>
///     <para>
///         The popup is <c>NodeSearchPopup.vxml</c>; this file is <see cref="NodeSearchRow" />, the
///         record its loop keys on, and the one-line element that lets markup write the empty
///         message's own text.
///     </para>
///     <para>
///         <b>This is the feature the UX target is judged by.</b> Doc 11 names Unity's shader graph as
///         best-in-class and names searchable creation from a dragged wire as the reason. A create
///         menu of nested submenus is what a graph editor has instead when nobody built this.
///     </para>
///     <para>
///         ⚠ <b>The field keeps the focus and the rows never take it</b>, the same arrangement
///         <c>CommandPalette</c> has and for the same reason: a popup where Down moved the focus into
///         the list is one where the next letter typed goes nowhere. The arrows move a highlight this
///         class owns.
///     </para>
///     <para>
///         <b>Filtered by the wire, not merely opened by it.</b> A wire dragged off a texture output
///         offers the node types with a texture input, and the row says which port it would land on —
///         so the gesture is one drag and one Enter rather than a drag, a menu, a guess and a second
///         drag.
///     </para>
/// </remarks>
public sealed partial class NodeSearchPopup;
