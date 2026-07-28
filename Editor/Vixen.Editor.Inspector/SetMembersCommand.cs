// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;

namespace Vixen.Editor.Inspector;

/// <summary>Setting one member to one value on every selected object.</summary>
/// <remarks>
///     <para>
///         <b>One command for the whole selection, not one per object.</b> Editing a field with
///         twenty things selected is one edit, and undoing it is one keystroke. A composite of twenty
///         commands would undo correctly and would report twenty entries in the history, which is a
///         history nobody can read.
///     </para>
///     <para>
///         <b>The old values are per object.</b> They have to be: the whole point of a mixed-value
///         edit is that the objects disagreed, and undo has to put each one back to what it held
///         rather than to a shared "before". This is the array that makes "apply to all" reversible.
///     </para>
///     <para>
///         <b>Merging is what makes a slider drag one entry.</b> Two of these merge when they set the
///         same member on the same objects in the same order, and the merged command keeps the
///         earlier one's old values — so one undo goes back to before the drag, not to the value one
///         mouse-move ago. <c>CommandStack.Seal</c> on mouse-up is what ends the run, exactly as
///         <c>EditorProperty</c> does it.
///     </para>
/// </remarks>
/// <typeparam name="TOwner">What the member belongs to.</typeparam>
/// <typeparam name="TValue">What it holds.</typeparam>
public sealed class SetMembersCommand<TOwner, TValue> : IEditorCommand where TOwner : class {
    readonly InspectorMember<TOwner, TValue> member;
    readonly TOwner[] targets;
    readonly TValue[] oldValues;
    readonly TValue newValue;
    readonly EditorDocument? document;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Describes setting a member across a selection.</summary>
    /// <param name="member">What to set.</param>
    /// <param name="targets">What to set it on.</param>
    /// <param name="oldValues">What each of them held, which undo puts back. One per target.</param>
    /// <param name="newValue">What they should all hold.</param>
    /// <param name="document">The document to mark as touched, if the objects belong to one.</param>
    /// <exception cref="ArgumentException">The two arrays are different lengths.</exception>
    public SetMembersCommand(
        InspectorMember<TOwner, TValue> member,
        TOwner[] targets,
        TValue[] oldValues,
        TValue newValue,
        EditorDocument? document = null
    ) {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(oldValues);

        if (targets.Length != oldValues.Length) {
            throw new ArgumentException(
                "Every target needs the value it held, because a mixed-value edit undoes each object "
                + "to its own previous value rather than to a shared one.",
                nameof(oldValues)
            );
        }

        this.member = member;
        this.targets = targets;
        this.oldValues = oldValues;
        this.newValue = newValue;
        this.document = document;

        Name = targets.Length > 1
            ? $"Set {member.DisplayName} ({targets.Length})"
            : $"Set {member.DisplayName}";
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var target in targets) {
            member.Set(target, newValue);
        }

        Touch(context);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < targets.Length; index++) {
            member.Set(targets[index], oldValues[index]);
        }

        Touch(context);
    }

    /// <inheritdoc />
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (!member.CoalescesEdits
            || previous is not SetMembersCommand<TOwner, TValue> earlier
            || !ReferenceEquals(earlier.member, member)
            || !SameTargets(earlier.targets, targets)) {
            return false;
        }

        merged = new SetMembersCommand<TOwner, TValue>(member, targets, earlier.oldValues, newValue, document);
        return true;
    }

    void Touch(EditorContext context) {
        if (document is not null) {
            context.Touch(document);
        }
    }

    static bool SameTargets(TOwner[] left, TOwner[] right) {
        if (left.Length != right.Length) {
            return false;
        }

        for (var index = 0; index < left.Length; index++) {
            if (!ReferenceEquals(left[index], right[index])) {
                return false;
            }
        }

        return true;
    }
}
