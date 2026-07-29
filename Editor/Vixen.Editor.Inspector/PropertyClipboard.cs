// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Inspector;

/// <summary>The one value copy-property put there, and what it will paste into.</summary>
/// <remarks>
///     <para>
///         <b>The type is carried with the value, and paste checks it.</b> The interesting case is
///         copying a <c>Vector3</c> position and pasting it into a <c>Vector3</c> scale, which is a
///         thing people do on purpose and which works. Pasting a <c>float</c> into an <c>int</c> is
///         refused rather than truncated, because a silent conversion is how a rotation ends up
///         quantised to whole degrees with nothing said.
///     </para>
///     <para>
///         <b>Deliberately not the system clipboard.</b> A property value is not text, round-tripping
///         it through one would need a serialisation format that means something outside this
///         process, and the operation people want is "put this number on that other object" — which
///         never leaves the editor. Copying a property's value <i>as text</i> is a different command
///         and belongs to the platform layer.
///     </para>
/// </remarks>
public sealed class PropertyClipboard {
    /// <summary>The clipboard the inspector uses unless it is handed another.</summary>
    public static PropertyClipboard Default { get; } = new();

    /// <summary>What was copied, if anything.</summary>
    public object? Value { get; private set; }

    /// <summary>What type it was copied from.</summary>
    public Type? ValueType { get; private set; }

    /// <summary>Where it came from, for the menu item's label.</summary>
    public string? SourceName { get; private set; }

    /// <summary>Whether anything has been copied.</summary>
    public bool HasValue => ValueType is not null;

    /// <summary>Takes a copy of a field's value.</summary>
    /// <param name="field">The field.</param>
    /// <returns>Whether there was one value to take — a mixed field has none.</returns>
    /// <remarks>
    ///     ⚠ <b>A mixed field copies nothing.</b> There is no value to copy: the objects disagree,
    ///     and taking the primary one's would make "copy, select something else, paste" quietly
    ///     propagate a value the user never saw as the answer.
    /// </remarks>
    public bool Copy(InspectorField field) {
        ArgumentNullException.ThrowIfNull(field);

        var (value, mixed) = field.Read();

        if (mixed) {
            return false;
        }

        Value = value;
        ValueType = field.Member.MemberType;
        SourceName = field.Member.DisplayName;

        return true;
    }

    /// <summary>Whether what is on the clipboard would go into a field.</summary>
    /// <param name="field">The field.</param>
    /// <returns>Whether paste would do anything.</returns>
    public bool CanPaste(InspectorField field) {
        ArgumentNullException.ThrowIfNull(field);

        return ValueType is not null && field.CanWrite && field.Member.MemberType == ValueType;
    }

    /// <summary>Writes what was copied into a field.</summary>
    /// <param name="field">The field.</param>
    /// <returns>Whether anything changed.</returns>
    public bool Paste(InspectorField field) {
        ArgumentNullException.ThrowIfNull(field);

        if (!CanPaste(field)) {
            return false;
        }

        var changed = field.Write(Value);
        field.Seal();

        return changed;
    }

    /// <summary>Forgets what was copied.</summary>
    public void Clear() {
        Value = null;
        ValueType = null;
        SourceName = null;
    }
}
