// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Core;

/// <summary>Setting one member to one value on every selected object, through boxed accessors.</summary>
/// <remarks>
///     <para>
///         <b>What an <see cref="IEditMember" /> without typed accessors returns from
///         <see cref="IEditMember.CreateSetCommand" />.</b> The inspector's generated members build a
///         typed command instead, because they can reach a <c>struct</c> field by reference and this
///         cannot. Everything else about the two is the same, and this is the one a graph port, a
///         settings row or a plugin's own member gets for free rather than writing.
///     </para>
///     <para>
///         <b>One command for the whole selection, not one per object.</b> Editing a field with
///         twenty things selected is one edit and undoing it is one keystroke. A composite of twenty
///         commands would undo correctly and would report twenty entries in a history nobody can read.
///     </para>
///     <para>
///         <b>The old values are per object.</b> They have to be: the whole point of a mixed-value
///         edit is that the objects disagreed, and undo has to put each one back to what it held
///         rather than to a shared "before".
///     </para>
/// </remarks>
public sealed class SetValuesCommand : IEditorCommand {
    readonly IEditMember member;
    readonly object[] targets;
    readonly object?[] oldValues;
    readonly object? newValue;
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
    public SetValuesCommand(
        IEditMember member,
        IReadOnlyList<object> targets,
        IReadOnlyList<object?> oldValues,
        object? newValue,
        EditorDocument? document = null
    ) {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(oldValues);

        if (targets.Count != oldValues.Count) {
            throw new ArgumentException(
                "Every target needs the value it held, because a mixed-value edit undoes each object "
                + "to its own previous value rather than to a shared one.",
                nameof(oldValues)
            );
        }

        this.member = member;
        this.targets = [.. targets];
        this.oldValues = [.. oldValues];
        this.newValue = newValue;
        this.document = document;

        Name = targets.Count > 1
            ? $"Set {member.DisplayName} ({targets.Count})"
            : $"Set {member.DisplayName}";
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var target in targets) {
            member.Write(target, newValue);
        }

        Touch(context);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < targets.Length; index++) {
            member.Write(targets[index], oldValues[index]);
        }

        Touch(context);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The merged command keeps the earlier one's old values, so however many mouse-moves
    ///     collapsed into it, one undo goes back to before the drag rather than to the value one
    ///     frame ago.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (!member.CoalescesEdits
            || previous is not SetValuesCommand earlier
            || !ReferenceEquals(earlier.member, member)
            || !SameTargets(earlier.targets, targets)) {
            return false;
        }

        merged = new SetValuesCommand(member, targets, earlier.oldValues, newValue, document);
        return true;
    }

    void Touch(EditorContext context) {
        if (document is not null) {
            context.Touch(document);
        }
    }

    static bool SameTargets(object[] left, object[] right) {
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
