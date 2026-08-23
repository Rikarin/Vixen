// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>One row of the palette.</summary>
public sealed partial class PaletteRow : ButtonBase {
    string category = string.Empty;
    string detail = string.Empty;

    /// <inheritdoc />
    protected override string TagName => "palette-row";

    /// <summary>Where this row's result came from.</summary>
    public UiElement CategoryPart { get; private set; } = null!;

    /// <summary>The shortcut or path on the right, if there is one.</summary>
    public UiElement DetailPart { get; private set; } = null!;

    /// <summary>Which result it is showing, as an index into the palette's list.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>Where this row's result came from, as text a binding can write.</summary>
    /// <remarks>
    ///     ⚠ <b>The panel ledger's shape 5, in the form a control takes it.</b> A cell's own
    ///     <c>Text</c> has no markup spelling, and this row is a control rather than a bare element —
    ///     so the escape is a property on the row that writes the cell, the same shape
    ///     <c>FactRow.Name</c> has. Kept in a field as well, because a caller may assign it before
    ///     <c>OnCreated</c> has built the cell.
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
    public string Detail {
        get => detail;
        set {
            detail = value;

            if (DetailPart is not null) {
                DetailPart.Text = value;
            }
        }
    }

    /// <summary>Whether this is the row Enter would run.</summary>
    /// <remarks>
    ///     ⚠ <b>One bit of <see cref="ElementState" />, exposed so that a binding may write it.</b>
    ///     <c>State</c> is a flag set holding Hover, Focused and Pressed beside Checked, and a
    ///     binding that assigned the whole set would undo whatever the pointer had just put there.
    ///     <c>SettingsTab.Selected</c> is the same four lines, written up under the ledger's shape 5.
    ///     ⚠ And it is a <i>property</i> rather than part of the row's key precisely so that arrowing
    ///     down rebuilds nothing: the binding is an effect inside the row's own region.
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

        CategoryPart = Part("palette-category");
        DetailPart = Part("palette-detail");

        CategoryPart.Text = category;
        DetailPart.Text = detail;
    }
}

/// <summary>The pane under the rows, as an element markup can write the text of.</summary>
/// <remarks>
///     <para>
///         The ledger's shape 5 and its four-line escape. <c>palette-empty</c> needs none — its text
///         is written once from <c>OnComposed</c>, where the C# spelling is available and is what the
///         localisation table wants.
///     </para>
///     <para>
///         ⚠ <b><c>internal</c>, and that is the answer to <c>CheckDocs</c> rather than an exemption
///         line.</b> See <c>NodeSearchEmpty</c>: a caption whose only caller is the <c>.vxml</c>
///         beside it is not public surface, and <see cref="CommandPalette.PreviewPart" /> is
///         <c>UiElement</c>.
///     </para>
/// </remarks>
internal sealed class PalettePreview : UiElement {
    /// <inheritdoc />
    protected override string TagName => "palette-preview";
}

/// <summary>One row of the palette, as the <c>@for</c> keys it.</summary>
/// <param name="Index">Where it is in the ranking, which is what a click reports.</param>
/// <param name="Title">What the row says.</param>
/// <param name="Category">Where the result came from.</param>
/// <param name="Detail">The shortcut or path on the right, or empty.</param>
/// <remarks>
///     ⚠ <b>A projection of <see cref="PaletteItem" /> rather than the item</b>, because an item
///     carries a score and a <c>Run</c> delegate: two queries producing the same visible list would
///     be different keys and would rebuild every row for nothing. What the row draws is its
///     identity, and the slot is in it because two sources may legitimately offer the same words.
/// </remarks>
internal readonly record struct PaletteRowData(int Index, string Title, string Category, string Detail);

/// <summary>Fuzzy search over everything the editor can do, reached with one chord.</summary>
/// <remarks>
///     <para>
///         The palette is <c>CommandPalette.vxml</c>; this file is <see cref="PaletteRow" />, the
///         record its loop keys on, and the one-line element for the preview pane's own text.
///     </para>
///     <para>
///         <b>Cheap to build on the registry, and the feature power users judge tooling by</b> —
///         doc 11's words, and the reason it is in the first version of the shell rather than a
///         later one. Nothing here knows what a command is beyond
///         <see cref="IPaletteSource" />: commands, assets, scene objects and settings all arrive
///         the same way.
///     </para>
///     <para>
///         ⚠ <b>The field keeps the focus and the rows never take it.</b> A palette where Down moved
///         the focus into the list is one where the next letter typed goes nowhere — so the arrows
///         move a highlight this class owns, and the rows are not focusable at all. It is the
///         opposite arrangement to <see cref="Menu" />, whose items <i>are</i> the focus, and for the
///         opposite reason: a menu has no text field to protect.
///     </para>
///     <para>
///         ⚠ <b>Rows are rebuilt per query and capped.</b> Ten results is what fits and what anybody
///         reads; a palette that realised a thousand rows to show ten would be the tree view's
///         virtualisation problem without the tree view's payoff. ⚠ <b>They are no longer
///         <i>pooled</i>, which wave 6 deleted</b> — a keyed <c>@for</c> is the pool, and
///         <c>palette-row.parked</c> is now reached by nothing in this assembly.
///     </para>
/// </remarks>
public sealed partial class CommandPalette;
