// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.App;

/// <summary>What a line's chevron says, if it says anything.</summary>
/// <remarks>
///     ⚠ <b>One value rather than two flags, because two of the four combinations are not
///     states.</b> A line either does something, opens a category or goes back out of one, and
///     "no arrow but pointing left" is a row nothing can produce.
/// </remarks>
enum RowArrow {
    /// <summary>A line that adds something, which has no arrow at all.</summary>
    None,

    /// <summary>A category to go into.</summary>
    Into,

    /// <summary>The line back out to the top level.</summary>
    Out
}

/// <summary>One line of the Add Component picker: a category to open, or a thing to add.</summary>
/// <remarks>
///     <para>
///         A <see cref="ButtonBase" /> rather than a <c>MenuItem</c>, for <c>PaletteRow</c>'s reason:
///         the rows must not take the focus, because the field above them has it and every keystroke
///         belongs to the query. What they carry beyond a label is the arrow that says a line opens
///         something rather than doing something, and the quiet word on the right that says which
///         kind of thing it is.
///     </para>
///     <para>
///         ⚠ <b>Three of its members exist so that markup can write parts it owns.</b> The panel
///         ledger's shape 5: a cell's own <c>Text</c> has no markup spelling, an
///         <see cref="ElementState" /> is a flag set a binding may not assign whole, and an icon's
///         visibility is two writes rather than one. <see cref="Detail" />, <see cref="Selected" />
///         and <see cref="Opening" /> are the properties a binding can reach, and each is the same
///         four lines <c>NodeSearchRow</c> and <c>SettingsTab</c> already carry.
///     </para>
/// </remarks>
sealed partial class AddComponentRow : ButtonBase {
    string detail = string.Empty;
    RowArrow opening;

    /// <inheritdoc />
    protected override string TagName => "add-component-row";

    /// <summary>The word at the right: a category, or "Script".</summary>
    public UiElement DetailPart { get; private set; } = null!;

    /// <summary>The chevron shown on a line that opens a category.</summary>
    public Icon Arrow { get; private set; } = null!;

    /// <summary>Which line it is, as an index into what the picker is showing.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>What the right-hand cell says, as text a binding can write.</summary>
    /// <remarks>
    ///     Kept in a field as well, because a caller may assign it before <c>OnCreated</c> has run.
    /// </remarks>
    public string Detail {
        get => detail;
        set {
            detail = value;

            if (DetailPart is not null) {
                DetailPart.Text = value;
            }
        }
    }

    /// <summary>Whether the arrows have landed on this row.</summary>
    /// <remarks>
    ///     ⚠ <b>One bit of <see cref="ElementState" />, exposed so that a binding may write it.</b>
    ///     <c>State</c> also holds Hover, Focused and Pressed, and a binding that assigned the whole
    ///     set would undo whatever the pointer had just put there.
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

    /// <summary>Whether the line opens something, and which way.</summary>
    /// <remarks>
    ///     ⚠ <b>The arrow is turned rather than swapped for another glyph, and it is turned here
    ///     rather than by a rule</b>: a transform is not something this layout applies, so "the same
    ///     arrow, the other way" has to be the other chevron. The display is written inline for the
    ///     same reason the hand-written picker wrote it inline — <c>add-component-row &gt; icon</c>
    ///     has a size and no visibility, and a class for it would be a second place to decide.
    /// </remarks>
    public RowArrow Opening {
        get => opening;
        set {
            opening = value;

            if (Arrow is not null) {
                Restate();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        DetailPart = Part("add-component-detail");
        DetailPart.Text = detail;

        Arrow = Part<Icon>();
        Arrow.Geometry = ControlIcons.ChevronRight;

        Restate();
    }

    void Restate() {
        Arrow.SetStyle("display", opening == RowArrow.None ? "none" : "flex");
        Arrow.Geometry = opening == RowArrow.Out ? ControlIcons.ChevronLeft : ControlIcons.ChevronRight;
    }
}

/// <summary>What the picker says when a query matches nothing.</summary>
/// <remarks>
///     ⚠ <b>A four-line subclass rather than an interpolation</b>, which is the panel ledger's shape
///     5: <c>&lt;add-component-empty&gt;@Text&lt;/…&gt;</c> would append a <c>text</c> child where
///     the hand-written picker set the element's own text, and that is a box of its own inside a
///     padded cell. <c>internal</c>, because <see cref="AddComponentMenu.EmptyPart" /> is a
///     <see cref="UiElement" /> and nothing outside needs the name.
/// </remarks>
sealed class AddComponentEmpty : UiElement {
    /// <inheritdoc />
    protected override string TagName => "add-component-empty";
}
