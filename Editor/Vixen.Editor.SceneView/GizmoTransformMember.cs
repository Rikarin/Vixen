// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;

namespace Vixen.Editor.SceneView;

/// <summary>A gizmo target's position, rotation and scale, as the one member the edit pipeline records.</summary>
/// <remarks>
///     <para>
///         <b>What puts the gizmo on <see cref="EditProperty" /></b> (doc 50 § P5, #1291). A drag is
///         one edit of three values on N targets, and the pipeline's unit is a member over a
///         selection — so the three are one member, valued as the tuple
///         <see cref="GizmoDrag.Captured" /> already holds.
///     </para>
///     <para>
///         ⚠ <b>It records with <see cref="TransformTargetsCommand" />, not the default.</b> That command
///         is the N-target shape the gizmo has always recorded with — one entry named <c>Move (3)</c> —
///         and reads the after-state from the targets itself, which by the time it is built is what
///         <c>after</c> says.
///     </para>
/// </remarks>
sealed class GizmoTransformMember : IEditMember {
    /// <summary>The one instance; the member has no state.</summary>
    public static GizmoTransformMember Instance { get; } = new();

    GizmoTransformMember() { }

    /// <inheritdoc />
    public string Name => "Transform";

    /// <inheritdoc />
    public string DisplayName => "Transform";

    /// <inheritdoc />
    public Type ValueType => typeof((Vector3 Position, Quaternion Rotation, Vector3 Scale));

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    /// <remarks>A drag is recorded once, on release, so there is nothing to coalesce.</remarks>
    public bool CoalescesEdits => false;

    /// <summary>A target's state, as the value this member reads.</summary>
    /// <param name="target">The target.</param>
    /// <returns>Its position, rotation and scale.</returns>
    public static (Vector3 Position, Quaternion Rotation, Vector3 Scale) State(IGizmoTarget target) =>
        (target.Position, target.Rotation, target.Scale);

    /// <inheritdoc />
    public object? Read(object owner) => State(Target(owner));

    /// <inheritdoc />
    public void Write(object owner, object? value) {
        var target = Target(owner);
        var (position, rotation, scale) = ((Vector3, Quaternion, Vector3))value!;

        target.Position = position;
        target.Rotation = rotation;
        target.Scale = scale;
    }

    /// <inheritdoc />
    public IEditorCommand CreateSetCommand(IReadOnlyList<object> targets, object? value, EditorDocument? document) {
        ArgumentNullException.ThrowIfNull(targets);

        return new SetValuesCommand(this, targets, [.. targets.Select(Read)], value, document);
    }

    /// <inheritdoc />
    public IEditorCommand CreateRecordCommand(
        IReadOnlyList<object> targets,
        IReadOnlyList<object?> before,
        IReadOnlyList<object?> after,
        string name,
        EditorDocument? document
    ) {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(before);

        return new TransformTargetsCommand(
            name,
            [.. targets.Select(Target)],
            [.. before.Select(state => ((Vector3, Quaternion, Vector3))state!)],
            document
        );
    }

    static IGizmoTarget Target(object owner) =>
        owner as IGizmoTarget
        ?? throw new ArgumentException($"The gizmo's member was handed a '{owner?.GetType()}', which is not a gizmo target.", nameof(owner));
}
