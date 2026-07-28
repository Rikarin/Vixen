// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Core;

/// <summary>Setting one property to one value.</summary>
/// <typeparam name="T">What the property holds.</typeparam>
/// <remarks>
///     The command nearly every edit in the editor turns out to be, and the one that merges: a drag
///     across a slider produces one of these per mouse-move and they collapse into a single entry
///     that undoes to where the drag began. Public rather than internal because a cross-document
///     operation builds these against documents other than its own.
/// </remarks>
public sealed class SetPropertyCommand<T> : IEditorCommand {
    readonly T newValue;
    readonly T oldValue;
    readonly EditorProperty<T> property;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Describes setting a property.</summary>
    /// <param name="property">What to set.</param>
    /// <param name="oldValue">What it held, which undo puts back.</param>
    /// <param name="newValue">What it should hold.</param>
    public SetPropertyCommand(EditorProperty<T> property, T oldValue, T newValue) {
        ArgumentNullException.ThrowIfNull(property);

        this.property = property;
        this.oldValue = oldValue;
        this.newValue = newValue;
        Name = "Set " + property.Name;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        property.Assign(newValue);
        Touch(context);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        property.Assign(oldValue);
        Touch(context);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The merged command keeps <paramref name="previous" />'s old value, so however many
    ///     mouse-moves collapsed into it, one undo goes back to before the drag rather than to the
    ///     value one frame ago.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        if (property.CoalescesEdits
            && previous is SetPropertyCommand<T> earlier
            && ReferenceEquals(earlier.property, property)) {
            merged = new SetPropertyCommand<T>(property, earlier.oldValue, newValue);
            return true;
        }

        merged = null;
        return false;
    }

    void Touch(EditorContext context) {
        if (property.Owner.Document is { } document) {
            context.Touch(document);
        }
    }
}
