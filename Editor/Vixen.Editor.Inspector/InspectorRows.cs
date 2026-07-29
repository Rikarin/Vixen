// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector;

/// <summary>Builds one member's row: the label, the drawer's editor, and the reset button.</summary>
/// <remarks>
///     <para>
///         <b>Shared because there are now three callers rather than one.</b>
///         <see cref="InspectorView" /> builds the rows of the inspected type; a nested-object drawer
///         builds the rows of a member's own type; a list drawer builds one per element. All three
///         want the same row — a label, a tooltip, an editor from whichever drawer resolved, a reset
///         button that appears only when there is something to reset to — and three copies of it is
///         how a fix to one of them stops applying to the other two.
///     </para>
///     <para>
///         ⚠ <b>The two marks are different questions and both are here.</b> "Differs from the type's
///         default" is what the reset button answers; "differs from the prefab this came from" is
///         what the override bar answers, and an object can be either without being the other.
///     </para>
/// </remarks>
public static class InspectorRows {
    /// <summary>Builds a row for a member, if anything can draw it and it is visible at all.</summary>
    /// <param name="parent">Where the row goes.</param>
    /// <param name="field">The member, bound to what is being inspected.</param>
    /// <param name="drawers">Which drawer edits which member.</param>
    /// <param name="built">Told about the row once it exists, before it is filled in.</param>
    /// <returns>The row, or <see langword="null" /> when the member is hidden or undrawable.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="built" /> runs before <see cref="Show" />, deliberately.</b> It is
    ///     where the caller subscribes to the field's <c>Changed</c>, and a subscription made
    ///     afterwards would miss a drawer that wrote something while being filled in for the first
    ///     time.
    /// </remarks>
    public static InspectorRow? Add(
        UiElement parent,
        InspectorField field,
        DrawerRegistry drawers,
        Action<InspectorRow>? built = null
    ) {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(drawers);

        if (!field.IsVisible || drawers.Resolve(field.Member) is not { } drawer) {
            return null;
        }

        var row = parent.Add<InspectorRow>();

        row.Field = field;
        row.Drawer = drawer;
        row.Label.Text = field.Member.DisplayName;

        if (field.Member.Tooltip is { } text) {
            // Attached to the label rather than the row, so hovering the editor does not cover the
            // thing being edited with a description of it.
            var tooltip = row.Add<Tooltip>();

            tooltip.Label = text;
            tooltip.Attach(row.Label);
        }

        built?.Invoke(row);

        row.Editor = drawer.Build(field, row.Slot);
        Show(row);

        return row;
    }

    /// <summary>Reads a row's editor back off the objects.</summary>
    /// <param name="row">The row.</param>
    /// <remarks>
    ///     ⚠ <b>Inside <see cref="InspectorField.Refreshing" />.</b> Putting a value into a control
    ///     raises the control's own changed event, and for a <i>mixed</i> field the neutral position
    ///     it parks at would be written to every selected object the moment the row was drawn.
    /// </remarks>
    public static void Show(InspectorRow row) {
        ArgumentNullException.ThrowIfNull(row);

        using (row.Field.Refreshing()) {
            row.Drawer.Show(row.Field, row.Editor);
        }

        Restate(row);
    }

    /// <summary>Shows the reset button only when there is something to reset to.</summary>
    /// <param name="row">The row.</param>
    public static void Restate(InspectorRow row) {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Field.CanReset && row.Field.IsModified) {
            row.Reset.RemoveClass("hidden");
        } else {
            row.Reset.AddClass("hidden");
        }

        if (row.Field.IsOverridden) {
            row.AddClass("overridden");
        } else {
            row.RemoveClass("overridden");
        }
    }
}
