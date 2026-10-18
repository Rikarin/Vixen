// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Core;

/// <summary>A change somebody already applied, as one undo entry: each object's before and after.</summary>
/// <remarks>
///     <para>
///         <b>What <see cref="IEditMember.CreateRecordCommand" /> returns by default</b> (doc 50 § P5,
///         #1291). <see cref="SetValuesCommand" /> is one value written to N objects and reads what
///         they held when it is built; this is N values, both halves handed over, because the surface
///         that applied the change is the only thing that still knows what was there before.
///     </para>
///     <para>
///         ⚠ <b>One entry for N objects, never N entries.</b> Recording a three-object drag as three
///         per-object commands under a composite would be the shape doc 36 refused — and a per-frame
///         command per mouse-move, coalesced afterwards, is the other shape it refused.
///     </para>
///     <para>
///         ⚠ <b>Never merges.</b> A recorded change is a finished one; folding it into the edit before
///         it would undo two drags at once.
///     </para>
/// </remarks>
public sealed class RecordedValuesCommand : IEditorCommand {
    readonly IEditMember member;
    readonly object[] targets;
    readonly object?[] before;
    readonly object?[] after;
    readonly EditorDocument? document;

    /// <summary>Records a change already applied.</summary>
    /// <param name="member">The member that changed.</param>
    /// <param name="targets">What it changed on.</param>
    /// <param name="before">What each target held before.</param>
    /// <param name="after">What each target holds now.</param>
    /// <param name="name">What the history calls it; <c> (N)</c> is added for several targets.</param>
    /// <param name="document">The document the objects belong to, if any.</param>
    /// <exception cref="ArgumentException">The three lists are not the same length.</exception>
    public RecordedValuesCommand(
        IEditMember member,
        IReadOnlyList<object> targets,
        IReadOnlyList<object?> before,
        IReadOnlyList<object?> after,
        string name,
        EditorDocument? document = null
    ) {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (before.Count != targets.Count || after.Count != targets.Count) {
            throw new ArgumentException(
                "Every target needs the value it held and the value it holds, because a change to "
                + "several objects undoes each to its own previous value rather than to a shared one.",
                nameof(before)
            );
        }

        this.member = member;
        this.targets = [.. targets];
        this.before = [.. before];
        this.after = [.. after];
        this.document = document;

        Name = targets.Count > 1 ? $"{name} ({targets.Count})" : name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Writes the after-state, which on the first execution the objects already hold — the write
    ///     is what makes redo work, and it costs nothing the first time.
    /// </remarks>
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(after);
        Touch(context);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        Apply(before);
        Touch(context);
    }

    /// <inheritdoc />
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    void Apply(object?[] values) {
        for (var index = 0; index < targets.Length; index++) {
            member.Write(targets[index], values[index]);
        }
    }

    void Touch(EditorContext context) {
        if (document is not null) {
            context.Touch(document);
        }
    }
}
