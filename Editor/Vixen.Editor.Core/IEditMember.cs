// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>One editable thing on an object, reached without knowing what type either of them is.</summary>
/// <remarks>
///     <para>
///         <b>The narrowest contract an <see cref="EditProperty" /> needs</b>, and deliberately
///         narrower than any of the descriptors that implement it. The inspector's
///         <c>InspectorMember</c> carries headings, ranges, conditions, asset pickers and curve
///         bounds; a graph node's port carries none of those and a settings row carries different
///         ones again. What all three have in common is a name, a type, a read, a write and the
///         command that makes the write undoable — so that is what the pipeline asks for.
///     </para>
///     <para>
///         ⚠ <b><see cref="CreateSetCommand" /> is on the member and not on the pipeline</b>, because
///         the member is the only thing that knows both the owner's type and the value's. A pipeline
///         that built the command itself would have to reach for <c>MakeGenericType</c>, which is the
///         one thing the whole descriptor layer exists to avoid — see <c>SetValuesCommand</c> for the
///         boxed fallback an implementation without typed accessors can use instead.
///     </para>
/// </remarks>
public interface IEditMember {
    /// <summary>What the member is called in source, which is what a path names it by.</summary>
    string Name { get; }

    /// <summary>What a row is labelled.</summary>
    string DisplayName { get; }

    /// <summary>What it holds.</summary>
    Type ValueType { get; }

    /// <summary>Whether it can be written at all.</summary>
    bool CanWrite { get; }

    /// <summary>Whether consecutive writes collapse into one undo step.</summary>
    /// <remarks>
    ///     On for a slider, where a drag is one edit. Off where two writes in a row are two decisions
    ///     — a dropdown, an object reference — because collapsing them takes away an undo the user is
    ///     entitled to. <c>CommandStack.Seal</c> is what ends a run either way.
    /// </remarks>
    bool CoalescesEdits { get; }

    /// <summary>Reads it, boxing a value type.</summary>
    /// <param name="owner">What to read it from.</param>
    /// <returns>Its value.</returns>
    object? Read(object owner);

    /// <summary>Writes it, unboxing a value type.</summary>
    /// <param name="owner">What to write it on.</param>
    /// <param name="value">What to write.</param>
    /// <remarks>
    ///     ⚠ <b>The un-undoable path, and the pipeline only takes it when there is no document.</b>
    ///     Everything else goes through <see cref="CreateSetCommand" />; a surface that called this
    ///     directly would edit the project and leave no way back.
    /// </remarks>
    void Write(object owner, object? value);

    /// <summary>Builds the undoable command that sets this member across a selection.</summary>
    /// <param name="targets">What to set it on.</param>
    /// <param name="value">What to set it to, boxed.</param>
    /// <param name="document">The document the objects belong to, if any.</param>
    /// <returns>The command.</returns>
    IEditorCommand CreateSetCommand(IReadOnlyList<object> targets, object? value, EditorDocument? document);
}
