// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;

namespace Vixen.Editor.SceneView;

/// <summary>One gizmo drag, as something the undo stack can take back.</summary>
/// <remarks>
///     <para>
///         <b>Recorded on mouse-up, from what the gizmo captured on mouse-down.</b> A command per
///         frame of the drag would work — they would merge — but it would mean allocating and
///         executing three hundred commands to move one crate, and every one of them would re-apply
///         a transform the gizmo had already applied. The gizmo owns the live manipulation and this
///         owns the history, which is the same division <c>CommandTransaction</c> makes for a drag
///         somebody else is driving.
///     </para>
///     <para>
///         <b>Position, rotation and scale together, per target.</b> A rotate about a group's centre
///         moves every object as well as turning it, and three separate commands for one drag would
///         be three undo steps that only make sense applied together.
///     </para>
/// </remarks>
public sealed class TransformTargetsCommand : IEditorCommand {
    readonly IGizmoTarget[] targets;
    readonly (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] before;
    readonly (Vector3 Position, Quaternion Rotation, Vector3 Scale)[] after;
    readonly EditorDocument? document;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Describes a drag that has already happened.</summary>
    /// <param name="name">What it is called in the history — "Move", "Rotate", "Scale".</param>
    /// <param name="targets">What was dragged.</param>
    /// <param name="before">What each held before, one per target.</param>
    /// <param name="document">The document to mark as touched.</param>
    /// <exception cref="ArgumentException">The two arrays are different lengths.</exception>
    public TransformTargetsCommand(
        string name,
        IReadOnlyList<IGizmoTarget> targets,
        IReadOnlyList<(Vector3 Position, Quaternion Rotation, Vector3 Scale)> before,
        EditorDocument? document = null
    ) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(before);

        if (targets.Count != before.Count) {
            throw new ArgumentException(
                "Every target needs the transform it held, because a drag of several objects undoes "
                + "each to its own previous transform rather than to a shared one.",
                nameof(before)
            );
        }

        this.targets = [.. targets];
        this.before = [.. before];
        this.document = document;

        // Read *now*, because the gizmo has already applied the drag. Reading them in Do would give
        // whatever the objects hold at redo time, which after an undo is the before state — and the
        // redo would then be a no-op that looks like the command was lost.
        after = new (Vector3, Quaternion, Vector3)[targets.Count];

        for (var index = 0; index < targets.Count; index++) {
            after[index] = (targets[index].Position, targets[index].Rotation, targets[index].Scale);
        }

        Name = targets.Count > 1 ? $"{name} ({targets.Count})" : name;
    }

    /// <summary>Whether the drag changed anything at all.</summary>
    /// <remarks>
    ///     A click on a handle that did not move is not an undo entry. Checked by the caller rather
    ///     than by the constructor throwing, because "the user pressed and released" is an ordinary
    ///     thing to happen and not an error.
    /// </remarks>
    public bool IsEmpty {
        get {
            for (var index = 0; index < before.Length; index++) {
                if (before[index] != after[index]) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <inheritdoc />
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
    /// <remarks>
    ///     Two drags of the same objects do not merge. Each is a complete gesture that ended when the
    ///     button came up, and collapsing two of them would take away an undo the user is entitled to
    ///     — the same reasoning <c>EditorProperty.CoalescesEdits</c> gives for a dropdown.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    void Apply((Vector3 Position, Quaternion Rotation, Vector3 Scale)[] states) {
        for (var index = 0; index < targets.Length; index++) {
            targets[index].Position = states[index].Position;
            targets[index].Rotation = states[index].Rotation;
            targets[index].Scale = states[index].Scale;
        }
    }

    void Touch(EditorContext context) {
        if (document is not null) {
            context.Touch(document);
        }
    }
}
