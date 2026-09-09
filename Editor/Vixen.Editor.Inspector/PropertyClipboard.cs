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

        // ⚠ A copy rather than the object itself, for an owned type. The clipboard outlives the
        // selection it was taken from, so holding the live instance would let an edit made after the
        // copy travel into the paste — "copy, change your mind, paste" would paste the change.
        Value = OwnedValues.Copy(field.Member.MemberType, value);
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

        var changed = Write(field);
        field.Seal();

        return changed;
    }

    /// <summary>Puts the clipboard's value on every object the field reaches.</summary>
    /// <remarks>
    ///     ⚠ <b>One copy per object for an owned type, and one <c>EditProperty.Write</c>
    ///     would have been an aliasing bug rather than a preference.</b> A single write puts the
    ///     <i>same instance</i> on every selected object, so editing any one of them afterwards
    ///     silently edits all of them — and the clipboard is holding that instance too. Twenty
    ///     distinct copies is the only reading of "paste this into all of them" that survives the
    ///     next edit. For anything else — a value type, a string, a reference to an asset — the
    ///     instance <i>is</i> the value and <c>OwnedValues.Copy</c> hands it straight back, so this
    ///     is the same write it always was.
    /// </remarks>
    bool Write(InspectorField field) {
        if (!OwnedValues.IsOwned(field.Member.MemberType)) {
            return field.Write(Value);
        }

        var written = new object?[field.Objects.Count];

        for (var index = 0; index < written.Length; index++) {
            written[index] = OwnedValues.Copy(field.Member.MemberType, Value);
        }

        return field.WriteEach(written);
    }

    /// <summary>Forgets what was copied.</summary>
    public void Clear() {
        Value = null;
        ValueType = null;
        SourceName = null;
    }
}
